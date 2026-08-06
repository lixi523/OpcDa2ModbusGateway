# V2.0.0 版本升级实施计划

## 目标

将当前 OPC DA → Modbus TCP 网关的当前发布版本从 V1.9.0 精准升级到 V2.0.0，发布日期为 2026-08-02；保持历史版本记录和历史设计约束不变。

## 修改范围

1. `OpcDaToModbusGateway.csproj`、`Watchdog/OpcDaToModbusGateway.Watchdog.csproj`、`Keygen/OpcDaToModbusGateway.Keygen.csproj`
   - `Version` → `2.0.0`
   - `AssemblyVersion` / `FileVersion` → `2.0.0.0`
   - 修正 Watchdog/Keygen Product/Description 中 OPC UA 残留。
2. `AppConstants.cs`
   - `AppVersion` → `2.0.0`。
3. `Keygen/Program.cs`
   - banner → `v2.0.0`。
4. `README.md`
   - 顶部当前版本 → V2.0.0；
   - 在版本历史首行新增 V2.0.0（2026-08-02）摘要；
   - 保留 V1.9.0 及更早历史行。
5. `OPC_DA转ModbusTCP网关开发指南.md`
   - 顶部当前版本和当前版本 XML 示例 → 2.0.0；
   - 版本历史新增 2.0.0 行；
   - 不改写旧 1.9.0/1.5.0 历史。
6. `handoff.md`
   - 当前版本、关键文件说明和新窗口提示词更新为 V2.0.0；
   - 已完成工作中原本描述 1.9.0 历史动作的内容保留语义。

## V2.0.0 摘要

- Modbus 声明类型、寄存器宽度和高 word 编码统一；
- 自动地址分配和映射验证；
- 配置迁移原子保存、热重载引用稳定；
- OPC Quality 正确传播；
- 转换与 Modbus 写入显式失败；
- DA/Modbus 快照分离；
- Watchdog 优雅退出与重新武装修复；
- net472/x86 MSTest 增至 29 项。

## 验证

```bash
dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release -p:PlatformTarget=x86
dotnet build --configuration Release
git diff --check
```

并搜索所有当前版本声明，确认：

- 当前版本均为 `2.0.0` / `2.0.0.0`；
- 历史记录中的 `1.9.0`、`1.5.0` 未被全局替换；
- 三个项目和 UI/Keygen banner 一致。
