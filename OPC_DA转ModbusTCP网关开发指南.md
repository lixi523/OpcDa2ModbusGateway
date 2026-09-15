# OPC DA 转 Modbus TCP 网关开发指南

**版本：2.2.0**

---

## 1. 文档范围与 V2.2.0 版本边界

本文档以 V2.2.0 当前源码与 33 项 MSTest 回归测试为事实源，完整描述 `OpcDaToModbusGateway` 网关的架构、数据通路、配置格式、CSV 硬约束、看门狗协议与构建部署方式。

**版本边界：** 正文全部描述 V2.2.0 的实际行为，不包含 V1.x 旧实现描述（例如旧的 UA 残留、旧的 8 列点表导出格式、TagKey 不持久化等均已清除）。各版本差异仅在「第 13 章 完整版本历史」中保留。

**V2.2.0 的核心变更（相对 V2.1.0）：**

- P0 端到端验证：真实 OPC DA 服务器（Knight.OPC.Server.Demo）+ Modbus TCP 客户端 42 标签全量读验证，各类型数据转发正确；
- P1-1 发布包隔离：Keygen 源码移出仓库（本地保留 + `.gitignore`），主程序默认构建/CI 客户发布包不含 Keygen；
- P1-2 Modbus 网络边界：默认监听地址 `0.0.0.0` → `127.0.0.1`（仅本机回环，外部访问需显式配置）；
- 提交 `docs/archify` 架构图。

**V2.1.0 的核心变更（相对 V2.0.0）：**

- CSV 映射/点表导入统一跳过全逗号分隔空行，修复表头误判导致的导入错位与幽灵记录；
- 删除点表浏览对话框遗留死代码；
- 恢复 Windows CI（restore → build → test → 打包发布）；
- net472/x86 MSTest 回归测试增至 33 项。

**V2.0.0 里程碑（数据正确性与可靠性，V2.1.0 延续）：**

- Modbus 声明类型、寄存器宽度与高 word 编码统一，自动地址按四个地址空间分配并校验；
- 配置迁移原子保存并加固热重载；
- OPC Quality 正确传播（helper 识别 Good/Uncertain/Bad 三态），转换与 Modbus 写入显式报告失败；
- DA/Modbus 快照分离，监控值与实际寄存器状态一致；
- 修复看门狗优雅退出、手工启动后重新武装与配置 watcher 并发问题；
- net472/x86 MSTest 回归测试增至 33 项。

---

## 2. 运行环境、四项目组成和部署产物

### 2.1 运行环境

- **目标平台：** .NET Framework 4.7.2（`net472`）
- **进程位宽：** x86（`PlatformTarget=x86`）。OPC DA 服务器是 32 位 COM 进程外服务器（LocalServer32），64 位进程无法直接调用 32 位 COM 组件，必须以 x86 运行确保 COM 调用在同一进程位宽下完成。
- **操作系统：** Windows 7 / 8 / 8.1 / 10 / 11 或 Windows Server（清单声明 `supportedOS`）。
- **权限：** `app.manifest` 声明 `requestedExecutionLevel="requireAdministrator"`，要求以管理员权限运行（确保 OPC DA COM 组件与 Modbus TCP 端口 502 可正常访问）。
- **前置条件：** 目标 OPC DA 服务器的 COM 组件已正确注册（32 位注册表视图）。

### 2.2 项目组成

解决方案 `OpcDaToModbusGateway.sln` 包含 **三个** 项目：

| 项目 | 输出 | 类型 | 用途 |
|---|---|---|---|
| OpcDaToModbusGateway | OpcDaToModbusGateway.exe | WinForms（WinExe） | 主程序：UI、DA 客户端、Modbus TCP 服务器、数据桥接、授权管理、看门狗管理 |
| OpcDaToModbusGateway.Watchdog | OpcDaToModbusGateway.Watchdog.exe | WinExe（无 UI） | 独立进程：监控主程序并自动重启（崩溃/挂起） |
| OpcDaToModbusGateway.Tests | 测试程序集 | MSTest | net472/x86 单元测试，33 项，覆盖编码、质量、配置迁移、看门狗重启策略 |

**Keygen 授权码计算工具源码不随仓库分发（本地维护，`.gitignore` 排除），不在解决方案中。**

**注意：** `.sln` 中 Watchdog 项目仅保留 `ActiveCfg`（无 `Build.0` 行），避免解决方案级别重复构建；由主项目的 MSBuild Target 在编译前自动构建。

### 2.3 部署产物

主项目编译（`dotnet build OpcDaToModbusGateway.csproj -c Release`）会自动触发 Watchdog 构建并复制 exe 到主输出目录（Keygen 已移除出仓库，不再自动构建）。最终部署清单：

```
OpcDaToModbusGateway.exe              # 主程序（Costura.Fody 已嵌入全部托管依赖 DLL）
OpcDaToModbusGateway.exe.config       # .NET 运行时声明（必须随 exe 部署）
OpcDaToModbusGateway.Watchdog.exe     # 看门狗进程（与主程序同目录）
config.json                           # 网关配置（需随程序一起部署）
tags.json                             # 标签配置（首次启动迁移后生成）
app.ico                               # 应用图标
```

可选文件：`OpcDaToModbusGateway.pdb`（调试符号，生产环境可省略）。

---

## 3. 总体架构、组件依赖和三阶段启动

### 3.1 整体数据流

```
┌──────────────────────────────────────────────────────────────┐
│                    OpcDaToModbusGateway.exe                   │
│                                                              │
│  ┌──────────┐   ┌────────────┐   ┌──────────────────┐        │
│  │ OPC DA   │──▶│ OpcDaClient│──▶│ DataBridge       │        │
│  │ Server   │   │ (COM订阅/  │   │ (线程安全转发层)  │        │
│  │ (COM)    │   │  轮询)     │   └────────┬─────────┘        │
│  └──────────┘   └────────────┘            ▼                  │
│                                 ┌────────────────────┐       │
│                                 │GatewayModbusTcpServer│      │
│                                 │(NModbus TCP 从站)   │       │
│                                 └────────┬───────────┘       │
│  ┌────────────────────┐                  │  TCP/IP           │
│  │ LicenseAlgorithm / │                  ▼                   │
│  │ LicenseManager     │         ┌──────────────────┐         │
│  │ (PCID + HMAC 授权) │         │ Modbus TCP 主站   │         │
│  └────────────────────┘         │ (SCADA/MES/PLC)  │         │
│                                 └──────────────────┘         │
└──────────────────────────────────────────────────────────────┘
       ▲ IPC: StopEvent / HeartbeatEvent / GracefulExitEvent
┌──────────────────────────────────────────────────────────────┐
│  OpcDaToModbusGateway.Watchdog.exe（独立进程，崩溃自动重启）  │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 核心组件依赖

```
Program.cs（STA 入口 + 单实例 Mutex + 全局异常处理）
  └── MainForm.cs（UI 协调层，持有四个 Manager）
        ├── Services/LogManager.cs        → 日志（UI 同步显示 + 异步队列文件写入）
        ├── Services/ConfigManager.cs     → config.json / tags.json 加载保存、热重载、开机启动
        ├── Services/WatchdogManager.cs   → 看门狗进程生命周期（Start/Stop/SignalGracefulExit）
        ├── Services/GatewayManager.cs    → 网关生命周期（三阶段启动/停止/健康检查/DA 重连）
        ├── Services/LicenseManager.cs    → 授权码验证、试用计时（30 分钟）
        ├── Services/HealthSnapshot.cs    → 运行状态快照采集（health/ 目录 + 每日聚合）
        ├── OpcDaClient.cs                → OPC DA 客户端（TitaniumAS.Opc.Client）
        ├── GatewayModbusTcpServer.cs     → Modbus TCP 从站（NModbus 3.0.81）
        ├── DataBridge.cs                 → DA→Modbus 数据桥接与快照
        ├── OpcServerScanner.cs           → DA 服务器发现（5 种策略）
        ├── ItemSelectionDialog.cs        → 标签浏览/选择/CSV 导入导出/自动地址分配
        ├── ServerSelectionDialog.cs      → 服务器选择
        ├── AboutDialog.cs                → 关于对话框（版本、PCID、授权码输入）
        └── Models/
              ├── TagConfig.cs            → 配置模型（AppConfig/OpcDaConfig/ModbusTcpConfig/TagConfig）
              ├── DataTypeConverter.cs    → 数据类型转换与 Modbus wire encoding
              ├── OpcQualityHelper.cs     → OPC Quality 三态分类
              ├── ModbusWriteResult.cs    → Modbus 写入结果枚举
              ├── SnapshotData.cs         → TagSnapshot / SnapshotData
              └── LicenseAlgorithm.cs     → PCID 生成 + HMAC-SHA256 授权码
```

依赖注入：`GatewayManager` 持有 `IOpcDaClient` / `IGatewayModbusTcpServer` / `IDataBridge` 接口（`Services/Interfaces/`），测试中以 `FakeOpcDaClient`、`RecordingModbusServer` 注入。

### 3.3 三阶段启动（GatewayManager.StartAsync）

启动顺序严格有序、不可调换（桥接依赖 DA 与 Modbus 均已就绪）：

```
StartAsync(progressReport)
  校验映射: ValidateMappings(tags) → 失败则启动拒绝（启动前预校验）
  步骤 1/3: new GatewayModbusTcpServer(config.ModbusTcp) → await StartAsync()
            （先监听端口，确保下游客户端可连接）
  步骤 2/3: new OpcDaClient(progId, tags, host) → Start(updateRateMs, mode)
            （连接 DA 服务器 + 订阅/轮询；host 默认 localhost）
  步骤 3/3: 再次 ValidateMappings（CanonicalDataType 回写后基于最终类型）
            new DataBridge(daClient, modbusServer, tags) → Start()
            （注册 Modbus 映射 + 订阅 DA OnDataChanged 事件）
  → 锁内发布引用，IsRunning = true
```

- **失败回滚：** 任何步骤失败时按逆序释放已创建资源（bridge → daClient → modbusServer），每个释放调用独立 try-catch，并重置 `_startingFlag` 允许重试。
- **DA 首次连接失败可降级运行（V2.0.0）：** 步骤 2 的 DA 连接异常被单独捕获，**不影响 Modbus TCP 服务运行**——用户仍可用 Modbus Poll / Modscan 验证服务器监听；保留同一 `daClient` 实例（可能为未连接状态），DataBridge 仍订阅它，`CheckHealth` 将在此实例上自动重连，DA 恢复后数据自动流入。
- **启动后状态：** DA 已连接 → 绿色「● DA: 已连接」；DA 未连接 → 橙色「● DA: 未连接（自动重连中）」；Modbus 运行 → 绿色「● Modbus: 运行中」。

### 3.4 停止（逆序释放）

```
StopAsync()
  1. bridge.Dispose()        # 先取消 DA 事件订阅，防止桥接在 DA 释放后仍收到回调
  2. daClient.Dispose()      # 断开 DA 连接
  3. modbusServer.StopAsync() + Dispose()   # 关闭监听端口
  每个释放独立 try-catch，单个异常不阻断后续清理
```

### 3.5 健康监控与 DA 重连

`MainForm` 的 `_healthTimer`（`HealthCheckIntervalMs=10000`）每 10 秒调用 `GatewayManager.CheckHealth()`：

- `_healthCheckFlag`（Interlocked）防止重入；
- 检测 `daClient.IsConnected` 断开后执行 `TryReconnect`，累计最多 `MaxReconnectAttempts=50` 次；
- **指数退避（H-34）：** 初始 1 秒，每次失败翻倍，上限 60 秒；退避期间不计数；
- 重连成功计数器归零；超过上限后仅告警一次（「达到最大重连次数，停止重连」），不无限提示；
- 捕获 `ObjectDisposedException`（CheckHealth 锁外操作与 StopAsync 释放的生命周期交叉属正常）。

---

## 4. OPC DA 采集、TagKey 分发和 Quality 语义

### 4.1 OpcDaClient 数据采集

`OpcDaClient` 封装 TitaniumAS.Opc.Client（MIT），负责连接、订阅、浏览与数据回调。构造函数：

```csharp
public OpcDaClient(string serverProgId, List<TagConfig> tags, string host = "localhost")
```

**数据获取方式（DaAcquisitionMode）：** 由 `OpcDaConfig.Mode` 决定，经 `GetEffectiveMode()` 解析（`"Sync"` 不区分大小写 → Sync，其余含空值/笔误一律回退 Async）：

- **Async（默认）：** 注册 `_group.ValuesChanged += OnValuesChanged` 并 `IsSubscribed = true`，服务器主动推送；另启动 5 分钟（`DaSyncIntervalMs=300000`）定时 `DoSyncRead()` 全量兜底，防止回调丢包导致数据长期停滞。
- **Sync：** `IsSubscribed = false`，按 `UpdateRateMs` 定时 `group.Read(items, OpcDaDataSource.Device)` 主动轮询。

**订阅添加：** `AddAllItems()` 分批提交（`DaAddItemBatchSize=2000`/批，避免单次 COM 调用超时）；批量结果中失败标签逐条上报（前 5 条明细 + 汇总）。

**浏览地址空间：** 静态方法 `BrowseAllItems` 递归浏览（最大深度 `MaxBrowseDepth=10`、上限 `DaMaxBrowseItems=50000`），不做全局去重；`OpcDaItemInfo.Description` 记录父节点路径；Server 用 `using` 保证 COM RCW 释放。TitaniumAS 迁移后（H-26）使用 `OpcDaBrowserAuto.GetElements()` 一次性获取子元素，`DaBrowsePageSize` 常量已不再被浏览代码引用。

### 4.2 CanonicalDataType 约束（真实数据类型回写）

浏览阶段大多数 OPC DA 服务器只返回 "Variant"。`BrowseAllItems` 完成后创建临时 OPC DA Group（`IsActive=false`）批量添加点位，从 `OpcDaItem.CanonicalDataType.Name` 读取真实类型回写 `OpcDaItemInfo.DataTypeName` 后清理。

运行时 `AddAllItems()` 同样从订阅项的 `CanonicalDataType` 回写 `TagConfig.DataType`（排除 "Object"/"Variant"），若类型发生变更则触发 `OnConfigChanged` → `ConfigDirty` → 配置立即持久化。**回写后 `GatewayManager` 会基于最终类型再次执行 `ValidateMappings`**（因为宽度/编码取决于声明类型）。

### 4.3 TagKey 机制与持久化

TagKey 是解决同一 ItemId 在多个节点下出现时数据错位的内部唯一标识。

**分配规则（`TagConfig.AssignTagKeys`）：**

- 所有 ItemId 唯一 → `TagKey = ItemId`（透明传递）；
- 存在重复 ItemId → 所有标签统一使用 `"{列表索引}_{ItemId}"`（如 `3_Random.Int32`），确保同一 ItemId 映射到多个 Modbus 寄存器时可区分。

**持久化（N-8 / V2.0.0）：** TagKey **持久化于 `tags.json`**，加载后不再重新分配。`AssignTagKeys` 仅对 TagKey 为 null/empty 的标签分配，新分配的 Key 由 `ConfigManager.Load` 通过 `SaveAllImmediate()` 立即写入磁盘。

**使用位置：**

| 组件 | 用途 |
|---|---|
| `OpcDaClient._tagKeyToItem` | TagKey → OPC DA 订阅项（ConcurrentDictionary） |
| `OpcDaClient._itemIdToTagKeys` | ItemId → TagKey 列表（反向映射，ConcurrentDictionary） |
| `DataBridge._tagMap` / `_valueCache` | TagKey → TagConfig / TagSnapshot |
| `GatewayModbusTcpServer._tagMap` | TagKey → ModbusTagMapping |

**分发：** DA 回调以 `ItemName` 标识数据，经 `_itemIdToTagKeys` 反查后将同一 ItemId 的数据分发给所有匹配 TagKey：

```
DA 回调 (ItemName="Tag1", Value=42)
  → _itemIdToTagKeys["Tag1"] = ["0_Tag1", "5_Tag1"]
  → OnDataChanged("0_Tag1", 42, isGood, ts)
  → OnDataChanged("5_Tag1", 42, isGood, ts)
```

### 4.4 Quality 语义（三态 helper，桥接仅 bool）

`OpcQualityHelper` 识别 **三态**：

```csharp
public enum OpcQualityKind { Bad, Uncertain, Good }

public static OpcQualityKind Classify(int status)
{
    switch (status & 0xC0)
    {
        case 0xC0: return OpcQualityKind.Good;      // 高 2 位 11
        case 0x40: return OpcQualityKind.Uncertain; // 高 2 位 01
        default:   return OpcQualityKind.Bad;       // 高 2 位 00/10
    }
}
```

**关键约束：** `OpcDaClient` 的两处回调（异步 `OnValuesChanged` 与定时 `DoSyncRead`）均只调用 `IsGood()`（bool），`IOpcDaClient.OnDataChanged` 事件签名即为 `(string tagKey, object value, bool isGood, DateTime timestamp)`——**桥接接口当前只传播 Good / 非 Good**。即：

- 质量判定必须用语义正确的 OPC DA 数据质量位（`value.Quality.Status & 0xC0 == 0xC0`），而非操作结果 `value.Error.Succeeded`；
- 非 Good（含 Uncertain、Bad）在桥接层统一按「坏质量」处理：`DataBridge` 写快照 `DaQuality="Bad"`、`ModbusStatus="BadQuality"`，不写入寄存器。

---

## 5. Modbus 类型、wire encoding、寄存器宽度和地址空间

### 5.1 四个地址空间

NModbus `DefaultSlaveDataStore` 管理四个标准 Modbus 数据区：

| 数据区 | 显示前缀 | 功能码 | 读写性 |
|---|---|---|---|
| Coil | 0x | 01/05/15 | 布尔量读写 |
| Discrete Input | 1x | 02 | 布尔量只读 |
| Holding Register | 3x | 03/06/16 | 寄存器读写 |
| Input Register | 4x | 04 | 寄存器只读 |

### 5.2 声明类型、寄存器宽度与 wire encoding

`DataTypeConverter` 是类型语义的唯一事实源：

**规范化（`NormalizeModbusDataType`）：** 大小写不敏感，支持别名（bool/boolean → Bool、short → Int16、single/real4 → Float、real8 → Double 等）。**String 与 DateTime 未定义 wire encoding，调用即抛 `NotSupportedException`。**

**寄存器宽度（`GetModbusRegisterWidth`）：**

| Modbus 类型 | 寄存器宽度 |
|---|---|
| Bool / Byte / SByte / Int16 / UInt16 | 1 |
| Int32 / UInt32 / Float | 2 |
| Double | 4 |

**wire encoding（`EncodeModbusRegisters`）：** 多寄存器类型采用 **高 word 在前** 的大端序编码（`SplitWords`：`result[i] = (ushort)(bits >> ((count - i - 1) * 16))`）：

| 示例值 | 类型 | 编码结果（寄存器序列） |
|---|---|---|
| -1 | Int16 | `0xFFFF` |
| 0x11223344 | Int32 | `0x1122, 0x3344` |
| 1.0f | Float | `0x3F80, 0x0000` |
| 1.0d | Double | `0x3FF0, 0, 0, 0` |

**OPC DA → Modbus 类型映射（`GetModbusDataType`）：**

| OPC DA 类型 | Modbus 类型 | 宽度 |
|---|---|---|
| Boolean | Bool | 1 bit |
| SByte / Int16 | Int16 | 1 |
| Byte / UInt16 | UInt16 | 1 |
| Int32 | Int32 | 2 |
| UInt32 | UInt32 | 2 |
| Float (Single) | Float | 2 |
| Double | Double | 4 |
| String / DateTime | （无 wire encoding，启动拒绝） | — |
| Variant / 未知 | 抛 `NotSupportedException` | — |

### 5.3 寄存器类型推断与地址宽度

- `TagConfig.GetEffectiveRegisterType()`：`ModbusRegisterType` 为空时默认 `HoldingRegister`；支持 coil/discrete/holding/input 及别名（discrete、holding、input）。
- `TagConfig.GetEffectiveAddressWidth()`：**Bit 地址空间（Coil/DiscreteInput）固定占 1**，但声明类型仍必须是有定义 wire encoding 的类型（String/DateTime 在 Bit 空间同样被拒绝）；寄存器空间（Holding/Input）按声明类型宽度。
- `GetEffectiveModbusDataType()`：优先 `ModbusDataType`，为空时按 `DataType` 自动推断。

### 5.4 地址显示约定

内部 `ModbusAddress` 为 **0-based**；UI 与导出显示为标准表示法 `FormatModbusAddress`：前缀（`0x`/`1x`/`3x`/`4x`）+ `(address + 1).ToString("D4")`。

| 内部地址 | 显示 |
|---|---|
| Coil 0 | `0x0001` |
| DiscreteInput 5 | `1x0006` |
| HoldingRegister 0 | `3x0001` |
| InputRegister 1023 | `4x1024` |

### 5.5 启动拒绝（String/DateTime）

`GatewayManager.StartAsync` 第一步 `ValidateMappings` 对每个标签调用 `GetEffectiveAddressWidth()` → `GetModbusRegisterWidth()` → `NormalizeModbusDataType()`，String/DateTime 在此抛 `NotSupportedException`，被包装为 `InvalidOperationException("标签 '...' 的 Modbus 映射无效: ...String/DateTime 未定义 wire encoding...")`，**网关启动被拒绝**（测试 `UnsupportedWireTypes_AreRejected` 覆盖）。

---

## 6. 自动地址分配、映射校验和 CSV hard constraints

### 6.1 自动地址分配（已实现）

`ItemSelectionDialog.BtnOK_Click` 在用户确认导入后自动分配 Modbus 地址：

```
1. TagConfig.AssignTagKeys(SelectedTags)     # 先分配 TagKey
2. 按四个地址空间分别维护 next address（初始 0）：
   foreach tag:
     registerType = tag.GetEffectiveRegisterType()
     width = tag.GetEffectiveAddressWidth()   # Bit 空间固定 1，寄存器空间按类型宽度
     if next + width - 1 > ushort.MaxValue → 抛"地址空间溢出"
     tag.ModbusAddress = next
     next += width
```

即 **Coil / DiscreteInput 每点 +1；Holding / Input 按有效类型宽度递增**（Int32/Float +2、Double +4），四空间独立推进、互不干扰；分配前检查 `ushort.MaxValue` 溢出。

### 6.2 启动映射校验（ValidateMappings）

`GatewayManager.StartAsync` 启动前（及 CanonicalDataType 回写后再次）校验：

- 标签配置为空 → 拒绝；
- 每个标签：`GetEffectiveAddressWidth` 确定占位宽度，末地址（`ModbusAddress + width - 1`）不得溢出 `ushort.MaxValue`；
- 同一地址空间内地址区间不得重叠（`HashSet<int>` 逐位占用检测）；
- String/DateTime 等无 wire encoding 类型 → 拒绝（见 5.5）。

### 6.3 CSV hard constraints

**编码：** 导出与导入一律使用 **GBK** 编码（`Encoding.GetEncoding("GBK")`，WPS 兼容），不写 `sep=,` 首行。

**① 映射表（主程序「导出映射」/「导入映射」）——固定 7 列表头：**

```
序号,DA_ItemId,DisplayName,DA_DataType,Modbus_Function,Modbus_Address,Modbus_DataType
```

- 前导固定文本行（模板硬约束，A1-A4 语义）：
  - `#不用改,OPC DA 到 Modbus TCP 映射表,,,,,,`
  - `#不用改,导出时间:,yyyy-MM-dd HH:mm,,,,`
  - `#不用改,DA 服务器:,<ProgId>,,,,`
  - `#修改C4,点位数:,<N>,,,,`
  - `#不用改,地址格式:,1(D0 线圈),2(DI 离散输入),3(HR 保持寄存器),4(AR 输入寄存器),`
  - `#不用改,DA_DataType 为 OPC DA 侧类型,Modbus_DataType 为 Modbus 侧类型,,,,`
- **序号列 = 身份标识（`MappingCsvIdentity.FormatSequence`）：** `"{序号}|{Base64URL(TagKey)}"`（UTF-8 → Base64，去 `=`，`+`→`-`、`/`→`_`）。**导入时优先按 TagKey 匹配并校验与 DA_ItemId 一致**（不匹配抛异常）。
- **旧格式兼容（纯数字序号）：** 按 ItemId 匹配；`ValidateLegacyItemIds` 在 current 或 CSV 侧存在**重复 ItemId** 时抛「旧纯数字 CSV 格式无法区分重复 ItemId，请重新导出包含 TagKey 的映射表」。**新旧身份格式禁止混用。**
- `Modbus_Function` 列值：`1(D0 线圈)` / `2(DI 离散输入)` / `3(HR 保持寄存器)` / `4(AR 输入寄存器)`（导入解析兼容前缀数字与中文/缩写）。
- `Modbus_Address` 列导出 **0-based 原始地址值**（数字）；`Modbus_DataType` 导出规范化类型（Single → Float 转换，导入时 Float → Single 还原）。
- **导入校验：** 读取 GBK；前导行提取服务器名与点位数，与当前不一致时确认提示；跳过 `#` 注释行与表头；重复映射键拒绝；导入后 `SaveTagsImmediate()` + `Save()` 持久化并刷新监控表。

**② DA 标签列表（主程序「导出点表」与 ItemSelectionDialog「导出 CSV」）——固定 5 列表头：**

```
序号,ItemId,DisplayName,DataType,描述
```

- 前导固定文本行：`#不用改,#服务器:,<ProgId>,` / `#不用改,#导出时间:,...,` / `#修改C3,#已选点位:,<N>,`；
- DataType 列 `Single` 导出时转换为 `float`；
- ItemSelectionDialog 导入：跳过 `#` 与表头，RFC 4180 引号解析（`ParseCsvLine`），取第 2 列（或唯一列）作为 ItemId，与已浏览列表匹配并勾选。

---

## 7. `ModbusWriteResult` 和 DA/MB 分离快照

### 7.1 ModbusWriteResult

```csharp
public enum ModbusWriteStatus
{
    Success,        // 写入成功
    BadQuality,     // 数据质量非 Good，拒绝写入
    NotRunning,     // Modbus 服务器未运行或数据存储区不存在
    NotMapped,      // TagKey 未注册
    EncodingError,  // 编码失败（FormatException/OverflowException/InvalidCastException/NotSupportedException）
    WriteError      // 其他写入异常
}
```

`GatewayModbusTcpServer.UpdateValue(string tagKey, object value, bool isGood, DateTime timestamp)` **返回 `ModbusWriteResult`**：

- `!isGood` → `Failed(BadQuality)`；
- 未运行 / 未映射 → `NotRunning` / `NotMapped`；
- `WriteToRegister` 按寄存器类型写 `CoilDiscretes` / `CoilInputs` / `HoldingRegisters` / `InputRegisters`（`WritePoints`），编码异常映射为 `EncodingError`，其余异常为 `WriteError`；
- 成功后更新 `ModbusTagMapping.LastValue / LastTimestamp` 并返回 `Succeeded()`。

`DataBridge` 调用 `_modbusServer.UpdateValue(tagKey, convertedValue, true, timestamp)`，并用 `try-catch` 兜底（回调异常 → `WriteError`）。

### 7.2 DA/MB 分离快照

`TagSnapshot`（`Models/SnapshotData.cs`）为 **DA 侧与 Modbus 侧分离字段**：

```csharp
public class TagSnapshot
{
    public string TagKey; public string ItemId; public string DisplayName;
    public string DataType; public ushort ModbusAddress; public string ModbusRegisterType;
    // DA 侧
    public object DaValue; public string DaQuality; public string DaTimestamp;
    // Modbus 侧（与 DA 侧独立演进）
    public object ModbusValue; public string ModbusStatus; public string ModbusLastSuccessTimestamp;
}
```

`DataBridge.OnDaDataChanged` 快照更新规则（不可变对象整体替换，消除竞态）：

| 场景 | DaValue / DaQuality / DaTimestamp | ModbusValue / ModbusStatus / ModbusLastSuccessTimestamp |
|---|---|---|
| 初始（未收到数据） | `-` / `Waiting` / `-` | `-` / `Waiting` / `-` |
| 非 Good 或值为 null | 原值(或新值) / `Bad` / 当前时间 | 保留 `previous.ModbusValue` / `BadQuality` / 保留上次成功时间 |
| 类型转换失败 | 原始值 / `Good` / 当前时间 | 保留前值 / `ConversionError` / 保留上次成功时间 |
| 写入成功 | 转换后值 / `Good` / 当前时间 | 转换后值 / `Good` / 当前时间（`TotalUpdates++`） |
| 写入失败 | 转换后值 / `Good` / 当前时间 | 保留前值 / `WriteError` / 保留上次成功时间 |

要点：**Modbus 侧值只反映最后一次成功写入寄存器的值**；坏质量、转换失败、写入失败均不影响已成功写入的寄存器值（`ModbusLastSuccessTimestamp` 保留上次成功时刻），监控值与实际寄存器状态一致。快照顺序由构造时 `_orderedKeys` 保证，`GetSnapshots()` 按此顺序返回（UI 表格行一一对应）。

---

## 8. `config.json` / `tags.json`、原子迁移和热重载

### 8.1 配置模型

`Models/TagConfig.cs` 定义全部配置模型（Newtonsoft.Json 13.0.4 序列化）：

```
AppConfig
  ├── OpcDaConfig        ServerProgId / ServerHost / UpdateRateMs / Mode(Async|Sync) / Tags
  │     └── TagConfig    ItemId / DisplayName / DataType / ModbusAddress / ModbusRegisterType / ModbusDataType / TagKey
  └── ModbusTcpConfig    Port / ListenAddress / SlaveId / MaxHoldingRegisters / MaxCoils / MaxInputRegisters / MaxDiscreteInputs
  LastConnectedProgId / AutoConnectDa / AutoStartModbus / AutoStartWithWindows / EnableWatchdog / AuthorizationCode
```

`GetEffective*` 系列提供默认值回退：`ListenAddress` 空 → `0.0.0.0`、`Port ≤ 0` → `502`、`GetEndpointUrl()` → `modbus.tcp://0.0.0.0:502/`；`Mode` 非 "Sync" 一律回退 Async；`Tags` 默认空列表（避免各处 null 检查）。

### 8.2 标签分离存储（P1-1）

- **`config.json`**：仅网关设置（约 2KB），`DoSave` 序列化时移除 `OpcDa.Tags`；
- **`tags.json`**：仅标签数据（含 TagKey），`SaveTagsImmediate()` 单独写入（仅导入/变更时触发）；
- **加载优先级：** `ConfigManager.Load` 优先从 `tags.json` 读取 Tags；文件不存在时回退 `config.json` 内联 Tags（向后兼容），并自动迁移到 `tags.json`。

### 8.3 原子写入与迁移

- **原子写入（`AtomicWrite`）：** 先写 `path + ".tmp"`，再 `File.Replace`（目标存在时）或 `File.Move`（不存在时），读取端永远不会看到 torn write。
- **`SaveAllImmediate`（原子迁移）：** 锁内先写 `tags.json`，再写 `config.json`；若第二步失败则用备份回滚 `tags.json`（或删除新文件），保证两份文件一致。
- **并发保护：** `_saveLock`（Monitor，可重入）+ 手动 `Monitor.Enter/Exit` 保护 `DoSave`，防抖 Timer 回调与 `SaveImmediate` 不会并发写。
- **防抖：** `Save()` 复用同一 `System.Threading.Timer`（`ConfigDebounceMs=500`）合并连续变更；`SaveImmediate()` 先 Dispose 待执行 Timer 再写。

### 8.4 向后兼容默认值（ApplyBackwardCompatDefaults）

- `ModbusTcp` 节点缺失 → 创建，`ListenAddress="0.0.0.0"`、`Port=502`；
- `OpcDa` 节点缺失 → 创建，`UpdateRateMs=1000`；
- `ServerProgId` 为空 → 填充 `Matrikon.OPC.Simulation.1`（开箱即用）。

### 8.5 热重载（FileSystemWatcher，仅 config.json）

`ConfigManager.StartWatching()` 用 `FileSystemWatcher` 监听 **`config.json`**（`NotifyFilter = LastWrite | Size`）：

- **`tags.json` 当前不参与 watcher 热重载**（watcher 仅绑定 `GetConfigPath()`）；
- 事件 500ms 防抖合并（编辑器多次保存）；`_watchGeneration` + `_activeWatchCallbacks` + `_watchCallbacksIdle` 保证 StopWatching 后无旧回调残留；
- 触发 `ConfigFileChanged` → MainForm 分支处理：
  - 网关**运行中** → 仅提示「配置已变更，需重启」；
  - 网关**未运行** → 自动重新 `Load()` 并刷新 UI 控件；
- **根对象引用稳定：** `Load()` 在 `Config` 已存在时仅复制各字段，保持同一 `AppConfig` 实例，使 GatewayManager 等长期持有者看到热重载后的配置；
- `Dispose()`（IDisposable）停止 watcher 并等待在途回调排空。

---

## 9. UI、健康快照、日志和生命周期

### 9.1 主窗口（MainForm）

- 窗口标题：`internal const string WindowTitle`（= `AppConstants.WindowTitle` = "OPC DA → Modbus TCP 网关"），Program.cs 引用同一常量避免不一致；
- **主窗体禁止最大化：** `FormBorderStyle.FixedSingle` + `MaximizeBox = false`；
- 区域划分：OPC DA 服务器区（ProgId、浏览、获取点位、数据获取模式下拉）、Modbus TCP 设置区（监听地址/端口/从站 ID、端点 URL 预览、导出/导入映射）、控制面板（启动/停止、导出点表、自动选项、状态标签、关于）、标签数据监控表、运行日志框。

**标签监控表（DataGridView 虚拟模式，10 列）：**

| 列 | 数据来源 |
|---|---|
| DA 标签名称 | `tag.DisplayName` |
| DA_ItemId | `tag.ItemId` |
| DA 当前值 / DA 质量戳 / DA 时间戳 | 快照 `DaValue` / `DaQuality` / `DaTimestamp` |
| MB 数据类型 | `tag.GetEffectiveModbusDataType()` |
| MB 地址 | `FormatModbusAddress(tag.GetEffectiveRegisterType(), tag.ModbusAddress)`（如 `3x0001`） |
| MB 当前值 / MB 质量戳 / MB 时间戳 | 快照 `ModbusValue` / `ModbusStatus` / `ModbusLastSuccessTimestamp` |

- 虚拟模式：`RowCount` 只设行数不建行对象，`CellValueNeeded` 按需取数，仅使可见行失效（`InvalidateRow`），每秒仅 ~200-300 次回调而非 50 万次；
- `CellFormatting`：列 3（DA 质量）与列 8（MB 状态）——"Good" 绿色，其余红色；
- 双缓冲 + 列排序禁用，支持 50000+ 行流畅滚动；
- **自适应刷新（P1-4）：** `RefreshStats` 缓存快照特征哈希，无变化时 `_refreshTimer` 降为 `UiSlowRefreshMs=3000`，有变化恢复 `UiDefaultRefreshMs=1000`。

### 9.2 启动流程与自动选项

```
Program.Main [STAThread]
  1. Bootstrap.Initialize()   # TitaniumAS COM 安全初始化（任何 COM 操作前必须调用一次）
  2. 注册全局异常处理（ThreadException + UnhandledException + SetUnhandledExceptionMode）
  3. 单实例 Mutex（OpcDaToModbusGateway_SingleInstance）——已存在则激活已有窗口后退出
  4. 解析 --minimized（开机自启最小化到托盘）
  5. Application.Run(new MainForm(startMinimized))
```

MainForm 构造 → `BuildUI()` → `LoadConfiguration()`：

- `ConfigManager.Load()` → 初始化 `GatewayManager`、`HealthSnapshot`、`LicenseManager`；
- 恢复复选框（`_isLoadingConfig` 标志防触发自动保存）；
- `Config.EnableWatchdog` → `WatchdogManager.Start()`；
- `AutoStartModbus && Tags.Count > 0` → 1 秒后自动隐藏到托盘并启动网关；
- `AutoConnectDa` → 恢复上次连接服务器。

### 9.3 系统托盘与关闭（生命周期）

- `NotifyIcon` + 右键菜单（显示主窗口 / 退出）；双击显示主窗口；关闭按钮与最小化均隐藏到托盘（`OnResize`），除非 `_forceClose`；
- 恢复窗口顺序：`Show()` → `ShowInTaskbar=true` → `WindowState=Normal` → `Activate()`；
- **关闭（异步两阶段 OnFormClosing）：**
  1. 首次 `UserClosing` → 取消并隐藏到托盘；
  2. 真实退出：置 `_isShuttingDown`，捕获并释放三个 Timer（refresh/health/autoStart），`WatchdogManager.SignalGracefulExit()`（看门狗保持运行但不重启），`try-catch` 包裹 `GatewayManager.StopAsync()`，置 `_forceClose=true` 后 `Close()`；
  3. 第二次 Close → 释放 `LogManager` / `NotifyIcon` / `LicenseManager` / `HealthSnapshot` / `ConfigManager`，`base.OnFormClosing`；
- 未捕获异常写入 `crash.log`（追加、时间戳 + 完整堆栈，静态锁防交错）；`DllImport` 均带 `SetLastError=true`。

### 9.4 健康快照（HealthSnapshot）

- 每 5 分钟采集进程健康指标，写入 `health/` 目录（`health_*.json`），保留最近 24 小时（288 个文件）；
- `SnapshotData` 字段：WorkingSetMB / PrivateMemoryMB / GcTotalMemoryMB / ThreadCount / HandleCount、IsRunning / DaConnected / TotalUpdates / UpdateRatePerSec / ErrorCount / ModbusVariableCount / ModbusSlaveId、ThreadPoolWorkerBusy/Max；
- 每日凌晨 00:05 聚合昨日快照追加 `health_daily.jsonl`（`DailySummary`，保留 90 天）；
- 7 天/30 天工作集增长率超阈值触发 `OnAlert`（黄色 20%、红色 50%）→ MainForm 弹内存告警。

### 9.5 日志（LogManager）

- UI 同步显示 + 后台线程异步写文件（`BlockingCollection` 队列，容量 50000，满则丢新条目）;
- 文件：`logs/gateway_yyyy-MM-dd.log`（按日切换，AutoFlush=false，每 10 次写入 Flush）；
- 保留 30 天，每 500 次写入触发 `CleanupOldFiles`；
- UI 文本框超 500,000 字符截断至末尾 50,000；
- Dispose：`CompleteAdding` 通知后台线程排空，`Join` 最长 5 秒，后台退出后再关 StreamWriter。

---

## 10. Watchdog IPC、心跳、优雅退出和重启策略

### 10.1 命名事件协议

| 机制 | 名称 | 方向 | 模式 | 用途 |
|---|---|---|---|---|
| 命名 Mutex | `OpcDaToModbusGateway_SingleInstance` | 主程序内部 | — | 主程序单实例 |
| 命名 Mutex | `OpcDaToModbusGateway_Watchdog_Mutex` | 看门狗内部 | — | 看门狗单实例 |
| EventWaitHandle | `OpcDaToModbusGateway_Watchdog_Stop` | 外部 → 看门狗 | ManualReset | 通知看门狗自身退出 |
| EventWaitHandle | `OpcDaToModbusGateway_Heartbeat` | 主程序 → 看门狗 | ManualReset | 主程序存活心跳（乒乓协议） |
| EventWaitHandle | `OpcDaToModbusGateway_GracefulExit` | 主程序 → 看门狗 | ManualReset | 正常退出信号（不重启） |

### 10.2 心跳

- 主程序 `WatchdogManager` 心跳定时器每 10 秒 Set 一次 `HeartbeatEvent`（首次立即触发；回调异常写独立 `watchdog_errors.log`）；
- 看门狗超时阈值 30 秒（3 个心跳周期），允许偶发丢失一次心跳不误杀；
- **ManualReset 语义：** 主进程 Set、看门狗 Reset；`WaitOne` 不消费信号，检测后显式 Reset 准备下一轮（避免 AutoReset 时序窗口丢信号）；
- 看门狗两阶段检测：先 `WaitOne(0)` 快速路径，未命中再 `WaitOne(30000)` 阻塞等待；超时判定主进程**挂起**（isHung），`Kill()` 后按崩溃流程重启。

### 10.3 优雅退出与重新武装

- **托盘退出（正常）：** 主程序 `SignalGracefulExit()`：停止心跳定时器 → Set `GracefulExitEvent`（不存在则自行创建）→ 看门狗保持运行但跳过重启，等待手工启动；
- **取消勾选「进程守护」：** `WatchdogManager.Stop()`：停止心跳 → Set StopEvent → `WaitForExit(3000)` → 兜底 `KillAll()`；
- **崩溃：** 无信号 → 看门狗检测到进程消失后自动重启；
- **重新武装（V2.0.0 修复）：** `WatchdogManager.Start()` 会清除上次残留的 GracefulExit 信号，避免主程序手工重启后崩溃被误判为「优雅退出」而不拉起。

### 10.4 重启策略（WatchdogRestartPolicy）

```csharp
internal enum WatchdogDecision { None, SuppressRestart, Restart, Rearm }

Evaluate(bool isProcessRunning, bool gracefulExitSignaled)
```

状态机（测试 `WatchdogRestartPolicyTests` 覆盖）：

- 进程存活：普通轮询 → `None`；处于 WaitingForManualStart 时检测到手工启动 → `Rearm`（重新武装崩溃守护并 Reset 优雅退出事件）；
- 进程不存活 + 优雅退出信号 → `SuppressRestart`，进入 WaitingForManualStart（**持续抑制**，直到手工启动重新武装）；
- 进程不存活 + 无信号 + 处于 WaitingForManualStart → 说明信号已被主程序启动时清除 → `Restart`（重新武装崩溃守护）；
- 进程不存活 + 无信号 → `Restart`。

### 10.5 监控循环（Watchdog/Program.cs）

```
while (true):
  1. stopEvent.WaitOne(0) → 有信号退出
  2. 按进程名查找主程序（进程名不含扩展名）
  3. 心跳检测（进程存活且心跳事件存在时）：快速路径 / 慢速路径，超时判定挂起
     → 挂起则 Kill（WaitForExit 5s）并标记为不存活
  4. 不存活 → 检查 GracefulExitEvent → restartPolicy.Evaluate 决策
     → Restart 分支：
       防抖：60 秒窗口内已重启 ≥3 次 → 暂停 60 秒（WaitOne 可响应停止信号）后重置计数
       等待 3 秒（RestartDelayMs，WaitOne 可中断）→ Process.Start(mainExePath)
       （using 立即释放 Process 句柄；UseShellExecute=true）
  5. stopEvent.WaitOne(5000) 作为轮询间隔（可即时响应停止信号）
```

- 日志：`logs/watchdog.log`（单文件，`[yyyy-MM-dd HH:mm:ss]` 前缀），保留 7 天，每 100 条且文件 > 10KB 时按行内时间戳清理（非标准续行跟随前条状态）；
- 启动即 `CleanupOldLogs()`；退出时 Dispose 事件句柄。

---

## 11. 授权、构建、测试、部署和现场验收

### 11.1 授权体系

**LicenseAlgorithm（主程序与 Keygen 共享，`Models/LicenseAlgorithm.cs`）：**

- **PCID 生成：** WMI 查询 CPU ProcessorId / 主板序列号 / BIOS 序列号 → `"{cpu}|{board}|{bios}"` → SHA256 前 8 字节 → 16 位十六进制；三项全部失败时抛异常（防止所有机器共享 "UNKNOWN" PCID）。
- **授权码：** HMAC-SHA256(派生密钥, PCID) 取前 20 字节 → `XXXX-XXXX-XXXX-XXXX-XXXX`（29 字符）；验证用常量时间比较防时序攻击。
- **密钥保护（v1.3.5）：** 32 字节密钥分 `_layer1/_layer2/_layer3` 三段嵌入 IL，运行时 `DeriveKey()` 逐层 XOR + SHA256 派生；含版本标识字节，升级时同步更新。

**LicenseManager（试用/授权状态机）：**

- 启动读取 `AuthorizationCode`：验证通过 → 已授权；无码或无效（硬件变更）→ 进入 30 分钟试用（无效码清除并保存）；
- 试用每秒更新状态栏：≤5 分钟红色、≤10 分钟橙色、其余深橙色；
- 试用到期 → `GatewayStopRequested` → MainForm 停止网关、禁用启动、提示后退出（`_forceClose=true`）；
- `ApplyAuthorizationCode(authCode)` 验证成功 → 停止试用计时器、保存授权码。

**Keygen（控制台，源码不随仓库分发，本地维护）：** 交互模式（查看本机 PCID / 输入 PCID 生成授权码）与命令行模式（`Keygen.exe <PCID>` 直接输出）；通过链接主项目 `Models/LicenseAlgorithm.cs` 保证实现一致。

### 11.2 构建

```bash
# 完整清理 + 构建（推荐）
dotnet build-server shutdown      # 关闭 Roslyn 编译器缓存（防 MSB3030 "找不到 exe"）
rm -rf obj bin
dotnet build -c Release --no-incremental

# 仅构建主项目（自动触发看门狗构建）
dotnet build OpcDaToModbusGateway.csproj -c Release

# 运行测试
dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release
```

- 版本号在两个生产项目 csproj（主程序、Watchdog）中统一管理：`<Version>2.2.0</Version>`、`<AssemblyVersion>2.2.0.0</AssemblyVersion>`、`<FileVersion>2.2.0.0</FileVersion>`，另加 `AppConstants.AppVersion`；
- 主项目自定义 MSBuild Target（`BeforeTargets="Build"`）：`BuildAndCopyWatchdog`——先 `dotnet build` 子项目再复制 exe 到主输出目录；（Keygen 已移除出仓库，无对应 Target）
- `<Compile Remove="Watchdog\**" />`、`Tests\**` 排除子目录源码，防 CS0579 重复程序集属性；
- **Costura.Fody（5.7.0）：** `FodyWeavers.xml` 嵌入 Newtonsoft.Json、TitaniumAS.Opc.Client、NModbus、System.Diagnostics.DiagnosticSource、Common.Logging、Common.Logging.Core；嵌入后主 exe 约 2MB，输出目录无第三方 DLL（`<PrivateAssets>all</PrivateAssets>`）。

### 11.3 测试（33 项 MSTest）

| 测试类 | 覆盖 |
|---|---|
| ModbusCorrectnessTests（24 项） | 寄存器宽度（10 类型）、转换失败抛异常而非写 0、高 word 编码、Single→Float、DataBridge 传有效声明类型、Fake 客户端 Dispose 拒绝与重启、String/DateTime/Variant 拒绝、位空间宽度=1、未解析类型待定与配置变更后补注册、CSV 身份往返与旧格式识别、DA/MB 分离快照、Uncertain 质量区分、旧 CSV 拒绝重复 ItemId |
| OpcQualityTests（5 项） | Quality 高 2 位分类（Good/Uncertain/Bad） |
| ConfigMigrationTests（2 项） | 内联标签迁移 tags.json 后二次加载、tags 写入失败保留旧配置 |
| WatchdogRestartPolicyTests（2 项） | 优雅退出持续抑制至手工启动重新武装、手工启动后崩溃重新武装 |

测试依赖：`FakeOpcDaClient`、`RecordingModbusServer`（实现 `Services/Interfaces` 接口）、`WatchdogRestartPolicy`（`<Compile Include="..\Watchdog\WatchdogRestartPolicy.cs">` 链接）。

### 11.4 部署与现场验收

部署即复制 2.3 节清单到目标目录，以管理员运行 `OpcDaToModbusGateway.exe`。现场验收清单：

1. OPC DA 服务器（32 位）COM 已注册；首次运行默认 ProgId 为 `Matrikon.OPC.Simulation.1`；
2. 浏览/获取点位 → 确认导入后 TagKey 与自动 Modbus 地址已分配（tags.json 可见）；
3. 启动网关：日志依次输出 `[1/3]` `[2/3]` `[3/3]`；DA 失败时 Modbus 仍监听（橙色状态 + 自动重连）；
4. 用 Modbus Poll / Modscan 连接 `0.0.0.0:502`（从站 ID 1），按 `3x0001` 等地址读取并核对值与质量；
5. 勾选「进程守护」验证崩溃自动拉起与托盘退出不重启；
6. 授权：授权方通过内部工具生成授权码 → 关于窗口输入 → 状态变「已授权」。

---

## 12. 已知限制与安全边界

### 12.1 功能限制

1. **单 DA 服务器：** 同时仅连接一个 OPC DA 服务器，不支持多服务器聚合到同一从站；
2. **单向数据流：** 仅 OPC DA → Modbus 读取方向，不支持 Modbus 主站写回 OPC DA；
3. **String/DateTime 不支持：** 未定义 Modbus wire encoding，映射含此类标签时启动被拒绝（属刻意设计而非缺陷）；
4. **Quality 传播粒度：** helper 识别 Good/Uncertain/Bad 三态，但桥接接口（`OnDataChanged` 的 bool `isGood`）当前仅传播 Good/非 Good，Uncertain 与 Bad 在快照中同记 `Bad`/`BadQuality`；
5. **热重载范围：** watcher 仅监听 `config.json`；`tags.json` 变更不触发热重载（标签修改后需重启或手动导入）；
6. **同步轮询延迟：** Sync 模式下数据变化延迟约为轮询周期的一半（平均）；
7. **多寄存器类型**（Int32/Float +2、Double +4）会占多个连续地址，大量使用时需注意地址空间规划（自动分配已按宽度推进并防溢出）。

### 12.2 安全边界

1. **默认监听 0.0.0.0:502** 允许所有网络接口访问，无内置认证；仅应在可信工业内网部署，必要时以防火墙限制来源；
2. **管理员权限运行**（manifest `requireAdministrator`），进程被提权——部署环境需信任该程序；
3. **授权保护强度有限：** 密钥静态数据仍在 IL 中（三层 XOR + SHA256 派生），可被深度反编译提取，适合基本授权管理场景；
4. **看门狗按进程名监控**，可能匹配同名不同路径进程（当前单实例部署场景可接受）；
5. **日志含点位数/路径等部署信息**，`crash.log`、`logs/`、`health/` 目录需纳入运维访问控制。

### 12.3 可优化方向

1. OPC DA 多服务器实例支持；
2. Modbus 写回（主站 → DA 服务器）；
3. Quality 三态全量传播（接口升级为枚举而非 bool）；
4. 在线授权/证书式授权替代纯离线 HMAC；
5. Modbus 写保护、访问控制白名单。

---

## 13. 完整版本历史

| 版本 | 日期 | 变更 |
|---|---|---|
| 2.2.0 | 2026-09-15 | **P0 端到端验证 + P1 发布治理与安全边界**：① P0 完成真实 OPC DA 服务器（Knight.OPC.Server.Demo）+ Modbus TCP 客户端 42 标签全量读验证，各类型数据转发正确、Double 实时变化；② P1-1 发布包隔离：Keygen 源码移出仓库（本地保留 + .gitignore），主程序默认构建/CI 客户发布包不含 Keygen；③ P1-2 Modbus 网络边界：默认监听地址 `0.0.0.0` → `127.0.0.1`（仅本机回环）；④ 提交 `docs/archify` 架构图。版本统一升级至 2.2.0。编译 0 警告 0 错误。 |
| 2.1.0 | 2026-08-06 | **CSV 导入健壮性修复 + 工程治理**：映射 CSV 导入将全逗号分隔空行误当表头导致数据行错位、点表 CSV 导入将 `",,,,"` 分隔行当首行导致真实表头变为幽灵数据行，两处导入现统一跳过全逗号空行（`line.Trim(',')` 判空）；删除点表浏览对话框遗留死代码（InferRegisterType/FormatModbusAddress）；恢复 Windows CI（restore → build → test → 打包发布）；net472/x86 回归测试增至 33 项。版本统一升级至 2.1.0。编译 0 警告 0 错误。 |
| 2.0.0 | 2026-08-02 | **数据正确性与可靠性里程碑**：统一 Modbus 声明类型、寄存器宽度与高 word 编码，自动地址按四个地址空间分配并校验；配置迁移原子保存并加固热重载；OPC Quality 正确传播 Good/Uncertain/Bad，转换与 Modbus 写入显式报告失败；DA/Modbus 快照分离；修复看门狗优雅退出、重新武装与配置 watcher 并发问题；net472/x86 回归测试增至 29 项。 |
| 1.9.0 | 2026-07-20 | **全面代码审查 + 启动卡顿最终修复 + DA模式切换 + CSV导出标准化**：① DataBridge.StartAsync 异步启动（Task.Run 后台线程创建映射 + SynchronizationContext.Post 进度回调），3.5 万节点场景窗口保持响应；② 新增 DA 数据获取方式选择（异步订阅/同步轮询），UI 下拉框 + 配置持久化；③ 首次运行默认填充 ProgId Matrikon.OPC.Simulation.1，开箱即用；④ 未选择服务器时禁用获取点位和启动网关按钮；⑤ Boolean 类型转换增强（支持字符串 true/1/yes 等）；⑥ SourceTimestamp 单调递增修复（bool 翻转标签可被正确检测）；⑦ Dispose 后重连检查、Monitor.Exit 安全检查、SafeInvoke 句柄防护；⑧ ConfigManager 实现 IDisposable；⑨ 版本号 1.5.0 → 1.9.0；⑩ 删除 PLAN.md（文档整合完成）；⑪ OPC DA 点位浏览导出 CSV 格式对齐模板（A1-A4 固定文本、C3 填点位数、表头 序号,ItemId,DisplayName,DataType,描述）；⑫ 导出映射 CSV 格式优化（删除 sep=,、去除多余空格、GBK 编码兼容 WPS）；⑬ OPC DA 标签类型 Single 导出时转换为 float；⑭ 清理 UA 残留代码（重命名配置节点、移除 UA 专属字段、更新窗口标题）；⑮ 代码审查修复（线程安全锁、接口依赖注入、Modbus 大端序转换、TcpListener.ExclusiveAddressUse、NModbus CreateSlaveNetwork 正确用法）；⑯ 禁用主窗体最大化按钮（FormBorderStyle.FixedSingle + MaximizeBox=false）；⑰ 替换应用图标为 opc-da-modbus-tcp.ico；⑱ 默认监听地址 0.0.0.0、默认端口 502、自动启动 Modbus 服务。编译 0 警告 0 错误。 |
| 1.8.0 | 2026-07-16 | **Modbus TCP 点表导出功能升级**：导出 CSV 增加 DA_DataType、ModbusAddress、Modbus_DataType、EndpointUrl、ModbusPath 列；Modbus 地址格式统一为 0x0001/1x0001/3x0001/4x0001 标准表示法；修复 DA 回调质量判定误用 Error.Succeeded 问题，改用 Quality.Status & 0xC0；三个项目版本号统一升至 1.8.0；编译 0 警告 0 错误 |
| 1.7.0 | 2026-07-15 | **定稿发布**：整合 V1.6.0~V1.6.3 全部变更；时间戳同源统一（DA 回调入口统一取 recvUtc 同时传给 Modbus 存储与本地快照）；监控时间戳显示层 ToLocalTime() 转换；ItemSelectionDialog SafeBeginInvoke 句柄防护；质量判定修复；编译 0 警告 0 错误 |
| 1.6.0 | 2026-07-13 | **新增 OPC DA 同步/异步获取模式**：`OpcDaConfig.Mode`/`GetEffectiveMode()` + UI「数据获取」下拉（`Async`/`Sync`，默认 `Async`）；`OpcDaClient.Start(updateRateMs, mode)` 按模式分支——Async 维持订阅+5分钟兜底，Sync 按 `UpdateRateMs` 定时 `group.Read` 轮询；`TryReconnect` 复用模式 |
| 1.5.0 | 2026-07-04 | **ponytail 代码优化（8项）**：R1 删除 GateController.cs；R2 新建 LicenseManager.cs；R3 MainForm 提取 SafeInvoke；R4 DataBridge 清理冗余；R5 LogManager 精简；R6 HealthSnapshot 内联；R7 GatewayModbusTcpServer.Diag() 标记 DEBUG；三个项目版本号统一升到 1.5.0 |
| 1.4.3 | 2026-07-03 | **ponytail-review 执行**：删除 GatewayFactory.cs；SnapshotData/DailySummary 移至 Models/；ComputeGrowthRate() 改用内存缓存；净减约 89 行 |
| 1.4.2 | 2026-07-03 | **PLAN 3.5 长期内存监控**：HealthSnapshot 升级，DailySummary 17 字段 + 增长率检查 + 90 天轮转 |
| 1.4.1 | 2026-07-03 | **接口抽象与单元测试基础设施**：IOpcDaClient/IGatewayModbusTcpServer/IDataBridge 三个接口；FakeOpcDaClient 测试桩 |
| 1.4.0 | 2026-06-24 | **第二轮代码审查 P0/P1 修复**：RefreshStats 自适应哈希修复；ConfigManager.DoSave 异常恢复；ConvertValue 预编译委托缓存；AppConstants 常量化迁移 |
| 1.3.9 | 2026-06-24 | **N-7 Modbus 映射顺序修复**：DataBridge.Start() 按 `_orderedKeys` 有序列表遍历注册映射 |
| 1.3.8 | 2026-06-23 | **N-5 配置立即持久化**：新增 OnConfigChanged 回调；**N-6 导出字符串拼接优化** |
| 1.3.7 | 2026-06-23 | **Task #35 导出点表升级**；**Task #36 导入后立即分配 TagKey**；**H-26 TitaniumAS 迁移**（Technosoftware 商业库 → TitaniumAS.Opc.Client MIT 开源） |
| 1.3.6 | 2026-06-23 | **定时同步兜底**（5 分钟 DoSyncRead）；**H-25 DA 浏览超时修复**（BrowsePageSize=500）；**UI 诊断过滤** |
| 1.3.5 | 2026-06-18 | 关键安全与健壮性修复（17项CRITICAL）：授权码密钥三层XOR混淆、心跳事件ManualResetEvent修复、看门狗生命周期重设计、SignalGracefulExit机制、托盘退出不关闭看门狗 |
| 1.3.4 | 2026-06-16 | 关键修复与优化：全局异常处理器+crash.log、OnFormClosing 死锁修复、GatewayManager 启动回滚链、DA 回调异常上报、LogManager Dispose 竞态修复、ConcurrentDictionary 线程安全、ConfigManager 定时器复用+锁竞态修复 |
| 1.3.3 | 2026-06-15 | 全面优化：线程安全（volatile+lock）、性能（类型缓存）、资源（IDisposable）、设计（TextBoxExtensions 共享提取） |
| 1.3.2 | 2026-06-15 | LogManager 异步日志写入：BlockingCollection 队列 + 后台线程模式 |
| 1.3.1 | 2026-06-15 | MainForm 职责拆分：提取 LogManager/ConfigManager/WatchdogManager/GatewayManager 四个独立服务类 |
| 1.3.0 | 2026-06-12 | 授权码机制（PCID + HMAC-SHA256）、30 分钟试用倒计时、Keygen 工具（V2.2.0 起源码不随仓库分发） |
| 1.2.0 | 2026-06-12 | 单实例限制、Modbus TCP 可配置设置（监听地址/端口/从站ID）、Costura.Fody DLL 嵌入 |
| 1.1.0 | - | TagKey 机制、DataBridge 线程安全、COM 泄漏修复、异步关闭、BrowseAllItems 去重移除 |
| 1.0.0 | - | 初始版本：DA→Modbus TCP 网关、看门狗、系统托盘、日志、配置管理 |
