# OPC DA→UA 网关 — 稳定性与规划

> **目标场景**：工业现场上位机本地读取 OPC DA 标签，映射为 OPC UA 供第三方采集系统使用。
> **核心要求**：7×24 长期稳定运行（≥30 天不中断）。
> **配套文档**：[OPC_DA转UA网关开发指南.md](./OPC_DA转UA网关开发指南.md)（架构与版本历史）。

---

## 0. 文档说明

| 项 | 说明 |
|---|---|
| 用途 | 跟踪面向"工业现场长期稳定运行"的代码状态与后续规划 |
| 维护方式 | 每完成一项修复，更新对应章节并标注 commit/日期 |
| 配套 | 架构与版本演变见开发指南；本文件聚焦"稳定性"维度 |
| 原文件 | 历史版本已备份为 `PLAN.md.bak` |

---

## 1. 稳定性优化记录（原 8 项已全部完成）

按"防崩溃 / 可诊断性 / 性能退化 / 运维友好"4 层框架归档，每项含实现标记、文件位置、备注。

### 1.1 防崩溃层 ✅

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 变量缓存上限保护 | H-33 | `AppConstants.cs:36`, `GatewayOpcUaServer.cs:132-135` | 阈值 100000 节点，超限拒绝添加 |
| DA 重连指数退避 | H-34 | `Services/GatewayManager.cs:71,182,306,335` | 避免 DA 长时不可用时的重连风暴 |
| 日志丢弃持久化告警 | H-35 | `Services/LogManager.cs:155` | 队列满时直接旁路写 `dropped_warnings.log` |

### 1.2 可诊断性层 ✅

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 运行状态快照 | H-36 | `Services/HealthSnapshot.cs` | 24 小时滑动窗口，UP/CPU/内存/标签数 |
| 看门狗异常独立日志 | H-37 | `Watchdog/WatchdogManager.cs:221` | 主日志系统崩溃时仍保留痕迹 |

### 1.3 性能退化防护层 ✅

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| UpdateValue 锁粒度优化 | H-38 | `GatewayOpcUaServer.cs:328` | `ClearChangeMasks` 移到锁外 |
| DataGridView 刷新优化 | H-39 | `MainForm.cs:1089` | 全量 XOR 哈希聚合 + P1-4 自适应间隔 |

### 1.4 运维友好层 ✅

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 配置热加载感知 | H-40 | `Services/ConfigManager.cs:51,66,147,495` | `FileSystemWatcher` + 事件订阅 |

---

## 2. 已完成但未列入原计划的修复

按功能域分类，沿用原计划的 C/H/P/R/N/M 标记体系。

### 2.1 架构增强 [P1]

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 增量配置保存 | P1-1 | `Services/ConfigManager.cs:86,114,274,338,489` | 标签数据分离到 `tags.json` |
| GateController 业务协调层 | P1-2 | `Services/GateController.cs`, `MainForm.cs:701,981,1051` | 从 MainForm 提取生命周期/业务逻辑 |

### 2.2 防崩溃加固 [C/H]

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| UA Server StopAsync 死锁修复 | C-06 | `GatewayOpcUaServer.cs:544,839,856` | Task.Run 切线程池 |
| License 密钥 3 层 XOR 混淆 | C-11 | `Models/LicenseAlgorithm.cs:16,31` | 密钥不再以明文嵌入 IL |
| crash.log 线程安全写入 | C-16 | `Program.cs:138,154` | 加锁防并发损坏 |
| 常数时间比较（防时序攻击） | H-12 | `Models/LicenseAlgorithm.cs:180,203` | UA Server 启动守卫也复用 |
| CreateNode NRE 修复 | H-23 | `GatewayOpcUaServer.cs:164,288` | 改用 AddChild + AddPredefinedNode |
| FlatTags 路由 BUG 修复 | H-24 | `GatewayOpcUaServer.cs:188`, `DataBridge.cs:210` | 无分支时建中间文件夹 |
| Batch 子文件夹分组 | H-24-1 | `GatewayOpcUaServer.cs:53,196,298,375,395,808` | 1000 节点/批，防单文件夹过载 |
| Dispose 防护 | H-30 | `OpcDaClient.cs:313` | `Interlocked.Exchange` 守护定时器回调 |

### 2.3 依赖与稳定性 [H/N]

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 迁移至 TitaniumAS.Opc.Client | H-26 | `OpcDaClient.cs:6-9,17` | 替代商业库 Technosoftware（30 天试用） |
| UA 节点注册顺序 | N-7 | `DataBridge.cs:153` | 按 `_orderedKeys` 遍历，与 DA 扫描一致 |
| TagKey 持久化分配 | N-8 | `Models/TagConfig.cs:55,75,91`, `Services/ConfigManager.cs:139` | 仅对 null/empty 分配，避免重写 |

### 2.4 性能优化 [P/R]

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| 自适应刷新（1s/3s） | P1-4 | `MainForm.cs:50,459,1084` | 快照哈希无变化时放宽间隔 |
| COM Variant 边缘处理 | R-1 | `DataBridge.cs:309,318` | DBNull / COM decimal / 未知类型 |
| 预编译类型转换委托 | R-3 | `DataBridge.cs:58,64,308,326` | 静态构造器填充，O(1) 数组索引 |
| TagSnapshot 值语义哈希 | R-5 | `DataBridge.cs:399,419` | 5 属性共同决定相等性 |

### 2.5 健壮性 [M/R]

| 项 | 标记 | 实现位置 | 备注 |
|---|---|---|---|
| Stopwatch 防时钟篡改 | M2 | `MainForm.cs:65,851,865` | 替代 `DateTime.Now` 差值 |
| async void 重入防护 | M3 | `MainForm.cs:74,1282` | `volatile bool _closeInProgress` |
| ConfigManager 序列化异常恢复 | R-8 | `Services/ConfigManager.cs:298,330` | finally 恢复原始引用 |

---

## 3. 面向长期稳定运行的未来规划

按优先级排列，每项含动机 / 实施要点 / 风险 / 工作量。

### 3.1 [P0] 接口抽象与单元测试

- **动机**：`OpcDaClient` / `GatewayOpcUaServer` / `DataBridge` 直接依赖具体类，无法模拟 DA 断连、数据丢包、质量码异常等场景做回归测试。
- **实施**：
  - 提取 `IOpcDaClient` / `IGatewayOpcUaServer` / `IDataBridge`
  - 建立 `FakeOpcDaClient`（可控注入异常/断连/坏数据）
  - 覆盖：DA 重连风暴、退避生效、订阅丢包、质量码 `Bad` 透传
- **风险**：接口签名需稳定，避免连锁修改。
- **工作量**：3-5 天

### 3.2 [P1] 多 DA 服务器聚合

- **动机**：大型现场常需从多台 DA 数据源汇总；当前仅支持单服务器。
- **实施**：
  - `AppConfig.DaConfigs: List<OpcDaConfig>`
  - `GatewayManager` 持有 N 个 DA Client
  - UA 地址空间前缀区分（`DA1.*` / `DA2.*`）
- **风险**：TagKey 需跨服务器唯一；UI 需支持多源浏览。
- **工作量**：5-7 天

### 3.3 [P1] UA Browse 数据类型增强

- **动机**：浏览 ItemId 时统一标记 `Variant`；实际可从 DA 服务器读取 `canonical_data_type`。
- **实施**：
  - `OpcDaItemInfo` 新增 `CanonicalDataType` 字段
  - `ItemSelectionDialog` 显示真实类型，支持手动覆盖
- **风险**：部分 DA 服务器不返回类型信息，保留 Variant 兜底。
- **工作量**：2 天

### 3.4 [P1] 授权升级 HMAC → ECDSA P-256

- **动机**：HMAC 共享密钥方案在 IL 中可被反编译获取（`LicenseAlgorithm.cs` 已有 TODO）。
- **实施**：
  - 生成 ECDSA 密钥对（管理员持有私钥）
  - 主程序嵌入公钥验签
  - 授权码格式改为 `PCID + 签名`
- **风险**：迁移期需保留旧 HMAC 兼容路径。
- **工作量**：3 天

### 3.5 [P2] 长期内存泄漏检测 ✅ 已完成（2026-07-03）

工业现场 7×24 长期运行下，未被察觉的内存缓慢增长是稳定性杀手（托管堆/非托管句柄/GDI/ComWrap 等都可能）。3.5 在 H-36 健康快照基础上叠加日聚合与增长率告警。

**实现内容**：
- `IHealthSnapshot` 接口扩展：`OnAlert` 事件 + `GenerateDailySummary()` 公开方法
- `HealthSnapshot` 内部新增 `DailySummary` 类（17 个字段）+ `AppendDailySummary` / `ComputeGrowthRate` / `RotateDailyFile` 3 个方法
- `Capture()` 末尾增加聚合时间检查（凌晨 00:05~00:10 窗口）+ `_captureLock` 防 5 分钟定时器重入
- `MainForm` 直接订阅 `_healthSnapshot.OnAlert` 事件（无中间层）；`MainForm.ShowMemoryAlert` 更新状态栏颜色（黄色 20%、红色 50%）
- 数据存储：`health/health_daily.jsonl`（追加 O(1)，90 天轮转）
- 告警阈值：7天/30天 工作集增长 ≥ 20% 黄色、≥ 50% 红色

**关键文件**：
- `Services/HealthSnapshot.cs`（内存监控 + 日聚合；后由 ponytail R6 内联精简）
- `Services/Interfaces/IHealthSnapshot.cs`（含 `OnAlert` 事件 + `GenerateDailySummary()`）
- `MainForm.cs` `ShowMemoryAlert` 方法 + 直接订阅 `_healthSnapshot.OnAlert`

**验收**：
- ✅ 编译 0 错误 0 警告
- ✅ 现有 health/health_*.json 不变（向后兼容）
- ✅ 凌晨 00:05 自动聚合昨日数据
- ✅ 20% 触发黄色、50% 触发红色状态栏
- ✅ 90 天 jsonl 自动轮转
- ✅ 幂等保护：同一天只写一次

> **后续变更（ponytail 精简，2026-07-07）**：原 3.5 实现依赖的 `Services/GateController.cs` 事件透传中间层，已在 ponytail R1 中删除（`GateController` 仅做事件透传、无业务逻辑）。内存告警链路改为 `MainForm` 直接订阅 `HealthSnapshot.OnAlert`，功能不变。`HealthSnapshot` 内部 4 个私有方法也在 R6 中内联，代码行数回落。当前版本 V1.5.0。

### 3.6 [P2] 故障恢复场景预案

- **动机**：现场故障恢复需要明确操作手册。
- **实施**：文档化以下场景的恢复步骤：
  - DA 服务器更换（配置修改 + 网关重启）
  - 网络分区（依赖指数退避 + 看门狗）
  - 证书过期（UA Server 自动重新生成，需重做客户端信任）
  - 看门狗循环重启（heartbeat 持续失败）
- **风险**：纯文档工作，零代码风险。
- **工作量**：1 天
- **状态**：✅ 已完成（2026-07-11）。交付独立文档 `故障恢复预案.md`，覆盖 S1 DA 服务器更换、S2 DA 断连/网络分区、S3 UA 服务异常、S4 UA 证书过期/不受信任、S5 看门狗循环重启、S6 授权失效/试用到期、S7 内存增长告警、S8 配置损坏、S9 程序崩溃，并附应急快速定位清单与关键机制对照表。每条恢复步骤均锚定代码事实（指数退避重连 ≤50 次、看门狗 60s/3 次重启保护、证书自动重生成、配置原子写等）。同步新增配套 `使用文档.md`（用户手册）。

---

## 4. 假设与前提

| 项 | 值 |
|---|---|
| 生产环境标签量 | 1000 ~ 50000 范围 |
| 部署形态 | 单机 Windows 7/10/11 |
| DA 连接方式 | 远程 DCOM（网络可能不稳定） |
| 运行目标 | 7×24 不中断 ≥ 30 天 |
| 第三方采集 | 通过 OPC UA 拉取数据 |
| UA 客户端数 | ≤ 5 个并发 |
