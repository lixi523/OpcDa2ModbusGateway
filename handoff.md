# Handoff Document — OpcDa2Modbus Code Review Fixes

> 生成时间：2026-09-16 | 基于提交 `c62027c` (V2.2.0)

---

## 1. 项目目标

**OpcDa2Modbus** 是一个 OPC DA → Modbus TCP 网关：
- 连接本地/远程 OPC DA 服务器（通过 TitaniumAS.Opc.Client 1.0.2）
- 将 DA 标签实时映射为 Modbus 寄存器（通过 NModbus 3.0.81）
- 提供 WinForms UI：服务器浏览、标签选择、配置持久化、授权管理、看门狗守护
- 支持 DA 断连降级运行、自动重连、健康监控

---

## 2. 当前进度

**代码审查报告（`docs/2026-09-16-code-review.md`）全部 11 项问题已修复：**

| 等级 | 数量 | 状态 |
|------|------|------|
| 🔴 高危 | 1 | ✅ 完成 |
| 🟠 中危 | 5 | ✅ 完成 |
| 🟡 低危 | 5 | ✅ 完成 |

**验证结果**：
- ✅ `dotnet build` — 0 警告 / 0 错误
- ✅ `dotnet test` — 33 项单元测试全部通过

---

## 3. 已完成修改

### H1 (Critical) — 种子配置暴露全网
- **文件**：`config.json:10`
- **修改**：`"ListenAddress": "0.0.0.0"` → `"127.0.0.1"`
- **原因**：出厂默认需安全；需外访时由用户显式改回

### M1 — 重连后未重新校验地址重叠
- **文件**：`Services/GatewayManager.cs:419-428`
- **修改**：`CheckHealth()` 重连成功后调用 `ValidateMappings(_config.OpcDa?.Tags)`
- **效果**：防止降级启动(宽度1)通过校验 → 重连后真实类型(宽度2)导致静默寄存器串写

### M2 — VariableCount 并发不安全
- **文件**：`GatewayModbusTcpServer.cs:47-56`
- **修改**：属性改为 `lock (_lock) { return _tagMap.Count; }`

### M3 — 启动阻塞 UI 线程 + STA 问题
- **文件**：`Services/GatewayManager.cs:170-197`
- **修改**：DA 连接移至**专用 STA 线程**（`Thread.SetApartmentState(STA)` + `TaskCompletionSource`），非 `Task.Run`（MTA）
- **关键**：OPC DA COM 组件要求 STA，`Task.Run` 会导致 `CO_E_NOTINITIALIZED`

### M4 — 试用期重启即重置
- **文件**：`Models/TagConfig.cs` (新增 `TrialStartUtc`)、`Services/LicenseManager.cs` (全面重写)
- **修改**：首次试用写入 UTC 起始时间到 `config.json`，授权成功时清空，启动时按已用时长计算剩余

### M5 — DoSyncRead 重复投递 + 静默吞异常
- **文件**：`OpcDaClient.cs:369-418`
- **修改**：
  - 引入 `volatile DaAcquisitionMode _lastMode`
  - 异步模式下 `group.Read()` 触发 `ValuesChanged` 事件，跳过手动 `OnDataChanged` 投递
  - 同步模式保留手动投递
  - 空 `catch {}` 补日志

### L1 — 时间 UTC/Local 混用
- **文件**：`DataBridge.cs:153`
- **修改**：`_lastUpdateTime = DateTime.Now` → `DateTime.UtcNow`

### L2 — 配置迁移触发文件监视重入
- **文件**：`Services/ConfigManager.cs` (新增 `_suppressWatch` 标志)
- **修改**：`Load()` 中 `SaveAllImmediate()` 前后设置/清除标志，`Changed` 事件内首行判断跳过

### L3 — 看门狗进程名误杀
- **文件**：`Watchdog/Program.cs:230-249`
- **修改**：`GetProcessesByName` 后逐个比对 `MainModule.FileName` 与当前进程路径

### L4 — ComboBox 下标硬编码
- **文件**：`MainForm.cs:162-171`
- **修改**：`Dictionary<int, string> daModeMap = { [0]="Async", [1]="Sync" }` 显式映射

### L5 — COM 枚举释放确认
- **文件**：`OpcServerScanner.cs`
- **结论**：已正确 — `finally` 块中 `Marshal.ReleaseComObject(serverList/enumerator)`，线程用协作式取消而非 `Thread.Abort`

---

## 4. 关键文件

| 文件 | 角色 | 关键修改行 |
|------|------|------------|
| `config.json` | 种子配置 | 10 |
| `Services/GatewayManager.cs` | 生命周期/健康检查/重连 | 170-197, 419-428 |
| `GatewayModbusTcpServer.cs` | Modbus 服务器/寄存器映射 | 47-56 |
| `OpcDaClient.cs` | DA 客户端/订阅/同步读 | 65, 369-418 |
| `DataBridge.cs` | 数据桥接/快照/转发 | 153 |
| `Services/LicenseManager.cs` | 授权/试用计时 | 全文重写 |
| `Models/TagConfig.cs` | 配置模型 | 新增 `TrialStartUtc` |
| `Services/ConfigManager.cs` | 配置加载/保存/监视 | 61, 170-191 |
| `Watchdog/Program.cs` | 看门狗进程监控 | 230-249 |
| `MainForm.cs` | WinForms UI | 162-171 |
| `OpcServerScanner.cs` | OPC 服务器扫描 | 无需改（已验证） |

---

## 5. 不能动的边界

1. **依赖版本锁死**：
   - `NModbus 3.0.81` — 从站默认接受写功能码，无 `AllowWrites` 开关
   - `TitaniumAS.Opc.Client 1.0.2` — API 签名固定（`OpcDaServer`/`OpcDaGroup`/`CanonicalDataType` 等）

2. **COM 单元模型**：
   - OPC DA 所有 COM 调用**必须在 STA 线程**；已在 `GatewayManager` 启动、`OpcServerScanner` 枚举中保证

3. **配置原子写入**：
   - `ConfigManager.AtomicWrite()` — 临时文件 + `File.Replace` 模式，**不可改为直接写入**

4. **授权算法**：
   - `LicenseAlgorithm` — HMAC 三层 XOR 混淆，内部 TODO 标注"需迁非对称签名"，**勿动**

5. **三阶段启动顺序**：
   - `GatewayManager.StartAsync()`：Modbus → DA → Bridge，**严禁调换**；失败逆序回滚

---

## 6. 已经否掉的方案

| 方案 | 否决原因 |
|------|----------|
| `Task.Run` 做 DA 连接 | MTA 线程池违反 COM STA 要求，会抛 `CO_E_NOTINITIALIZED` |
| `Thread.Abort` 强制终止 STA 线程 | 破坏 COM 状态、泄漏非托管资源；已改用协作式取消 + `IsBackground=true` |
| 试用期纯内存 `Stopwatch` | 重启即绕过；已改为持久化 `TrialStartUtc` |
| 配置迁移不抑制 watcher | 触发 `ConfigFileChanged` → 重入 `Load()` → 死循环风险；已加 `_suppressWatch` |
| `ValidateMappings` 仅初始启动跑一次 | 重连后真实类型回写会改变地址宽度，必须再跑一次 |

---

## 7. 当前风险点

| 风险 | 等级 | 说明 |
|------|------|------|
| STA 线程异常未传播到主线程 | 🟡 中 | `TaskCompletionSource` 只捕获 `TrySetException`，若 STA 线程内未 catch 的异常会导致进程崩溃；当前 `try/catch` 全覆盖，风险可控 |
| 试用期系统时间回拨 | 🟡 中 | `DateTime.UtcNow - _trialStartUtc` 受系统时间影响；可接受（工控机通常禁用时间同步或锁定 BIOS 时间） |
| 大量标签(5万+)启动时内存峰值 | 🟢 低 | `AddAllItems` 分批 2000 条，已验证可接受 |
| 看门狗 `MainModule.FileName` 访问权限 | 🟢 低 | 某些进程无权限读取模块路径会抛异常；已 `try/catch` 忽略单个进程 |

---

## 8. 已经跑过的测试

```bash
dotnet build    # 0 warnings, 0 errors
dotnet test     # 33 passed, 0 failed, 0 skipped
```

测试覆盖：
- `ModbusCorrectnessTests` — 编码/解码/地址宽度/类型转换
- `ConfigMigrationTests` — 向后兼容/标签迁移/TagKey 分配
- `OpcQualityTests` — 质量三态分类
- `WatchdogRestartPolicyTests` — 重启策略/退避/上限

---

## 9. 下一步计划（可选，非阻塞）

| 任务 | 优先级 | 说明 |
|------|--------|------|
| CI 打包步骤加断言：种子 `config.json` ListenAddress == `127.0.0.1` | P1 | 防止 H1 回归 |
| `LicenseAlgorithm` 迁移至非对称签名 (Ed25519/RSA) | P2 | 当前 HMAC-XOR 仅挡浅层静态分析 |
| `OpcServerScanner` COM 枚举补充单测 | P3 | 现有测试未覆盖扫描器 |
| UI 增加"导出配置/导入配置"菜单 | P4 | 便于现场部署复制配置 |

---

## 10. 新窗口启动提示词

```
请阅读 D:\Documents\Code\OpcDa2Modbus\handoff.md 了解项目现状。
当前所有代码审查问题已修复，构建/测试通过。
如需继续开发，请基于此上下文进行。
```