# 全项目代码审查报告

> 审查时间：2026-09-16
> 版本：V2.2.0（HEAD `c62027c`，main）
> 范围：全项目（网关数据链路、生命周期管理、看门狗、配置、授权、UI、构建/CI）
> 依赖核对：NModbus 3.0.81、TitaniumAS.Opc.Client 1.0.2

---

## 总体评估

代码质量整体较高：生命周期管理（三阶段启动 + 逆序回滚）、COM 资源释放、原子写配置、看门狗状态机都有系统性加固，注释详尽，测试覆盖核心正确性（33 项）。但存在 **1 个高危安全一致性缺陷** 和若干中危正确性/并发问题，多与"DA 断连降级 + 重连"这一被明确支持的路径有关。

关键依赖的运行时行为核对结论：
- NModbus 3.0.81 的 `PointSource<T>` 内部用 `lock` 保护读写（跨线程读写安全），其从站**默认接受写功能码**（无 `AllowWrites` 之类开关）。
- TitaniumAS.Opc.Client 1.0.2 中 `OpcDaServer`/`OpcDaGroup`/`OpcDaItemValue`/`CanonicalDataType`/`OpcDaBrowserAuto` 等类型均存在，API 用法与库一致。

---

## 🔴 高危

### H1. 种子 `config.json` 仍为 `0.0.0.0`，架空了 P1-2 的安全默认值

- **位置**：`config.json:10`（`"ListenAddress": "0.0.0.0"`）
- **问题**：handoff §3.7 声明 P1-2 已把默认监听从 `0.0.0.0` 改为 `127.0.0.1`，并在 `GatewayModbusTcpServer`/`ConfigManager`/`TagConfig`/`GetEndpointUrl`/`MainForm` 五处同步。但**随包发布的种子 `config.json`（CI 打包直接带出）仍显式写死 `0.0.0.0`**。`ApplyBackwardCompatDefaults()` 只在 `string.IsNullOrEmpty(ListenAddress)` 时填默认值（`ConfigManager.cs:219`），而文件里已有非空值，安全默认**永远不会生效**。
- **放大因素**：已核实 NModbus 3.0.81 从站**默认接受写功能码**（无 `AllowWrites` 之类的开关）。因此出厂状态下，**同一网段任意主机既能读又能写全部寄存器**——既可被窃听数据，也可被远程篡改。
- **修复**：把 `config.json` 的 `ListenAddress` 改为 `"127.0.0.1"`，与代码默认值对齐；需要外访的部署再显式改为 `0.0.0.0`/内网 IP。建议在 CI 打包步骤加一条断言，防止种子配置与代码默认值漂移。

---

## 🟠 中危

### M1. DA 断连降级启动后重连，未重新做地址重叠校验 → 静默寄存器串写

- **位置**：`GatewayManager.cs:158/196`（仅初始启动调用 `ValidateMappings`）；重连路径 `CheckHealth → TryReconnect (OpcDaClient.cs:483) → Start → AddAllItems → OnConfigChanged → DataBridge.RegisterModbusNodes (DataBridge.cs:99-105)` **全程不再校验**。
- **问题**：初始启动若 DA 连不上，类型仍是 `Variant`/不可解析，`ValidateMappings` 按**宽度 1 保守校验**（`TagConfig.cs:109-112`）。随后 DA 重连成功，`AddAllItems` 回写真实类型（如 Int32=宽度 2），`RegisterModbusNodes` 按真实宽度重新注册，**但没有任何地方再次跑 `ValidateMappings`**。
  - 例：两个 Int32 标签地址 0、1。降级时按宽度 1 校验无重叠、通过；重连后真实占用 `tag0=[0,1]`、`tag1=[1,2]`，地址 1 重叠，`UpdateValue` 交错写高/低 word → **数据静默损坏**，且无告警。
- **修复**：在 `DataBridge.OnClientConfigChanged`（类型回写补注册后）触发一次映射重校验，或在 `GatewayManager` 的重连成功回调里复用 `ValidateMappings`；发现重叠时降级（跳过冲突标签 + 告警）而非静默覆盖。

### M2. `GatewayModbusTcpServer.VariableCount` 读 `Dictionary.Count` 未持锁，与写并发不安全

- **位置**：`GatewayModbusTcpServer.cs:47`（`public int VariableCount => _tagMap.Count;`）
- **问题**：`_tagMap` 是普通 `Dictionary`，写（`AddVariableNode`/`Dispose`）在 `_lock` 内进行，但 `VariableCount` **不加锁**。它被 `HealthSnapshot.Capture`（定时器线程，`HealthSnapshot.cs:121`）和导出流程访问，与 COM/UI 线程的 `AddVariableNode` 并发时，`Dictionary.Count` 读可能抛 `InvalidOperationException` 或读到撕裂值。
- **修复**：`VariableCount` 也走 `lock (_lock) { return _tagMap.Count; }`，或把 `_tagMap` 换成并发安全结构 / 用 `Interlocked` 维护一个计数。

### M3. `StartAsync` 名义异步，实际阻塞 UI 线程完成 DA 连接 + 加点位

- **位置**：`GatewayManager.cs:141`（`async Task StartAsync`），`daClient.Start(...)` 在 `:182`；调用方 `MainForm.BtnStart_Click` 为 `async void`（`MainForm.cs:870/910`）。
- **问题**：`StartAsync` 内唯一的 await（`modbusServer.StartAsync()`）实为同步完成，`daClient.Start()` 是阻塞 COM 调用（连接 + 按 2000/批 `AddItems` 最多 5 万点位）。整段在 **UI 线程**执行，窗口在启动期间冻结数秒到数十秒。`progressReport` 用 `SynchronizationContext.Post` 投递，但因当前就卡在 UI 线程，进度**渲染不出来**，直到阻塞结束才一次性刷出——进度回调形同虚设。
- **修复**：把 DA 连接/加点位移入 `Task.Run`（注意 COM STA 约束，需 `Task.Run` 内自建 STA 或用已有的 STA 封装），或至少把 `AddAllItems` 的批次进度真正异步化，保证 UI 可响应。

### M4. 试用期计时未持久化，重启即可重置 30 分钟

- **位置**：`LicenseManager.StartTrialTimer`（`LicenseManager.cs:82`，`_trialStopwatch = Stopwatch.StartNew();`）
- **问题**：试用时长仅存在于内存 `Stopwatch`，**没有把开始时间/剩余时间持久化**到 config/注册表。未授权用户每次重启程序都能拿到全新 30 分钟，等效无限试用，授权约束被轻易绕过。
- **修复**：持久化"试用起始 UTC 时间戳"（或剩余秒数）到 config.json，启动时据此计算剩余；若确属"宽松试用"的有意设计，请在此补一句注释说明，避免后续误判为漏洞。

### M5. `DoSyncRead` 可能重复投递数据 + 内层静默吞异常

- **位置**：`OpcDaClient.cs:364-414`（尤其 `:387` `group.Read` 与 `:403` 手动 `OnDataChanged`，`:407` 空 `catch {}`）
- **问题**：类注释（`:44-45`）自述"`group.Read()` 会触发 `ValuesChanged`"。若属实，异步模式下每 5 分钟同步读会对每个标签**投递两次** `OnDataChanged`（事件一次 + 手动循环一次）：值因"同值覆盖"不出错，但会**翻倍 Modbus 写流量**并使 UI 的"更新"计数（`_totalUpdates`）虚高。`:407` 的 `catch { }` 完全静默，单条点位异常无任何痕迹。
- **修复**：确认 `Read` 是否真的触发订阅事件；若是，去掉手动投递（只留事件）或去掉事件订阅（只留手动），二选一。`:407` 至少补一行日志（与外层 `:412` 风格一致）。

---

## 🟡 低危

| # | 位置 | 问题 | 建议 |
|---|------|------|------|
| L1 | `DataBridge.cs:153` vs `GatewayModbusTcpServer.cs:133` | `_lastUpdateTime` 用 `DateTime.Now`，而 `mapping.LastTimestamp` 用 `DateTime.UtcNow`，本地/UTC 混用（仅展示，但易误导排障） | 统一为 `DateTime.UtcNow` 或统一本地时间 |
| L2 | `ConfigManager.cs:168-182` | `Load()` 内部迁移会 `SaveAllImmediate()` 写 config.json，再次触发文件监视 → 多跑一次 `Load`。虽自限（第二次 `keysAssigned=false`/`tags.json` 已存在），但脆弱 | 迁移写盘前先临时 `EnableRaisingEvents=false`，写完恢复；或用"自写标志"抑制自身触发 |
| L3 | `Watchdog/Program.cs:233` | 按进程名 `GetProcessesByName` 可能误杀同名不同路径进程（注释已自认风险） | 若可行，结合可执行文件全路径（`p.MainModule.FileName`）比对，降低误杀面 |
| L4 | `MainForm.cs:166` | `Config.OpcDa.Mode = ... == 1 ? "Sync" : "Async"` 直接以 ComboBox 下标映射，下标与选项顺序强耦合，调整选项顺序即出错 | 用枚举显式映射而非下标 |
| L5 | `OpcServerScanner.cs`（COM/注册表扫描段） | 本段未逐行深审；STA 线程上的 COM 枚举需确认枚举器/对象在异常路径也释放 | 复核 `RunComEnumerationOnStaThread` 的 COM 对象 try/finally 释放 |

---

## 四个维度小结

- **Bug / 逻辑**：M1（重连后重叠串写）、M5（重复投递 + 计数虚高）是真实可复现的；H1 属配置与代码语义不一致。
- **安全**：H1 是最需要立即处理的（网络暴露 + 默认可写）。授权侧 M4 削弱了试用约束。`LicenseAlgorithm` 的 HMAC 三层 XOR 混淆仅挡浅层静态分析，类内 TODO 已诚实标注"需迁非对称签名"，方向正确。
- **性能**：`VariableCount` 无锁读（M2）在高频 HealthSnapshot 下有隐患；`DoSyncRead` 5 万点位的 COM 全量读在后台线程可接受。
- **可读性 / 可维护性**：整体良好，注释密度高、职责清晰。主要扣分点是若干"自述行为"（如 M5 的注释断言）与实际运行路径需再核对；`OpcServerScanner` 的多策略 + 大量诊断日志偏长，但结构清晰。

---

## 建议处理优先级

1. **立即**：H1（改 `config.json` 为 `127.0.0.1` + CI 断言）。
2. **本迭代**：M1（重连后重校验重叠）、M2（`VariableCount` 加锁）、M3（启动阻塞 UI）。
3. **排期**：M4（试用持久化）、M5（去重投递 + 补日志）。
4. **顺手**：L1–L5。
