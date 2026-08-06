# Handoff：OPC DA → Modbus TCP 网关

> 生成时间：2026-08-06
> 版本：V2.1.0
> 适用：新窗口继续任务

---

## 1. 项目目标

开发一个 Windows 桌面网关程序，将 OPC DA 服务器的数据实时转换到 Modbus TCP 从站协议对外提供访问。
核心能力：
- 连接任意 OPC DA 服务器（通过 ProgID），浏览并选择标签
- 将 OPC DA 标签映射到 Modbus 地址（线圈/离散输入/保持寄存器/输入寄存器）
- 启动 Modbus TCP 从站服务，供上位机 SCADA 通过 Modbus TCP 读取数据
- 支持 CSV 导出/导入标签配置与 Modbus 映射
- 单文件部署（Costura.Fody 嵌入依赖 DLL）
- 看门狗守护进程，异常自动拉起

---

## 2. 当前进度

| 模块 | 状态 |
|------|------|
| OPC DA 客户端连接与浏览 | ✔ 完成（含 CanonicalDataType 真实类型回写）|
| Modbus TCP 从站服务 | ✔ 完成（NModbus 3.0.81，声明类型/宽度/高 word 编码统一）|
| DA → Modbus 数据桥接 | ✔ 完成（显式写入结果 + DA/MB 分离快照）|
| OPC Quality 传播 | ✔ 完成（helper 三态分类；桥接仅 Good/非 Good 处理）|
| 主窗口监控表格 | ✔ 完成（DA/MB 分列显示，含实时缓存）|
| CSV 导出/导入（标签/映射分离）| ✔ 完成（GBK 编码，WPS 兼容，TagKey 身份）|
| 配置持久化与迁移 | ✔ 完成（config.json + tags.json 原子保存，热重载加固）|
| 看门狗进程 | ✔ 完成（优雅退出状态机 + 重新武装）|
| 构建与自动化测试 | ✔ Release 0 错误 0 警告；MSTest 33/33 通过；Windows CI 已恢复 |
| 文档 | ✔ README / 开发指南 / handoff 已更新至 V2.1.0 |
| **运行时验证（连接真实 DA 服务 + Modbus 客户端读取）** | ❌ **尚未进行（P0 阻塞项）** |

---

## 3. 已完成修复
### 3.1 版本升级至 V2.0.0
- 三个生产项目（主程序、Watchdog、Keygen）`Version=2.0.0`、`AssemblyVersion/FileVersion=2.0.0.0`
- `AppConstants.AppVersion = "2.0.0"`，Keygen banner 同步
- Watchdog/Keygen 产品元数据中的 OPC UA 残留已清除
- README、开发指南、handoff 当前版本与版本历史已更新

### 3.2 Modbus 类型、宽度与字节序统一
- `ModbusDataType` 进入运行时链路，注册节点时传给 server 并按声明类型编码（不再按 CLR 类型猜测）
- 支持 `Bool / Byte / SByte / Int16 / UInt16 / Int32 / UInt32 / Float(Single) / Double`
- 16-bit 1 register、32-bit 2 registers、64-bit 4 registers；多 word 高 word 在前
- `String`、`DateTime` 无 wire encoding，启动映射校验明确拒绝并指出标签
- `UInt16/UInt32` 不再被错误推断为有符号类型
- Modbus TCP 启动失败路径清理 listener/network/CTS/datastore，避免端口残留

### 3.3 地址分配与映射验证
- 自动地址按四个地址空间（Coil/DiscreteInput/HoldingRegister/InputRegister）分别从 0 顺序推进，按声明类型宽度递增
- 启动前和 CanonicalDataType 回写后各执行一次 `ValidateMappings`：检查地址溢出与同空间重叠
- `TagKey` 持久化于 `tags.json`；CSV 第一列使用 `<序号>|<Base64Url(TagKey)>` 稳定身份，旧纯数字格式仅在无重复 ItemId 时兼容

### 3.4 数据正确性（P0 修复批次）
- **配置迁移不丢标签**：`SaveAllImmediate()` 先原子写 tags.json 再写 config 快照；热重载保持 `AppConfig` 根引用稳定
- **OPC Quality**：`OpcQualityHelper` 按高两位分类 Good/Uncertain/Bad；`Error.Succeeded` 不再替代 Quality；Bad/Uncertain 有值
- **显式失败**：`DataTypeConverter.ConvertValue` 不再吞异常写零；`IGatewayModbusTcpServer.UpdateValue` 返回 `ModbusWriteResult`
- **DA/MB 快照分离**：`TagSnapshot` 分离 DA（DaValue/DaQuality/DaTimestamp）与 MB（ModbusValue/ModbusStatus/ModbusLastSuccess）

### 3.5 看门狗修复
- 优雅退出后进入持久 `WaitingForManualStart` 状态，主程序缺席期间持续抑制重启
- 主程序重新出现（或优雅信号被新实例清除）后重新武装崩溃守卫
- 补充“手工启动后首个崩溃不拉起”测试
- 独立 `WatchdogRestartPolicy` 纯逻辑类，便于单元测试

### 3.6 自动化测试
- 新增 MSTest 测试项目 `Tests/OpcDaToModbusGateway.Tests.csproj`（net472）
- 覆盖：类型宽度/编码、CSV 身份、DA/MB 快照、配置迁移回滚、Quality 分类、Watchdog 状态机等，共 33 项

### 3.7 开发指南结构化重写
- `OPC_DA转ModbusTCP网关开发指南.md` 以 V2.0.0 当前代码为事实源重建，共 13 章（约 736 行）
- 清除正文中 V1.x 旧实现描述；完整版本历史表保留（2.0.0 → 1.0.0）
- 明确纠偏：四项目组成、TagKey 持久化、String/DateTime 拒绝、Quality 三态 helper 但桥接仅 bool、tags.json 不参与 watcher 热重载、DA 首次失败可降级运行

### 3.8 V2.0.0 变更集审查修复（子代理复核 6 项）
- **#1（HIGH，确认）** 映射导出 `",,,,,,"` 分隔行被导入误当表头 → 数据行错位抛"序号/TagKey 格式无效"。已修：`MainForm.BtnImportCsvMapping_Click` 起始行扫描与数据循环均跳过全逗号行（`line.Trim(',')` 判空）
- **#2（否）** 子代理称存在旧 9 列 Modbus 映射格式需列平移——已用 `git show 472c1b3` 核实：V1.9.0 导出为 UA 格式与 5 列点表，从未有 9 列 Modbus 映射；文档定义"旧格式"= 同 7 列布局下的纯数字序号身份（`MappingCsvIdentity.ValidateLegacyItemIds`），现有导入已正确处理
- **#3（确认）** 点表导出 `",,,,"` 分隔行被导入当"首个非注释行"→ 真实表头变幽灵数据行（ItemId="ItemId"）。已修：`ItemSelectionDialog.BtnImportCsv_Click` 跳过全逗号行
- **#4（保留）** 默认监听 localhost→0.0.0.0 属 V2.0.0 有意设计（ConfigManager.cs:217 注释 + hard constraint #5），用户确认保持 0.0.0.0
- **#5（确认）** `ItemSelectionDialog` 私有方法 `InferRegisterType`/`FormatModbusAddress` 无任何调用者，已删除（`MainForm.FormatModbusAddress` 在用，未动）
- **#6（否）** `BtnOK_Click` 地址溢出已有显式守卫（`nextAddress + width - 1 > ushort.MaxValue` 抛 `InvalidOperationException`），且 MainForm 调用方有 catch → 无未捕获溢出

### 3.9 版本升级至 V2.1.0（2026-08-06）
- 三个生产项目（主程序、Watchdog、Keygen）`Version=2.1.0`、`AssemblyVersion/FileVersion=2.1.0.0`
- `AppConstants.AppVersion = "2.1.0"`，Keygen banner 同步（v2.1.0）
- 版本历史：README、开发指南、handoff 顶部当前版本与版本历史表首行已更新（新增 2.1.0 行，2.0.0 及更早历史行保留）
- 开发指南正文版本边界切换至 V2.1.0，核心变更新增 CSV 导入健壮性/死代码清理/CI 恢复条目，测试分布更新为 33 项（ModbusCorrectnessTests 24、OpcQualityTests 5、ConfigMigrationTests 2、WatchdogRestartPolicyTests 2）

---

## 4. 关键文件

| 文件 | 说明 |
|------|------|
| `OpcDaToModbusGateway.csproj` | 主项目，net472/x86，Version 2.1.0；Costura.Fody 嵌入依赖 |
| `AppConstants.cs` | 全局常量，AppVersion=2.1.0 |
| `Models/DataTypeConverter.cs` | DA 类型解析、Modbus 类型规范化、宽度/高 word 编码 |
| `Models/OpcQualityHelper.cs` | OPC Quality 高两位三态分类 |
| `Models/ModbusWriteResult.cs` | Modbus 写入结果枚举与错误消息 |
| `Models/SnapshotData.cs` | TagSnapshot：DA/MB 分离字段 |
| `Models/TagConfig.cs` | 标签配置、TagKey 持久化、有效宽度 |
| `Models/LicenseAlgorithm.cs` | PCID + HMAC-SHA256 授权码（三层 XOR 混淆）|
| `OpcDaClient.cs` | OPC DA 客户端：订阅/轮询、CanonicalDataType 回写、Quality 传播 |
| `DataBridge.cs` | DA → Modbus 桥接：显式写入结果、分离快照、Start 幂等 |
| `GatewayModbusTcpServer.cs` | NModbus TCP 从站：声明类型编码、写结果、锁保护 |
| `Services/GatewayManager.cs` | 生命周期管理：三阶段启动、降级运行、CheckHealth 重连、映射校验 |
| `Services/ConfigManager.cs` | config.json/tags.json 原子保存、迁移、热重载 watcher |
| `Services/WatchdogManager.cs` | 看门狗子进程管理、心跳、SignalGracefulExit |
| `Watchdog/Program.cs` | 看门狗进程主循环、重启策略执行 |
| `Watchdog/WatchdogRestartPolicy.cs` | 优雅退出状态机（可测试纯逻辑）|
| `Services/MappingCsvIdentity.cs` | CSV 序号与 TagKey 编码/解析、旧格式兼容校验 |
| `Tests/` | MSTest 测试项目（33 项）|
| `config.json` | 网关配置（不含 Tags）|
| `tags.json` | 标签配置（运行时生成/迁移）|
| `README.md` | 项目说明与版本历史 |
| `OPC_DA转ModbusTCP网关开发指南.md` | V2.1.0 结构化开发指南（13 章）|
| `docs/superpowers/specs|plans/` | 各轮设计文档与实施计划 |

---

## 5. 不能动的边界

以下规则是项目的 hard constraint，修改前必须确认。
1. **Modbus 地址格式**：必须使用标准 Modbus 地址表示法：`0x0001`(Coil)、`1x0001`(Discrete Input)、`3x0001`(Holding Register)、`4x0001`(Input Register)。导出映射时地址值从 0 起始（0-based 内部地址 + 1-based UI 显示）。
2. **导出点表 CSV 格式**（ItemSelectionDialog）：
   - 首行固定格式：`#不用管#服务器,xxx,,` / `#不用管#导出时间:,xxx,,` / `#修改C3,#已选点数,N,,`
   - 第二行空行：`,,,,`
   - 第三行表头：`序号,ItemId,DisplayName,DataType,描述`
   - 编码为 **GBK**（WPS 兼容，不可改为 UTF-8）
   - `Single` 类型必须转换为 `float` 输出
   - 不可添加 `sep=,` 首行
3. **导出映射 CSV 格式**（MainForm）：
   - 表头固定：`序号,DA_ItemId,DisplayName,DA_DataType,Modbus_Function,Modbus_Address,Modbus_DataType`（7 列）
   - 第一列含 `<序号>|<Base64Url(TagKey)>` 稳定身份；旧纯数字格式仅在无重复 ItemId 时兼容
   - 编码为 **GBK**
   - 不可添加 `sep=,` 首行
   - 不可有多余空格（如 `导出时间:` 后、`Modbus_DataType` 前）
4. **OPC DA 数据类型检查**：必须从 `OpcDaItem.CanonicalDataType` 获取（在 AddItems 加入 Group 之后），浏览阶段不可信
5. **Modbus TCP 默认监听**：`0.0.0.0:502`
6. **目标框架**：`net472` + `x86`（OPC DA COM 组件通常为 32 位）
7. **DLL 嵌入**：使用 Costura.Fody 实现单 EXE 部署
8. **窗口样式**：`FormBorderStyle = FormBorderStyle.FixedSingle` + `MaximizeBox = false`（主窗体禁止最大化）
9. **支持的 Modbus wire type**：Bool/Byte/SByte/Int16/UInt16/Int32/UInt32/Float/Double；**String/DateTime 没有 wire encoding，启动时拒绝**
10. **多字节编码**：高 word 在前；不支持配置 byte/word swap
11. **Quality 传播**：helper 识别三态，但 `IOpcDaClient.OnDataChanged` 当前仅传 `bool isGood`，Uncertain 与 Bad 统一按非 Good 处理（若需三态贯通 UI，必须同步改接口和快照）

---

## 6. 已经否掉的方案
- ~~在 ItemSelectionDialog 的 CSV 中同时处理 Modbus 映射~~ → 已分离到 MainForm 的专用映射导出/导入按钮，保持关注点分离
- ~~从浏览阶段直接信任服务器返回的 DataType~~ → 必须创建临时 Group 通过 `CanonicalDataType` 二次确认
- ~~CSV 使用 UTF-8 with BOM~~ → WPS 打开时 BOM 字符导致列分隔失败，改为 GBK
- ~~CSV 首行添加 `sep=,`~~ → WPS 无法识别，导致列内容挤入 A 列，已删除
- ~~引号包裹 CSV 中的特殊字符~~ → WPS 解析异常，不加引号
- ~~时间格式 `yyyy-MM-dd HH:mm:ss`~~ → 模板用 `yyyy-M-d HH:mm`（不带前导零），已对齐
- ~~版本升级采用全局字符串替换~~ → 会篡改历史记录；改为精准升级，历史行保留
- ~~开发指南继续局部打补丁~~ → 正文 V1.x 描述与当前行为矛盾过多，采用结构化整篇改写
- ~~依据 grep 乱码输出做转码~~ → 乱码属工具显示层解码错配，盲目转码会造成二次损坏

---

## 7. 当前风险
### 🔴 P0 项：尚未运行时验证
- 构建与单测通过，但**未连接真实 OPC DA 服务器 + Modbus TCP 客户端进行端到端测试**。
- 验证方式：启动程序 → 连接 Matrikon OPC Simulation 或 Knight OPC Server Demo → 添加标签 → 启动 Modbus TCP → 用 Modbus Poll 或 Modscan 连接 `0.0.0.0:502`，读取映射地址，确认数据正确；验证 Good/Bad Quality 传播、DA 断线重连、看门狗拉起。
### 🟡 P1 项：发布治理与安全边界
- **发布包隔离**：主项目构建仍会构建并复制 Keygen 到输出目录；客户发布包应使用白名单，不随包下发 Keygen。
- **Modbus 写入风险**：服务创建可读写 slave 并暴露 `0.0.0.0:502`；外部客户端可写寄存器，生产网络需用防火墙/ACL 限定访问范围。
- **授权算法**：HMAC 共享密钥方案，深度逆向可被破解（源码已注明），长期应迁移 ECDSA 非对称签名。
### 🟡 P2 项：已知限制（文档已列）
- `tags.json` 外部修改不会触发当前 watcher 热重载（仅监听 config.json）。
- 无 Modbus 写回 OPC DA 支持（当前为 DA → Modbus 只读方向）。
- `csharp-ls` 对 net472/WinForms 项目存在基础引用级联误报；实际 MSBuild/MSTest 通过，调试诊断需以构建为准。

---

## 8. 已经跑过的测试
| 测试项 | 结果 | 备注 |
|--------|------|------|
| Release 构建 | ✔ 通过 | 0 错误 0 警告 |
| MSTest（net472/x86）| ✔ 33/33 通过 | 编码、宽度、Quality、迁移、Watchdog、快照 |
| CSV 格式验证 | ✔ 通过 | GBK 编码，WPS 打开列分隔正常 |
| 导出点表格式 | ✔ 通过 | 首行固定格式 + 表头，Single→float |
| 导出映射格式 | ✔ 通过 | 7 列表格 + TagKey 身份，无 sep=, |
| 导入映射 | ✔ 通过 | 新格式按 TagKey，旧格式仅无重复时兼容 |
| 配置迁移 | ✔ 通过 | 旧内容 Tags 迁移不丢，写失败回滚 |
| Modbus 端口监听 | ✔ 通过 | netstat 确认 502 端口监听（历史验证） |
| 端到端运行时验证 | ❌ 未进行 | P0 阻塞项 |

---

## 9. 下一步计划
优先级从高到低：

1. **P0 项：运行时端到端验证**
   - 启动程序，连接本地 Knight.OPC.Server.Demo（或 Matrikon OPC Simulation）
   - 浏览标签，确认真实数据类型显示正确（非 Variant）
   - 启动 Modbus TCP，用 Modbus Poll / Modscan / NModbus 客户端读取映射地址（0x/1x/3x/4x），验证数据转发正确
   - 验证 Good/Uncertain/Bad Quality 传播、DA 断线自动重连、写失败状态显示
   - 验证实时数据刷新（监控表格 DA/MB 分列随源变化）
2. **P1 项：发布治理**
   - 客户发布包与内部工具隔离（不随包下发 Keygen）
   - 明确 Modbus 网络访问边界（防火墙/ACL/可配置监听地址）
   - 评估授权方案迁移 ECDSA 非对称签名
3. **P2 项：可选优化**
   - 看门狗进程保活与优雅退出现场验证
   - 日志清理与轮转验证
   - 大数据量监控表格虚拟模式性能
   - `tags.json` 热重载支持（需扩展 watcher 范围并处理与运行中映射的一致性）

---

## 10. 新窗口启动提示词

```
继续开发 OPC DA → Modbus TCP 网关项目。工作目录：D:\Documents\Reasonix\OpcDa2Modbus
版本：V2.1.0

请先读取 handoff.md 确认上下文。
当前状态：
- Release 构建通过，0 错误 0 警告；MSTest 33/33 通过。
- V2.0.0 数据正确性与可靠性修复已完成，开发指南已结构化重写；V2.1.0 已完成 CSV 导入健壮性修复与版本升级。
- V2.0.0 变更集子代理审查 6 项已全部处理（2 项修复、2 项核实为否、1 项保留默认、1 项删死代码）。
- 工作区存在大量未提交改动（涉及多轮功能与修复），历史记录与 hard constraints 已保留。
- P0 阻塞项：尚未进行真实 OPC DA 服务器 + Modbus 客户端端到端验证。
下一步任务：
1. P0：运行时端到端验证（连接真实 DA 服务器，用 Modbus 客户端工具验证数据转发）
2. P1：发布治理（发布包隔离、网络边界、授权方案评估）
3. P2：可选优化（看门狗现场验证、日志轮转、tags.json 热重载等）
编译命令：dotnet build --configuration Release
测试命令：dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release -p:PlatformTarget=x86
输出目录：bin\Release\net472\OpcDaToModbusGateway.exe
```
