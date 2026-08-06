# Modbus 正确性修复设计

## 目标

修复当前 OPC DA → Modbus TCP 数据链路中已确认的正确性问题，同时保持 `net472`、`x86`、现有配置和 7 列 GBK CSV 格式兼容。

## 范围

1. 首次 OPC DA 连接失败后可由健康检查恢复，并继续向既有 DataBridge 推送数据。
2. `ModbusDataType` 进入运行时编码链路。
3. 统一 Modbus 数据类型的寄存器宽度与大端 word 编码。
4. 自动地址分配按地址空间和类型宽度推进，阻止溢出。
5. 重复 `ItemId` 的 CSV 映射可无歧义导入。
6. 临时 OPC Group 在异常路径也释放。
7. 添加最小 `net472/x86` 自动化测试。

不定义 `String` 与 `DateTime` 的 wire encoding；遇到这两类映射时启动失败并明确提示标签。

## 设计

### 类型和编码

以 `TagConfig.GetEffectiveModbusDataType()` 为唯一声明类型。`DataBridge` 注册节点时将声明类型传给 `IGatewayModbusTcpServer.AddVariableNode`，服务器保存在 tag mapping 中；更新值时按声明类型转换与编码，不再依赖传入对象的 CLR 类型猜测。

支持：

- `Bool`
- `Byte` / `SByte` / `Int16` / `UInt16`：1 register
- `Int32` / `UInt32` / `Float` / `Single`：2 registers
- `Double`：4 registers

32/64 位数值按高 word 在前写入。浮点先取得 IEEE 754 位模式，再按相同 word 顺序拆分。Coil 和 DiscreteInput 固定占一个 bit 地址。

### 地址分配

自动分配按四个 Modbus 地址空间分别维护 next address。Coil/Discrete 每个加 1；Holding/Input register 根据有效类型宽度递增。分配前检查末地址不超过 `ushort.MaxValue`。不支持类型在确认点位或启动时给出明确错误。

### 首次连接失败恢复

`GatewayManager` 创建 `OpcDaClient` 后，即使首次 `Start` 失败也保留该实例，不 Dispose、不设为 null。`DataBridge` 订阅同一个客户端，`CheckHealth` 调用其 `TryReconnect`；重连后事件自然进入原 bridge。

`OpcDaClient` 保存最近一次 acquisition mode，重连沿用 Async/Sync 配置。健康检查增加重入保护，退避期间不增加尝试次数。

### CSV 身份兼容

表头、列数、字段顺序和 GBK 编码保持不变。新导出在第一列“序号”写：

```text
<序号>|<Base64Url(TagKey)>
```

新格式按 `TagKey` 精确导入，并校验该行 `DA_ItemId`。旧格式第一列为纯数字时继续按 `ItemId` 导入；若 CSV 或当前配置中 `ItemId` 重复，则拒绝导入并提示旧格式无法区分，不再静默覆盖。

### COM 资源释放

`FillRealDataTypes` 在外层声明临时 Group，在 `finally` 中分别执行 `RemoveGroup` 和 `Dispose`，成功和异常路径一致释放。

### 测试

新增 `Tests/OpcDaToModbusGateway.Tests.csproj`，目标 `net472/x86`。主项目排除 `Tests/**` 编译项。测试至少覆盖：

- 声明 Modbus 类型的宽度和编码；
- Int16 单寄存器、32/64 位高 word 在前；
- DataBridge 将有效 Modbus 类型传入 server；
- 地址宽度推进和溢出；
- CSV 新旧身份解析及重复 ItemId 拒绝；
- 首次 DA 失败保留客户端所需的生命周期逻辑（纯逻辑/偽物验证）；
- 临时资源释放通过代码路径审查，真实 COM 留给端到端验证。

## 文档与版本

同步更新 `README.md` 当前版本状态，说明上述正确性修复和测试命令；版本号保持 `1.9.0`。
