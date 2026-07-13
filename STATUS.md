# OPC_DA转UA网关 — 项目状态报告

**生成时间：** 2026-07-04 21:46  
**当前版本：** 1.5.0  
**编译状态：** 0 警告 0 错误 ✅

---

## 1. 当前目标

对 OpcDaToUaGateway 项目进行代码瘦身与结构优化（ponytail 8 项），消除过度工程、冗余代码，提升可维护性。

**状态：全部执行完毕，编译通过，版本号已升级到 1.5.0。**

---

## 2. 已经完成的修改

### ponytail-review（过度工程清理）

| 操作 | 净减 |
|---|---|
| 删除 `GatewayFactory.cs`（56 行），工厂调用改为直接 `new` | -56 |
| `HealthSnapshot` 嵌套类移至 `Models/`；内存缓存替代文件重读 | -89 |

### ponytail（代码精简 8 项）

| 项目 | 操作 | 净减 |
|---|---|---|
| R1 删 GateController | 删除 `Services/GateController.cs`（150 行），事件透传反模式根除 | -150 |
| R2 提取 LicenseManager | 新建 `Services/LicenseManager.cs`，封装授权/试用/事件通知 | -100 |
| R3 SafeInvoke | MainForm 提取 `SafeInvoke(Action)` 方法，替换 4 处重复模式 | -30 |
| R4 DataBridge 裁剪 | 删 `_cachedDisplayName` 字典、`TagSnapshot.Equals/GetHashCode`、7 条诊断日志 | -35 |
| R5 LogManager 减脂 | 删注释块、字段注释、死 catch（`OperationCanceledException`） | -80 |
| R6 HealthSnapshot 内联 | 内联 `CountStatusFlips`/`TrimDailyCache`/`RewriteDailyFile`/`ComputeGrowthRate` | -60 |
| R7 诊断 DEBUG 包裹 | `Diag()` 标记 `[Conditional("DEBUG")]`，Release 自动移除 | -30 |
| **合计** | | **≈ -485 行** |

---

## 3. 关键文件

| 文件 | 状态 | 说明 |
|---|---|---|
| MainForm.cs | 已修改 | 授权委托 `LicenseManager`；事件直连 `_gatewayMgr`；SafeInvoke 通用化 |
| Services/GatewayManager.cs | 已修改 | 新增 `RunningStateChanged` 事件；移除 `_factory` 字段 |
| Services/LicenseManager.cs | 新增 | 独立授权管理器：PCID 生成、授权码验证、Stopwatch 试用、状态事件 |
| Services/DataBridge.cs | 已修改 | 删除 `_cachedDisplayName` 字典和未使用的 `Equals`/`GetHashCode` |
| Services/LogManager.cs | 已修改 | 注释减脂；删除死 catch |
| Services/HealthSnapshot.cs | 已修改 | 内联 4 个私有方法；注释 90→30 行 |
| Models/SnapshotData.cs | 已存在 | 从 HealthSnapshot 嵌套类移出 |
| Models/DailySummary.cs | 已存在 | 从 HealthSnapshot 嵌套类移出 |
| Services/GatewayFactory.cs | 已删除 | YAGNI — 工厂层只有默认实现且被强转绕过 |
| Services/GateController.cs | 已删除 | 事件透传反模式，5 个事件均为 `=> OtherEvent?.Invoke(...)` |

---

## 4. 不能动的边界

| 文件 | 原因 |
|---|---|
| GatewayOpcUaServer.cs | OPC UA 服务器核心逻辑，修改风险极高 |
| OpcDaClient.cs | OPC DA COM 客户端，线程安全和生命周期管理敏感 |
| ConfigManager.cs | 配置管理，未纳入优化范围 |
| Services/WatchdogManager.cs | 看门狗进程管理，仅通过事件交互 |
| Models/LicenseAlgorithm.cs | 授权算法（PCID + HMAC），修改导致已授权用户失效 |
| Theme.cs / AppConstants.cs | 设计系统常量，与代码优化无关 |
| Watchdog/ 目录 | 看门狗独立项目 |
| Keygen/ 目录 | 授权码生成工具 |

---

## 5. 已经否掉的方案

| 方案 | 否定原因 |
|---|---|
| 工厂模式（GatewayFactory） | 只有默认实现，`StartAsync` 内强转回具体类型，抽象被完全绕过 |
| GateController 中间层 | 5 个事件均为透传，零业务逻辑 |
| DataBridge 接口全量保留 + 强转 | "半吊子抽象"不如不用 |
| TagSnapshot 值语义（Equals/GetHashCode） | 全项目无使用场景 |
| LogManager `WriterLoop` 内联注释 | 注释量：代码量 ≈ 2:1，冗余 |
| 诊断日志全量保留 | 生产环境无法操作，仅调试期有用 |

---

## 6. 已经跑过的命令和测试结果

| 命令 | 结果 |
|---|---|
| `dotnet build OpcDaToUaGateway.csproj -c Debug` | 0 警告 0 错误（R1-R7 每轮修改后均执行） |
| 文件存在性检查 | `GateController.cs`、`GatewayFactory.cs` 已物理删除 |
| 引用搜索 | 全项目 0 引用已删除文件 |
| Watchdog/Keygen 编译 | 通过 `.csproj` Target 自动触发，编译成功 |

**注意：未执行运行时测试**（无 OPC DA 环境）。编译通过 ≠ 运行时行为正确。

---

## 7. 当前风险点

| 风险 | 等级 | 说明 |
|---|---|---|
| 事件订阅泄漏 | 🟡 中 | MainForm 直连订阅，OnFormClosing 需确认取消订阅 |
| SafeInvoke 使用 `Invoke` 而非 `BeginInvoke` | 🟡 中 | 与原 `LogManager` 不一致，特定场景可能死锁 |
| LicenseManager 试用到期处理 | 🟡 中 | `Close()` 需在 UI 线程执行，当前通过 `SafeInvoke` 包装 |
| 未运行时测试 | 🔴 高 | OPC DA 实际环境未验证事件转发链 |
| [Conditional("DEBUG")] 排障信息丢失 | 🟡 中 | Release 模式无地址空间初始化详情 |

---

## 8. 下一步计划

**当前无明确待办项。** PLAN.md 第 3 节（未来规划）6 个方向：

| 方向 | 状态 |
|---|---|
| 3.1 接口抽象与单元测试基础设施 | 已完成 |
| 3.5 长期内存监控 | 已完成 |
| 3.2 遥测历史（JSON Lines 持久化） | 待评估 |
| 3.3 告警历史 | 待评估 |
| 3.4 脚本钩子 | 待评估 |
| 3.6 配置校验 | 待评估 |

**建议**：
1. 在实际 OPC DA 环境中启动 V1.5.0，验证事件链路
2. 如需进一步优化：优先评估 3.2（遥测历史），health/ 目录已有基础
3. 如需新增功能：按后续指令执行
