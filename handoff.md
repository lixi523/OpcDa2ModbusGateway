# Handoff: OPC DA → Modbus TCP 网关迁移

> **生成时间**: 2026-07-20 22:03 (Asia/Shanghai)
> **项目路径**: `d:\Documents\TRAE Worx\OpcDa2Modbus\`
> **编译状态**: ✅ 0 错误, 0 警告 — 三个项目全部构建通过
> **Git 状态**: 未提交（所有改动在工作区）

---

## 1. 项目目标

将原 `OpcDaToUaGateway`（OPC DA → OPC UA 协议网关）**整体迁移**为 `OpcDaToModbusGateway`（OPC DA → Modbus TCP 协议网关）。

**核心要求**:
1. 将所有 OPC UA 服务器相关导入/依赖/引用替换为 Modbus TCP 对应库（NModbus）
2. 将 UA 地址空间/节点管理替换为 Modbus 寄存器映射（线圈/离散输入/保持寄存器/输入寄存器）
3. 将 UA 读写/订阅替换为 Modbus 功能码操作（01-04 读, 05-06 写, 15-16 批量）
4. 端口从 4840 改为 502，连接参数适配 Modbus TCP
5. 保持原有业务逻辑和数据流向不变，仅替换底层通信协议层
6. DA 点位与 Modbus 点位映射关系靠导出/导入 CSV 表格编辑
7. 所有 `OpcDaToUaGateway` 命名空间重命名为 `OpcDaToModbusGateway`

---

## 2. 当前进度

| 阶段 | 状态 |
|---|---|
| 命名空间全局替换 `OpcDaToUaGateway` → `OpcDaToModbusGateway` | ✅ 完成 |
| NuGet 包替换（移除 OPC UA SDK，添加 NModbus 3.0.81） | ✅ 完成 |
| `GatewayOpcUaServer.cs` → `GatewayModbusTcpServer.cs`（全新实现） | ✅ 完成 |
| `IGatewayOpcUaServer.cs` → `IGatewayModbusTcpServer.cs` | ✅ 完成 |
| `OpcUaConfig` → `ModbusTcpConfig`（TagConfig.cs 内） | ✅ 完成 |
| `DataTypeConverter.cs` 移除 OPC UA `BuiltInType` 依赖 | ✅ 完成 |
| `DataBridge.cs` 从 UA 节点更新改为 Modbus 寄存器写入 | ✅ 完成 |
| `MainForm.cs` UI 面板从 UA 改为 Modbus TCP | ✅ 完成 |
| `ItemSelectionDialog.cs` 移除 UA NodeId 分配，CSV 增加 Modbus 列 | ✅ 完成 |
| `ConfigManager.cs` 移除 UA 安全模式/会话数等向后兼容逻辑 | ✅ 完成 |
| `HealthSnapshot.cs` 从 UA 指标改为 Modbus 指标 | ✅ 完成 |
| `SnapshotData.cs` 新增 `TagSnapshot` 类，字段名更新 | ✅ 完成 |
| `AppConstants.cs` UA 常量改为 Modbus 常量 | ✅ 完成 |
| `Program.cs` / `AboutDialog.cs` 文字更新 | ✅ 完成 |
| 看门狗事件名称/Mutex 名/进程名更新 | ✅ 完成 |
| Keygen 标题更新 | ✅ 完成 |
| 项目文件重命名（`.csproj` / `.sln`） | ✅ 完成 |
| `FodyWeavers.xml` 更新嵌入程序集列表 | ✅ 完成 |
| `config.json` 结构从 `OpcUa` 改为 `ModbusTcp` | ✅ 完成 |
| 临时脚本清理 | ✅ 完成 |
| **编译通过** | ✅ **0 错误 0 警告** |
| 运行时测试 | ❌ 未执行 |
| Git 提交 | ❌ 未执行 |
| 文档更新（开发指南/使用文档/README） | ❌ 未执行 |

---

## 3. 已完成修改

### 3.1 新建文件

| 文件 | 说明 |
|---|---|
| `GatewayModbusTcpServer.cs` | Modbus TCP 从站服务器核心实现，使用 NModbus `ModbusFactory` + `TcpListener` + `DefaultSlaveDataStore`，含 `ModbusRegisterType` 枚举、`ModbusTagMapping` 映射类、4 种寄存器读写、类型转换 |
| `Services/Interfaces/IGatewayModbusTcpServer.cs` | Modbus TCP 服务器接口契约（`IsRunning` / `SlaveId` / `VariableCount` / `StartAsync` / `StopAsync` / `AddVariableNode` / `UpdateValue`） |

### 3.2 删除文件

| 文件 | 替代 |
|---|---|
| `GatewayOpcUaServer.cs` | `GatewayModbusTcpServer.cs` |
| `Services/Interfaces/IGatewayOpcUaServer.cs` | `Services/Interfaces/IGatewayModbusTcpServer.cs` |

### 3.3 重命名文件

| 原名 | 新名 |
|---|---|
| `OpcDaToUaGateway.csproj` | `OpcDaToModbusGateway.csproj` |
| `Watchdog/OpcDaToUaGateway.Watchdog.csproj` | `Watchdog/OpcDaToModbusGateway.Watchdog.csproj` |
| `Keygen/OpcDaToUaGateway.Keygen.csproj` | `Keygen/OpcDaToModbusGateway.Keygen.csproj` |

### 3.4 内容修改的文件

| 文件 | 关键变更 |
|---|---|
| `Models/TagConfig.cs` | `OpcUaConfig` → `ModbusTcpConfig`；移除 UA 安全/会话/命名空间属性；新增 `ModbusAddress` / `ModbusRegisterType` / `GetEffectiveRegisterType()`；`AppConfig.OpcUa` → `AppConfig.ModbusTcp` |
| `Models/DataTypeConverter.cs` | 移除 `using Opc.Ua`；`BuiltInType` → 自定义 `DataTypeId` 枚举 |
| `Models/SnapshotData.cs` | 新增 `TagSnapshot` 类（含 `TagKey`/`ItemId`/`DisplayName`/`DataType`/`ModbusAddress`/`ModbusRegisterType`/`Value`/`Quality`/`Timestamp`）；`UaVariableCount` → `ModbusVariableCount`，`UaNamespaceIndex` → `ModbusSlaveId` |
| `DataBridge.cs` | `_uaServer` → `_modbusServer`；UA 节点更新 → `AddVariableNode(tagKey, modbusAddress, registerType, initialValue)` + `UpdateValue(tagKey, value, isGood, timestamp)` |
| `MainForm.cs` | `Config.OpcUa` → `Config.ModbusTcp`；UA 安全/证书/会话 UI 控件逻辑注释掉；`_gateway.UaServer` → `_gateway.ModbusServer`；`NamespaceIndex` → `SlaveId` |
| `Services/GatewayManager.cs` | `IGatewayOpcUaServer` → `IGatewayModbusTcpServer`；`_uaServer` → `_modbusServer`；`UaServer` → `ModbusServer` |
| `Services/ConfigManager.cs` | 移除 UA `SecurityMode`/`SecurityPolicy`/`AutoAcceptCertificates`/`MaxSessionCount`/`SessionTimeout` 向后兼容逻辑 |
| `Services/HealthSnapshot.cs` | `_gateway.UaServer` → `_gateway.ModbusServer`；`NamespaceIndex` → `SlaveId`；`DaTagsChildren` 移除 |
| `Services/WatchdogManager.cs` | 事件名称从 `OpcDaToUaGateway_*` → `OpcDaToModbusGateway_*`；进程名更新 |
| `AppConstants.cs` | UA 常量段改为 Modbus 常量段；`UaDefaultPort=4840` → `ModbusDefaultPort=502` |
| `Program.cs` | 窗口标题、Mutex 名称更新 |
| `AboutDialog.cs` | 文字 "OPC UA" → "Modbus TCP"；技术栈 "OPC Foundation UA SDK" → "NModbus4" |
| `ItemSelectionDialog.cs` | 移除 `_namespaceIndex` 参数；`UaNodeId` → `ModbusAddress`；CSV 增加 Modbus 地址/寄存器类型列 |
| `OpcDaToModbusGateway.csproj` | 移除 OPC UA 包引用，添加 `NModbus 3.0.81`；`RootNamespace`/`AssemblyName` 更新 |
| `Watchdog/OpcDaToModbusGateway.Watchdog.csproj` | `AssemblyName`/`RootNamespace`/`Product`/`Description` 更新 |
| `Keygen/OpcDaToModbusGateway.Keygen.csproj` | 同上 |
| `OpcDaToUaGateway.sln` | 项目显示名更新（文件名未改） |
| `FodyWeavers.xml` | 移除 OPC UA 程序集列表，添加 `NModbus`/`NModbus4` |
| `config.json` | `OpcUa` 节 → `ModbusTcp` 节；端口 4840 → 502；Tags 增加 `ModbusAddress`/`ModbusRegisterType` 字段；`AutoStartUa` → `AutoStartModbus` |
| `Watchdog/Program.cs` | 命名事件名称、主程序路径兼容新旧名 |
| `Keygen/Program.cs` | 控制台标题更新 |

---

## 4. 关键文件

```
d:\Documents\TRAE Worx\OpcDa2Modbus\
├── OpcDaToModbusGateway.csproj      ← 主项目（net472, x86, WinExe）
├── OpcDaToUaGateway.sln              ← 解决方案（文件名未改，内容已更新）
├── Program.cs                        ← 入口（单实例 Mutex）
├── MainForm.cs                       ← 主窗体（~1100行，UI + 协调）
├── GatewayModbusTcpServer.cs        ← ⭐ Modbus TCP 从站核心
├── DataBridge.cs                     ← ⭐ DA→Modbus 数据桥接
├── OpcDaClient.cs                    ← DA 客户端（未改动）
├── OpcServerScanner.cs               ← DA 服务器扫描（未改动）
├── ItemSelectionDialog.cs            ← DA 点位选择 + CSV 导入导出
├── ServerSelectionDialog.cs           ← DA 服务器选择
├── AboutDialog.cs                    ← 关于/授权
├── AppConstants.cs                   ← 全局常量
├── Theme.cs / TextBoxExtensions.cs   ← UI 辅助
├── config.json                       ← 运行时配置
├── FodyWeavers.xml                   ← Costura DLL 嵌入配置
├── Models/
│   ├── TagConfig.cs                  ← ⭐ 配置模型（TagConfig + ModbusTcpConfig + AppConfig）
│   ├── DataTypeConverter.cs          ← 数据类型转换（移除 UA 依赖）
│   ├── SnapshotData.cs               ← TagSnapshot + SnapshotData（健康快照）
│   ├── DailySummary.cs               ← 日聚合（未改动）
│   └── LicenseAlgorithm.cs          ← 授权算法（未改动）
├── Services/
│   ├── GatewayManager.cs             ← ⭐ 网关生命周期管理
│   ├── ConfigManager.cs              ← 配置文件管理
│   ├── LogManager.cs                 ← 日志管理（未改动）
│   ├── HealthSnapshot.cs             ← 健康快照采集
│   ├── LicenseManager.cs             ← 授权管理（未改动）
│   ├── WatchdogManager.cs            ← 看门狗管理
│   └── Interfaces/
│       ├── IGatewayModbusTcpServer.cs ← ⭐ Modbus 服务器接口
│       ├── IDataBridge.cs             ← 数据桥接接口
│       ├── IOpcDaClient.cs            ← DA 客户端接口（未改动）
│       ├── IHealthSnapshot.cs        ← 健康快照接口（未改动）
│       └── FakeOpcDaClient.cs         ← 测试桩（未改动）
├── Watchdog/
│   ├── Program.cs                    ← 看门狗进程
│   └── OpcDaToModbusGateway.Watchdog.csproj
├── Keygen/
│   ├── Program.cs                    ← 授权码工具
│   └── OpcDaToModbusGateway.Keygen.csproj
└── bin/Release/net472/
    ├── OpcDaToModbusGateway.exe      ← ✅ 已编译产出
    ├── OpcDaToModbusGateway.Watchdog.exe
    └── OpcDaToModbusGateway.Keygen.exe
```

---

## 5. 不能动的边界

以下文件/模块在迁移中**未做任何改动**，不得修改：

| 文件/模块 | 原因 |
|---|---|
| `OpcDaClient.cs` | OPC DA 客户端实现，COM 互操作复杂，保持原样 |
| `OpcServerScanner.cs` | DA 服务器扫描逻辑，与协议层无关 |
| `Models/LicenseAlgorithm.cs` | 授权码算法，主程序和 Keygen 共享 |
| `Models/DailySummary.cs` | 健康日聚合数据结构 |
| `Services/LogManager.cs` | 日志管理器 |
| `Services/LicenseManager.cs` | 授权管理器 |
| `Services/Interfaces/IOpcDaClient.cs` | DA 客户端接口 |
| `Services/Interfaces/IHealthSnapshot.cs` | 健康快照接口 |
| `Services/Interfaces/FakeOpcDaClient.cs` | 测试桩 |
| `Theme.cs` / `TextBoxExtensions.cs` | UI 辅助类 |
| `ServerSelectionDialog.cs` | DA 服务器选择对话框 |
| `app.ico` / `app.manifest` | 图标和清单文件 |

---

## 6. 已经否掉的方案

| 方案 | 否掉原因 |
|---|---|
| 使用 `EasyModbusTCP` 库 | API 不提供 Slave/Server 端实现，仅 Client 端 |
| 使用 `NModbus4`（旧版） | 命名空间为 `Modbus.*`，与新版 `NModbus.*` 不同；最终选择 `NModbus 3.0.81`（命名空间 `NModbus.*`） |
| 保留 OPC UA 安全模式/证书管理 UI | Modbus TCP 无原生安全层，全部移除/注释 |
| 在 `TagConfig` 中保留 `UaNodeId` 字段做向后兼容 | 已完全移除，替换为 `ModbusAddress` + `ModbusRegisterType` |
| PowerShell `Set-Content` 批量替换文件 | **严重编码损坏**——PowerShell 的 `Set-Content` 默认编码会破坏含中文的 C# 文件（产生 `\xe2\x80?` 等坏字节），导致 CS1010/CS1003 编译错误。**最终改用 Python 脚本 + `[System.IO.File]::ReadAllText/WriteAllText` (UTF-8)** 完成所有替换 |
| `edit_file` 工具直接编辑含中文行 | 同样产生编码损坏（部分中文字符被替换为 `?`），**不可用于含中文的 C# 文件**。改用 `write_file`（完整重写）或 Python 脚本 |

---

## 7. 当前风险点

| 风险 | 严重度 | 说明 |
|---|---|---|
| **运行时未验证** | 🔴 高 | 编译通过但未实际运行，NModbus `ModbusTcpSlaveNetwork` 的 `ListenAsync` 是否正确启动监听未验证 |
| **MainForm UI 控件遗留** | 🟡 中 | `_cmbSecurityMode` / `_nudMaxSessions` / `_chkAutoAcceptCerts` 等控件仍被创建和布局，只是事件处理器被注释掉。UI 上会显示这些控件但不可用 |
| **`GatewayModbusTcpServer.AddVariableNode` 签名不匹配** | 🟡 中 | 接口定义 `AddVariableNode(string, ushort, ModbusRegisterType, object)`，但 `DataBridge.cs` 调用签名可能不一致（需核对） |
| **`DataTypeConverter.cs` 的 `DataTypeId` 枚举** | 🟡 中 | 原 UA `BuiltInType` 被替换为自定义 `DataTypeId` 枚举，但枚举值和转换逻辑可能与 DA 客户端返回的类型不匹配 |
| **CSV 导入导出 Modbus 列** | 🟡 中 | `ItemSelectionDialog.cs` 的 CSV 导出已增加 Modbus 地址/寄存器类型列，但导入时是否正确解析这两列并赋值给 `TagConfig` 需验证 |
| **`.sln` 文件名未改** | 🟢 低 | `OpcDaToUaGateway.sln` 文件名仍为旧名，内容已更新。不影响编译，但建议重命名 |
| **Costura 嵌入 NModbus** | 🟢 低 | `FodyWeavers.xml` 已列出 `NModbus`/`NModbus4`，但实际 NModbus 3.0.81 的 DLL 名是否匹配需验证 |
| **Git 未提交** | 🟢 低 | 所有改动在工作区，未做 commit |

---

## 8. 已经跑过的测试

| 测试 | 结果 |
|---|---|
| `dotnet restore` | ✅ NModbus 3.0.81 还原成功 |
| `dotnet build -c Release`（主项目） | ✅ 0 错误 0 警告 |
| `dotnet build -c Release`（Watchdog 子项目） | ✅ 0 错误 0 警告 |
| `dotnet build -c Release`（Keygen 子项目） | ✅ 0 错误 0 警告 |
| 运行时启动测试 | ❌ 未执行 |
| Modbus TCP 客户端连接测试 | ❌ 未执行 |
| DA 订阅 → Modbus 寄存器数据流测试 | ❌ 未执行 |
| CSV 导入导出测试 | ❌ 未执行 |
| 看门狗崩溃重启测试 | ❌ 未执行 |

---

## 9. 下一步计划

### P0 — 运行时验证（优先）

1. **启动主程序** `OpcDaToModbusGateway.exe`，确认窗口正常显示
2. **验证 Modbus TCP 服务器启动**：点击"启动"按钮，用 Modbus Poll 或 `modbus-cli` 连接 `localhost:502`，读取保持寄存器
3. **验证 DA → Modbus 数据流**：连接 Matrikon OPC DA Simulation 服务器，选择点位，启动网关，在 Modbus Poll 中观察寄存器值变化
4. **验证 CSV 导入导出**：导出点表 → 在 Excel 中编辑 Modbus 地址 → 重新导入 → 确认地址映射正确

### P1 — UI 清理

5. 移除 MainForm 中仍被创建但不可用的 OPC UA 控件（`_cmbSecurityMode` / `_nudMaxSessions` / `_chkAutoAcceptCerts`），替换为 Modbus TCP 相关控件（从站 ID NumericUpDown 等）
6. 将 `.sln` 文件名从 `OpcDaToUaGateway.sln` 重命名为 `OpcDaToModbusGateway.sln`

### P2 — 健壮性

7. 核对 `DataBridge.cs` 调用 `AddVariableNode` 的参数签名与 `IGatewayModbusTcpServer` 接口定义是否一致
8. 核对 `DataTypeConverter.cs` 的 `DataTypeId` 枚举与 DA 客户端实际返回类型的映射
9. 验证 Costura 嵌入的 NModbus DLL 名是否正确（检查 `bin/Release/net472/` 下是否有 `NModbus.dll`）

### P3 — 收尾

10. 更新文档（`README.md` / `OPC_DA转UA网关开发指南.md` / `使用文档.md`）
11. Git commit + push
12. 清理 `bin/` `obj/` 目录，做一次干净编译验证

---

## 10. 新窗口启动提示词

将以下内容粘贴到新窗口的第一条消息中：

```
源码在 d:\Documents\TRAE Worx\OpcDa2Modbus\ 目录下。

这是一个 OPC DA → Modbus TCP 协议转换网关项目（从 OPC DA → OPC UA 迁移而来），使用 .NET Framework 4.7.2 / x86 / WinForms / NModbus 3.0.81。

当前状态：编译已通过（0 错误 0 警告），但运行时未验证。

请先阅读 d:\Documents\TRAE Worx\OpcDa2Modbus\handoff.md 了解完整迁移上下文，然后帮我继续后续工作。

第一步：运行 OpcDaToModbusGateway.exe 验证程序能否正常启动，Modbus TCP 服务器能否在端口 502 上监听。

如遇到问题，关键文件：
- GatewayModbusTcpServer.cs — Modbus TCP 从站核心
- DataBridge.cs — DA→Modbus 数据桥接
- Models/TagConfig.cs — 配置模型（含 ModbusTcpConfig）
- Services/GatewayManager.cs — 网关生命周期管理
- MainForm.cs — 主窗体 UI

注意：
- 不要用 PowerShell Set-Content 或 edit_file 编辑含中文的 C# 文件（会损坏编码），用 Python 脚本或 [System.IO.File]::ReadAllText/WriteAllText (UTF-8)
- OpcDaClient.cs / LicenseAlgorithm.cs 等文件未做改动，不要修改
- 编译命令：cd /d d:\Documents\OpenSquilla\OpcDa2Modbus && dotnet build OpcDaToModbusGateway.csproj -c Release
```
