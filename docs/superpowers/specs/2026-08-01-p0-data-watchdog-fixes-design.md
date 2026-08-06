# P0 数据与退出正确性修复设计

## 目标

在保持 `net472`、`x86`、版本 `1.9.0` 和既有 CSV 格式不变的前提下，修复：

1. 旧内联标签迁移后丢失；
2. OPC Quality 判定与传播错误；
3. 类型转换或 Modbus 写入失败被伪装成成功；
4. 看门狗在优雅退出后再次拉起主程序。

## 配置迁移

为 `ConfigManager` 增加统一原子保存入口。标签迁移时先把完整标签写入 `tags.json.tmp` 并原子替换 `tags.json`；成功后再从独立 DTO/快照生成不含 Tags 的 `config.json.tmp` 并替换。不得通过临时修改共享 `Config.OpcDa.Tags` 生成配置文件。任一步失败时保留旧 `config.json`，并返回失败结果供日志/UI 处理。

## OPC Quality

集中实现 Quality 判定：高两位 `0xC0` 为 Good，`0x40` 为 Uncertain，其余为 Bad。`Error` 只表示读取操作状态，不替代 Quality。Good、Uncertain、Bad 样本均向桥接层传播；无法读取或无值时传播 Bad 状态且不覆盖 Modbus。

当前 `IOpcDaClient.OnDataChanged` 的 bool 参数继续表示“是否 Good”，避免本次扩大公共接口；快照质量文本由桥接层保存。若 TitaniumAS 可区分 Uncertain，则内部质量文本保留 `Uncertain`；否则至少按非 Good 处理。

## 转换与 Modbus 写入

`DataTypeConverter.ConvertValue` 不再捕获异常并返回默认值。转换失败向上抛出明确异常。

`IGatewayModbusTcpServer.UpdateValue` 返回 `ModbusWriteResult`，区分：成功、BadQuality 跳过、未运行/未映射、编码失败和 datastore 写失败。服务器不得空 catch。只有真正写入 datastore 后才更新 mapping 的 LastValue/LastTimestamp。

`DataBridge` 依据结果维护：

- `TotalUpdates`：仅成功 Modbus 写入；
- `ErrorCount`：转换或写入失败；
- Bad/Uncertain 不作为程序错误；
- 限流日志留作后续，本次保持每次错误一条。

## DA 与 Modbus 快照分离

扩展 `TagSnapshot`：

- DA：最新原始/转换值、Quality、Timestamp；
- Modbus：最后成功写入值、Status、LastSuccessTimestamp。

语义：

- 成功：两侧更新，MB Status=Good；
- Bad/Uncertain：只更新 DA，MB 值不变，Status=BadQuality；
- 转换失败：DA 保存原值，MB 不变，Status=ConversionError；
- 写入失败：DA 保存转换值，MB 不变，Status=WriteError。

主表格现有 DA/MB 列分别读取对应字段，不改变列数量。

## 看门狗优雅退出

看门狗维护 `waitingForManualStart`：首次消费 GracefulExitEvent 后设为 true，不 Reset 后立即恢复崩溃策略。在主进程持续缺席期间禁止重启；一旦观察到目标主进程重新出现，清除状态并 Reset GracefulExitEvent，恢复后续崩溃守护。

将状态判定提取为不依赖 Process 的纯逻辑类/方法，以便单元测试覆盖多个监控周期。

## 测试

新增覆盖：

- 只有旧 `config.json` 内联 Tags、无 `tags.json` 的两次加载往返；
- Good/Uncertain/Bad Quality 位判定；
- 非法字符串、溢出不写零；
- Modbus 编码/写入失败不增加成功数，MB 快照保留最后成功值；
- Bad Quality 更新 DA 但不覆盖 MB；
- GracefulExit 后多个周期不重启，手工出现后重新武装；
- 现有 18 个测试继续通过。

## 文档

更新 `README.md` V1.9.0 当前正确性状态和测试数量；不提升版本号。
