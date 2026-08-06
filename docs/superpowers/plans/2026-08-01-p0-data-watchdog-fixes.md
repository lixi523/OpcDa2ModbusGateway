# P0 Data and Watchdog Fixes Implementation Plan

> **For agentic workers:** Execute tasks in order with TDD. Do not commit because the workspace contains user-owned pending changes.

**Goal:** 修复标签迁移、OPC Quality、转换/写入状态和看门狗优雅退出四项 P0 正确性问题。

**Architecture:** 配置使用独立快照原子保存；桥接链路显式返回转换和 Modbus 写入结果，并分别维护 DA/MB 状态；看门狗用可测试的持久状态机跨监控周期记录优雅退出。

**Tech Stack:** C# 8, .NET Framework 4.7.2, WinForms, TitaniumAS OPC DA, NModbus, MSTest, x86

---

### Task 1: 配置迁移原子保存

**Files:**
- Modify: `Services/ConfigManager.cs`
- Create: `Tests/ConfigMigrationTests.cs`

- [ ] 写临时目录测试：旧 `config.json` 内含无 TagKey 的 Tags，且无 `tags.json`。
- [ ] 首次 Load 后断言 `tags.json` 已生成，再构造新 manager 执行第二次 Load，标签和 TagKey 完整。
- [ ] 增加不修改活动 `Config.OpcDa.Tags` 的配置快照序列化。
- [ ] 实现 `SaveAllImmediate()`：先原子写 tags，再原子写 config；迁移路径调用该方法。
- [ ] 模拟 tags 写入失败，断言旧 config 仍保留内联标签。

### Task 2: OPC Quality 正确传播

**Files:**
- Modify: `OpcDaClient.cs`
- Create: `Models/OpcQualityHelper.cs`
- Create: `Tests/OpcQualityTests.cs`

- [ ] 写 `0xC0=Good`、`0x40=Uncertain`、其他=Bad 的纯逻辑测试。
- [ ] `OnValuesChanged` 与 `DoSyncRead` 统一使用 helper；Error 不再替代 Quality。
- [ ] Bad/Uncertain 有值时仍触发数据事件，`isGood=false`；不可读/无值不覆盖 Modbus。
- [ ] 保持 `IOpcDaClient` 现有事件签名，避免扩大接口。

### Task 3: 转换与 Modbus 写入显式结果

**Files:**
- Modify: `Models/DataTypeConverter.cs`
- Create: `Models/ModbusWriteResult.cs`
- Modify: `Services/Interfaces/IGatewayModbusTcpServer.cs`
- Modify: `GatewayModbusTcpServer.cs`
- Modify: `DataBridge.cs`
- Modify: `Tests/ModbusCorrectnessTests.cs`

- [ ] 写非法字符串、溢出转换必须抛错的测试。
- [ ] 删除 `ConvertValue` 的默认值 catch。
- [ ] `UpdateValue` 返回 `ModbusWriteResult`，不得空 catch；未运行、未映射和写失败返回具体状态。
- [ ] Fake server 可注入成功/失败结果。
- [ ] 只有成功写入才增加 `TotalUpdates`；转换/写失败增加 `ErrorCount`。

### Task 4: DA/Modbus 快照分离

**Files:**
- Modify: `Models/SnapshotData.cs`
- Modify: `DataBridge.cs`
- Modify: `MainForm.cs`
- Modify: `Tests/ModbusCorrectnessTests.cs`

- [ ] 写 Good 成功、BadQuality、ConversionError、WriteError 四种快照测试。
- [ ] `TagSnapshot` 增加 DA 值/质量/时间和 MB 值/状态/最后成功时间字段。
- [ ] Bad/Uncertain 仅更新 DA，MB 保留最后成功值。
- [ ] 转换或写入失败保留 MB 值并设置对应状态。
- [ ] 主表格现有列改读分离字段，不新增列。

### Task 5: 看门狗优雅退出状态机

**Files:**
- Create: `Watchdog/WatchdogRestartPolicy.cs`
- Modify: `Watchdog/Program.cs`
- Modify: `Watchdog/OpcDaToModbusGateway.Watchdog.csproj`
- Create: `Tests/WatchdogRestartPolicyTests.cs`
- Modify: `Tests/OpcDaToModbusGateway.Tests.csproj`

- [ ] 写多个缺席周期测试：消费 graceful 后持续不重启。
- [ ] 写主进程再次出现后重新武装，再次缺席时允许重启。
- [ ] 将纯策略类共享编译到 Watchdog 与 Tests，监控循环调用策略。
- [ ] 仅在重新武装时 Reset GracefulExitEvent。

### Task 6: 文档和完整验证

**Files:**
- Modify: `README.md`

- [ ] 更新 V1.9.0 当前正确性状态和测试数量，不提升版本。
- [ ] 执行：

```bash
dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release -p:PlatformTarget=x86
dotnet build --configuration Release
git diff --check
```

- [ ] 对最新 pending diff 执行独立 review，修复全部 Blocking/High findings 后重跑验证。

预期：全部测试通过，Release 0 warning/0 error，diff check 通过。
