# Modbus Correctness Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use focused TDD and execute each task in order. Do not commit because the workspace contains unrelated user-owned pending changes.

**Goal:** 修复 OPC DA 恢复、Modbus 类型编码与地址分配、CSV 重复身份及临时 COM Group 释放问题，并用 net472/x86 测试固定行为。

**Architecture:** 将 Modbus wire type 的规范化、宽度和编码集中到 `DataTypeConverter`；`DataBridge` 将有效类型传入 server mapping。保留首次失败的 DA client 供现有 bridge/health check 重连。CSV 在原 7 列中携带 TagKey，旧格式仅在无歧义时兼容。

**Tech Stack:** C# 8, .NET Framework 4.7.2, WinForms, NModbus 3.0.81, MSTest, x86

---

### Task 1: 建立测试项目与 Modbus 类型规则

**Files:**
- Create: `Tests/OpcDaToModbusGateway.Tests.csproj`
- Create: `Tests/ModbusTypeTests.cs`
- Modify: `OpcDaToModbusGateway.csproj`
- Modify: `OpcDaToModbusGateway.sln`
- Modify: `Models/DataTypeConverter.cs`

- [ ] 新增 net472/x86 MSTest 项目，并从主项目编译项排除 `Tests/**`。
- [ ] 先写类型规范化、宽度和 16/32/64 位编码测试。
- [ ] 运行测试确认缺少 API 时失败。
- [ ] 在 `DataTypeConverter` 增加受支持类型规范化、地址宽度和 register 编码 API；String/DateTime 抛出明确异常。
- [ ] 运行测试确认通过。

验证：

```bash
dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release -p:PlatformTarget=x86
```

预期：全部测试通过。

### Task 2: 打通声明 ModbusDataType 的运行时链路

**Files:**
- Create: `Tests/DataBridgeTests.cs`
- Modify: `Services/Interfaces/IGatewayModbusTcpServer.cs`
- Modify: `DataBridge.cs`
- Modify: `GatewayModbusTcpServer.cs`

- [ ] 写 Fake server 测试，断言 `DataBridge.Start()` 将 `GetEffectiveModbusDataType()` 传给注册调用。
- [ ] 修改 `AddVariableNode` 接口与 mapping，保存声明类型。
- [ ] 写入寄存器时调用统一编码 API，移除按 CLR 类型猜测的旧编码器。
- [ ] 让 `DataBridge.Start()` 幂等，并拒绝 null client/server/tags。
- [ ] 修复 `FakeOpcDaClient` Dispose 状态检查。
- [ ] 运行测试。

### Task 3: 修复地址分配和启动验证

**Files:**
- Create: `Tests/AddressAllocationTests.cs`
- Modify: `Models/TagConfig.cs`
- Modify: `ItemSelectionDialog.cs`
- Modify: `Services/GatewayManager.cs`

- [ ] 在 `TagConfig` 增加公开的有效地址宽度方法，寄存器类型使用 Modbus type 宽度，bit 类型返回 1。
- [ ] 写连续分配与不支持类型测试。
- [ ] 自动地址按四个地址空间分别推进，并检查 `ushort` 溢出。
- [ ] 网关启动前验证所有映射类型受支持且同地址空间区间不重叠，错误消息列出标签。
- [ ] 运行测试。

### Task 4: 修复首次 DA 失败后的恢复生命周期

**Files:**
- Create: `Tests/GatewayLifecycleTests.cs`
- Modify: `OpcDaClient.cs`
- Modify: `Services/GatewayManager.cs`

- [ ] 保存最近 acquisition mode，`TryReconnect` 沿用该模式。
- [ ] 首次 Start 失败时保留 client，不 Dispose/置 null，bridge 订阅同一实例。
- [ ] `CheckHealth` 增加重入保护；退避到期后才增加真实尝试次数。
- [ ] 用可提取纯逻辑或 Fake 覆盖退避计数和 client 保留行为；真实 COM 恢复留给 P0。
- [ ] 运行测试。

### Task 5: 修复 CSV 重复 ItemId 身份

**Files:**
- Create: `Services/MappingCsvIdentity.cs`
- Create: `Tests/MappingCsvIdentityTests.cs`
- Modify: `MainForm.cs`

- [ ] 写 Base64Url TagKey 第一列编码/解析测试。
- [ ] 写旧纯数字序号解析测试。
- [ ] 导出第一列改为 `<序号>|<Base64Url(TagKey)>`，保持 7 列表头和 GBK。
- [ ] 新格式按 TagKey 导入并校验 ItemId；旧格式只有 ItemId 在 CSV 与当前配置都唯一时允许。
- [ ] 重复键直接报错，禁止覆盖。
- [ ] 运行测试。

### Task 6: 修复临时 Group 释放并更新文档

**Files:**
- Modify: `OpcDaClient.cs`
- Modify: `README.md`
- Modify: `Models/TagConfig.cs`

- [ ] 将临时 Group 的 `RemoveGroup` 与 `Dispose` 移入 `finally`。
- [ ] 删除 `Models/TagConfig.cs` 本次触及位置的 trailing whitespace。
- [ ] 在 README V1.9.0 状态中记录正确性修复、支持类型限制和测试命令，不提升版本号。
- [ ] 执行完整 Release 构建、测试、LSP diagnostics、`git diff --check` 和最终 review。

最终验证：

```bash
dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release -p:PlatformTarget=x86
dotnet build --configuration Release
git diff --check
```

预期：测试通过；构建 0 warning/0 error；本次文件无新增 whitespace error。
