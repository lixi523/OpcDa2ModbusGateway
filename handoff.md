# Handoff Document — OpcDa2Modbus (v2.3.0 + UI 修复)

> 生成时间：2026-09-21 | 基于提交 `4336b2a` (UI 勾选修复) + `f81d6ef` (v2.3.0 审查修复提交)
> 前手版本：`docs/2026-09-16-code-review.md`（V2.2.0 审查，已删除）→ 由 `docs/code-review-v2.3.0.md` 取代
> 工作区当前干净（仅未跟踪发布包 `OpcDaToModbusGateway-v2.3.0.zip`，不入 git）

---

## 1. 项目目标

**OpcDa2Modbus** 是一个 OPC DA → Modbus TCP 网关：
- 连接本地/远程 OPC DA 服务器（通过 TitaniumAS.Opc.Client 1.0.2）
- 将 DA 标签实时映射为 Modbus 寄存器（通过 NModbus 3.0.81）
- 提供 WinForms UI：服务器浏览、标签选择、配置持久化、授权管理、看门狗守护
- 支持 DA 断连降级运行、自动重连、健康监控

---

## 2. 当前进度

**v2.3.0 全项目代码审查报告（`docs/code-review-v2.3.0.md`）修复状态（已提交 `f81d6ef`）：**

| 等级 | 报告项数 | 已修复 | 部分修复 | 未修复 |
|------|---------|--------|----------|--------|
| 🔴 高危 | 5 | 4（#1/#2/#3/#5） | 1（#4） | 0 |
| 🟡 中危 | 15 | 14（#6-#9,#11-#20） | 1（#10） | 0 |
| 🟢 低危 | 23 | — | — | 未处理（按需排期） |

**本轮额外提交**：

| 提交 | 内容 |
|------|------|
| `f81d6ef` | 上述 20 项审查修复全部落地 + `docs/code-review-v2.3.0.md` 报告 |
| `4336b2a` | 点位浏览窗口（`ItemSelectionDialog`）单个点位勾选/取消即时反馈修复（用户功能请求 + 2 轮 OCR 代码审查修正） |

**验证结果**（两个提交后均验证）：
- ✅ `dotnet build`（Debug + Release）— 0 警告 / 0 错误
- ✅ `dotnet test`（Debug + Release）— 33 项单元测试全部通过

> 注：#4 部分修复 = 0.0.0.0 暴露警告 + `AllowedIps` 白名单配置预留 + 防火墙提示；实际拦截依赖防火墙（NModbus slave 网络内部 accept，外部无法插拔）。
> #10 部分修复 = 写入前地址边界/重叠校验已有；Word Swap（ABCD/CDAB/BADC）字序配置未实现。

---

## 3. 已完成修改

### 3.1 v2.3.0 审查修复（提交 `f81d6ef`，17 文件 +733/-318）

#### 高危

| # | 文件 | 修改 |
|---|------|------|
| #1 | `DataBridge.cs` + `Services/GatewayManager.cs` | `DataBridge` 构造函数接受 `null daClient`（降级模式跳过 DA 订阅，Modbus 服务照常启动）；`GatewayManager.StartAsync` DA 失败不再崩溃；新增 `ConnectDaClient()` 在 `CheckHealth` 中 daClient==null 时恢复连接 |
| #2 | `OpcServerScanner.cs` | 引入 `threadCompleted` 标志；仅当 `Join` 成功 **且** 线程标记完成时才合并 `localResults`，超时放弃时跳过合并，消除 STA 线程写非线程安全字典的竞态 |
| #3 | `GatewayModbusTcpServer.cs` | 引入 `ServerState` 状态机（Stopped/Starting/Running）+ `_startStopLock`，Start/Stop/Dispose 全持锁，消除 check-then-act 竞态与资源泄漏 |
| #5 | `Services/LicenseManager.cs` | ① PCID 生成失败置 `_pcidFailed` 进入拒绝态（不启动试用，拦截网关）；② WinForms `Timer` 改 `System.Threading.Timer`；③ 时钟回拨检测 `_maxObservedTrialTimeUtc` |
| #4 | `Models/TagConfig.cs` + `GatewayModbusTcpServer.cs` | `ModbusTcpConfig` 新增 `AllowedIps`；`StartAsync` 监听 0.0.0.0 时输出暴露警告 + 白名单提示 |

#### 中危

| # | 文件 | 修改 |
|---|------|------|
| #6 | `Services/ConfigManager.cs` | `DoSave`/`SaveTagsImmediate`/`SaveAllImmediate` 在文件操作前后置位/复位 `_suppressWatch`，自写不再触发热重载回环 |
| #7 | `OpcDaClient.cs` | `StopReadTimer` 用 `Timer.Dispose(WaitHandle)` 等待回调排空；`_group`/`_server` 读取与置 null 统一入 `_lifecycleLock` |
| #8 | `GatewayModbusTcpServer.cs` | `UpdateValue` 锁内只查 `_tagMap`，编码移到锁外（`WriteToRegister` 改收 `DefaultSlaveDataStore` 参数） |
| #9 | `GatewayModbusTcpServer.cs` | `ListenAsync` 用 `ContinueWith` 监控 Task，监听故障时置 `_isRunning=false` + `_state=Stopped` 并触发 `OnStatusChanged`，消除"假运行" |
| #11 | `OpcDaClient.cs` | `Start` 开头加 `IsConnected` 守卫（已连接再调抛 `InvalidOperationException`）；清掉重复 `<summary>` 死注释 |
| #13 | `Services/LicenseManager.cs` + `MainForm.cs` | 新增 `RefreshStatus()`，MainForm 订阅 `StatusChanged` 后立即调用，消除已授权模式状态栏停留"检测中" |
| #14 | `ServerSelectionDialog.cs` | `RunWorkerCompleted` 回调开头加 `if (IsDisposed \|\| Disposing) return;` |
| #15 | `ItemSelectionDialog.cs` | CSV 导入/确认改用 `Dictionary<string,CsvImportRecord>(OrdinalIgnoreCase)` O(1) 查找，消除 O(n×m) 线性扫描 |
| #16 | `MainForm.cs` | 高频状态事件（`DaStatusChanged`/`ModbusStatusChanged`/`WatchdogStatusChanged`/`RunningStateChanged`）统一改 `SafeBeginInvoke` 异步封送 |
| #17 | `Services/HealthSnapshot.cs` | 每日聚合改单次原子写（tmp + `File.Replace`），加载时按 Date 去重 |
| #18 | `Services/WatchdogManager.cs` | `IsRunning` 持锁 + try/catch；`SignalGracefulExit` 全程持锁；心跳 Timer 用 `Dispose(WaitHandle)` 排空 |
| #19 | `Services/LogManager.cs` | UI 日志改 `StringBuilder` 累积 + 200ms 批量刷新，批量追加后才按需裁剪；删除死代码 |
| #20 | `Services/ConfigManager.cs` + `MainForm.cs` | `Load` 不再直接弹 MessageBox，改记录 `LastLoadError` 属性；MainForm 读取后自行呈现 |

### 3.2 点位浏览窗口单点勾选/取消（提交 `4336b2a`，`ItemSelectionDialog.cs` +6 行）

**背景**：用户要求"OPC DA 点位浏览窗口中，增加单个点位 勾选/取消 的功能"。窗口本就是 `VirtualMode` + `CheckBoxes` + `_checkedItemIds` 数据源 + `RetrieveVirtualItem` 供给模式，单点勾选数据层其实已工作，但缺即时视觉反馈。

**方案演进（经 2 轮 OCR 审查修正）**：
1. 第一版：`ItemCheck` 中直接写 `_listView.Items[e.Index].Checked = e.NewValue` → OCR 指出 virtual mode 下 `Items[e.Index]` 可能为 null（NRE 风险），且 `Items.Count` 仅为已实例化可视行数，守卫对滚出行是无效 no-op
2. 中间版：`e.Item` 属性访问 → 编译错误（`ItemCheckEventArgs` 无 `Item` 属性，只有 `Index`/`CurrentValue`/`NewValue`）；且 `CheckState` 不能隐式转 `bool`
3. **最终版（已提交）**：删掉直接写行属性的冗余块，`ItemCheck` 只更新数据源 `_checkedItemIds` + `_listView.Invalidate()` 触发重绘；`RetrieveVirtualItem` 在重绘时据数据源供给正确 `lvi.Checked`

**为什么不直接改可视行**：虚拟模式下强行改 `Items[e.Index].Checked` 会与 ListView 内部虚拟项缓存竞态（快速滚动时可能闪烁/状态不一致）；而 `Invalidate()` + `RetrieveVirtualItem` 是框架标准的 virtual lifecycle 数据通路。

---

## 4. 关键文件

| 文件 | 角色 | 关键修改 |
|------|------|----------|
| `Services/GatewayManager.cs` | 生命周期/健康检查/重连 | `ConnectDaClient()`、降级路径 |
| `DataBridge.cs` | 数据桥接/快照/转发 | 接受 null daClient（降级） |
| `GatewayModbusTcpServer.cs` | Modbus 服务器 | 状态机 + `AllowedIps` + 锁粒度 + 监听监控 |
| `OpcDaClient.cs` | DA 客户端/订阅/同步读 | 已连接守卫 + Timer 排空 |
| `OpcServerScanner.cs` | OPC 服务器扫描 | 字典竞态收尾 |
| `Services/LicenseManager.cs` | 授权/试用计时 | PCID 拒绝态 + Threading.Timer + 时钟回拨 |
| `Models/TagConfig.cs` | 配置模型 | `AllowedIps` 字段 |
| `Services/ConfigManager.cs` | 配置加载/保存/监视 | `_suppressWatch` 覆盖 + `LastLoadError` |
| `Services/HealthSnapshot.cs` | 健康快照/每日聚合 | 原子写 + Date 去重 |
| `Services/WatchdogManager.cs` | 看门狗管理 | 锁 + Timer 排空 |
| `Services/LogManager.cs` | 双通道日志 | 批量刷新 |
| `MainForm.cs` | WinForms UI | `RefreshStatus` + 异步封送 + 错误呈现 |
| `ItemSelectionDialog.cs` | 点位浏览对话框 | O(1) 查找 + virtual mode 勾选即时反馈（`4336b2a`） |
| `ServerSelectionDialog.cs` | 服务器选择对话框 | 回调防护 |

---

## 5. 不能动的边界

1. **依赖版本锁死**：
   - `NModbus 3.0.81` — 从站默认接受写功能码，slave 网络内部 accept，外部无法插拔白名单（#4 只能靠防火墙）
   - `TitaniumAS.Opc.Client 1.0.2` — API 签名固定（`OpcDaServer`/`OpcDaGroup`/`CanonicalDataType` 等）

2. **COM 单元模型**：
   - OPC DA 所有 COM 调用**必须在 STA 线程**；已在 `GatewayManager` 启动、`ConnectDaClient`、`OpcServerScanner` 枚举中保证

3. **配置原子写入**：
   - `ConfigManager.AtomicWrite()` — 临时文件 + `File.Replace` 模式，**不可改为直接写入**；写文件前后必须置 `_suppressWatch`（#6）

4. **授权算法**：
   - `LicenseAlgorithm` — HMAC 三层 XOR 混淆，内部 TODO 标注"需迁非对称签名"；**勿动**。非对称迁移（Ed25519/RSA/ECDSA）需配套 keygen，属长期方案（#5 的②）

5. **三阶段启动顺序**：
   - `GatewayManager.StartAsync()`：Modbus → DA → Bridge，**严禁调换**；失败逆序回滚。DA 失败时降级运行（Modbus 保持，等待重连）

6. **Virtual ListView 数据通路**（新增，来自 `4336b2a` 经验）：
   - `ItemSelectionDialog` 的勾选状态唯一数据源是 `_checkedItemIds`；`RetrieveVirtualItem` 负责供给行，`ItemCheck` 只更新数据源 + `Invalidate()`
   - **不要**在 `ItemCheck` 中直接写 `Items[e.Index].Checked`（virtual mode 下 NRE 风险 + 与虚拟缓存竞态 + 守卫失效）
   - `ItemCheckEventArgs` 只有 `Index`/`CurrentValue`/`NewValue`，**没有** `Item` 属性

---

## 6. 已经否掉的方案

| 方案 | 否决原因 |
|------|----------|
| `Task.Run` 做 DA 连接 | MTA 线程池违反 COM STA 要求，会抛 `CO_E_NOTINITIALIZED` |
| `Thread.Abort` 强制终止 STA 线程 | 破坏 COM 状态、泄漏非托管资源；已改用协作式取消 + `IsBackground=true` |
| 试用期纯内存 `Stopwatch` | 重启即绕过；已改为持久化 `TrialStartUtc` + 时钟回拨检测 |
| 配置迁移不抑制 watcher | 触发 `ConfigFileChanged` → 重入 `Load()` → 死循环；已全路径 `_suppressWatch` |
| Modbus 白名单在 NModbus 层拦截 | NModbus slave 网络内部 accept 不可插拔；改为配置预留 + 防火墙提示 |
| `ItemCheck` 直接写 `Items[e.Index].Checked` + 范围守卫 | virtual mode 下 `Items.Count` ≈ 可视行数，守卫对滚出行是 no-op；且强改可视行与虚拟项缓存竞态（见 §3.2，OCR 两轮审查否决） |
| `ItemCheckEventArgs.Item` 属性 | 该 API 无此属性（编译 CS1061）；只有 Index/CurrentValue/NewValue |
| `CheckState` 直接赋给 `ItemView.Checked`（bool） | 类型不匹配（CS0029），需 `== CheckState.Checked` 显式转换 |

---

## 7. 当前风险点

| 风险 | 等级 | 说明 |
|------|------|------|
| #4 Modbus 白名单非强制 | 🟡 中 | `AllowedIps` 仅日志提示，实际拦截依赖防火墙；工控现场需配合网络隔离 |
| #10 Word Swap 未实现 | 🟡 中 | Int32/Float 固定 ABCD 大端字序，与 CDAB PLC 对接会得错值；需现场对接时补 |
| #5 授权对称密钥 | 🟡 中 | 三层 XOR + HMAC 仅挡浅层静态分析，非对称迁移待 keygen 配套 |
| 大量标签(5万+)启动时内存峰值 | 🟢 低 | `AddAllItems` 分批 2000 条，已验证可接受 |
| 低严重度 23 项未处理 | 🟢 低 | 按需排期，不影响核心功能 |
| `4336b2a` 仅编译/单测验证 | 🟢 低 | WinForms UI 人工交互路径未走查；建议在测试机跑一次浏览窗口手动勾选验证 |

---

## 8. 已经跑过的测试

```bash
dotnet build    # Debug + Release 均 0 warnings, 0 errors
dotnet test     # Debug + Release 均 33 passed, 0 failed, 0 skipped
```

测试覆盖：
- `ModbusCorrectnessTests` — 编码/解码/地址宽度/类型转换
- `ConfigMigrationTests` — 向后兼容/标签迁移/TagKey 分配
- `OpcQualityTests` — 质量三态分类
- `WatchdogRestartPolicyTests` — 重启策略/退避/上限

> 注：UI 对话框（`ItemSelectionDialog`）无单元测试，`4336b2a` 的修改靠编译 + 既有 33 项测试回归保证，单点勾选行为需人工验证。

---

## 9. 下一步计划（可选，非阻塞）

| 任务 | 优先级 | 说明 |
|------|--------|------|
| 测试机人工验证 `4336b2a` 勾选交互 | P1 | 运行 Release exe → 点位浏览窗口 → 手动勾选/取消单点，确认视觉即时反馈 + 状态栏数量同步 |
| CI 打包步骤加断言：种子 `config.json` ListenAddress == `127.0.0.1` | P1 | 防止 H1 回归 |
| #10 Word Swap 配置项 (ABCD/CDAB/BADC) | P2 | 现场 PLC 对接痛点 |
| `LicenseAlgorithm` 迁移至非对称签名 (Ed25519/RSA/ECDSA) | P2 | 需配套改 Keygen，处理旧授权码兼容 |
| #4 真正的 Modbus 层白名单拦截 | P2 | 需换可插拔网络实现或前置反向代理 |
| 低严重度 23 项按需修复 | P3 | 见 `docs/code-review-v2.3.0.md` 🟢 表；如 CsvHelper 提取、MainForm 拆分、DPAPI 加密授权码 |

**重新打包提醒**：工作区根目录的 `OpcDaToModbusGateway-v2.3.0.zip`（未跟踪，939KB，09-16 生成）已落后于当前代码（不含 `f81d6ef`/`4336b2a`）。发布前需重新打包含 Keygen 隔离检查（见报告"做得好的地方"提醒）。

---

## 10. 新窗口启动提示词

```
请阅读 D:\Documents\Code\OpcDa2Modbus\handoff.md 了解项目现状。
当前 v2.3.0 审查高危 4/5 + 中危 14/15 已修复并提交（f81d6ef），
点位浏览窗口单点勾选/取消即时反馈已修复并提交（4336b2a，经 2 轮 OCR 审查修正，
教训：virtual mode 下 ItemCheck 只更新数据源 _checkedItemIds + Invalidate，勿直接写行属性）。
构建 0 警告 0 错误，测试 33/33 通过（Debug + Release）。
低严重度 23 项未处理，#4/#10 部分修复，#5 非对称签名待 keygen 配套。
根目录 OpcDaToModbusGateway-v2.3.0.zip 已过期，发布前需重新打包。
如需继续开发，请基于此上下文进行。
```
