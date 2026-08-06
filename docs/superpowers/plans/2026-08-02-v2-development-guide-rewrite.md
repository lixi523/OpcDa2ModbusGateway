# V2.0.0 开发指南结构化改写计划

## 目标

以当前 V2.0.0 源码和 29 项测试为事实源，重建 `OPC_DA转ModbusTCP网关开发指南.md` 主体，保留项目 hard constraints 和完整版本历史，清除正文中混杂的 V1.x 旧实现描述。

## 章节

1. 文档范围与 V2.0.0 版本边界
2. 运行环境、四项目组成和部署产物
3. 总体架构、组件依赖和三阶段启动
4. OPC DA 采集、TagKey 分发和 Quality 语义
5. Modbus 类型、wire encoding、寄存器宽度和地址空间
6. 自动地址分配、映射校验和 CSV hard constraints
7. `ModbusWriteResult` 和 DA/MB 分离快照
8. `config.json` / `tags.json`、原子迁移和热重载
9. UI、健康快照、日志和生命周期
10. Watchdog IPC、心跳、优雅退出和重启策略
11. 授权、构建、测试、部署和现场验收
12. 已知限制与安全边界
13. 完整版本历史

## 事实源

- `Models/TagConfig.cs`
- `Models/DataTypeConverter.cs`
- `Models/OpcQualityHelper.cs`
- `Models/ModbusWriteResult.cs`
- `Models/SnapshotData.cs`
- `OpcDaClient.cs`
- `DataBridge.cs`
- `GatewayModbusTcpServer.cs`
- `Services/ConfigManager.cs`
- `Services/GatewayManager.cs`
- `Services/WatchdogManager.cs`
- `Watchdog/Program.cs`
- `Watchdog/WatchdogRestartPolicy.cs`
- `Tests/*.cs`
- 三个 `.csproj` 和 solution

## 必须保留

- `net472 + x86`
- 默认 `0.0.0.0:502`
- Costura.Fody
- 固定 CSV 模板、GBK 编码和 7 列映射表头
- 当前地址显示约定
- `CanonicalDataType` 约束
- 主窗体禁止最大化
- 完整版本历史

## 必须纠偏

- 解决方案为四项目，不是三个。
- `TagKey` 持久化于 `tags.json`。
- `String/DateTime` 无 Modbus wire encoding，启动拒绝。
- Quality helper 识别三态，但桥接接口当前只传播 Good/非 Good。
- 自动地址分配已实现。
- `UpdateValue` 返回 `ModbusWriteResult`。
- 快照为 DA/MB 分离字段。
- `tags.json` 当前不参与 watcher 热重载。
- DA 首次连接失败时 Modbus 可降级运行并重连。

## 验证

- 逐章与事实源交叉检查；
- 搜索旧签名、旧字段和错误未来项；
- 检查 Markdown 标题、代码块和表格；
- 保证版本历史完整；
- 执行 `git diff --check`；
- 运行现有 29 项测试和 Release 构建，确认文档修改未影响项目。
