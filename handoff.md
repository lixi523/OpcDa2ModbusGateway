# Handoff：OPC DA → Modbus TCP 网关

> 生成时间：2026-09-15
> 版本：V2.2.0
> 适用：新窗口继续任务
> 代码状态：HEAD=`ebc69e5` 之后（V2.2.0 开发中）；工作区含 `.archify/`（已提交 docs/archify）与本地 Keygen/（已 gitignore）

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
| OPC Quality 传播 | ✔ 完成（`OnDataChanged` 三态 `OpcQualityKind`，Uncertain/Bad 区分显示）|
| 主窗口监控表格 | ✔ 完成（DA/MB 分列显示，含实时缓存）|
| CSV 导出/导入（标签/映射分离）| ✔ 完成（GBK 编码，WPS 兼容，TagKey 身份，全逗号空行兼容）|
| 配置持久化与迁移 | ✔ 完成（config.json + tags.json 原子保存，热重载加固）|
| 看门狗进程 | ✔ 完成（优雅退出状态机 + 重新武装）|
| 构建与自动化测试 | ✔ Release 0 错误 0 警告；MSTest 33/33 通过；Windows CI 已恢复 |
| 文档 | ✔ README / 开发指南 / handoff 已更新至 V2.2.0 |
| 版本发布 | ✔ V2.2.0 提交中（含 P0 验证 + P1 治理） |
| **运行时验证（连接真实 DA 服务 + Modbus 客户端读取）** | ✔ **已完成（P0，2026-09-15）** |

---

## 3. 已完成修改

### 3.1 V2.0.0 升级与 Modbus 类型/宽度/字节序统一
- 三个生产项目（主程序、Watchdog、Keygen）`Version=2.0.0`；`AppConstants.AppVersion`、Keygen banner 同步
- `ModbusDataType` 进入运行时链路，按声明类型编码（不再按 CLR 类型猜测）；支持 Bool/Byte/SByte/Int16/UInt16/Int32/UInt32/Float/Double
- 16-bit 1 寄存器、32-bit 2、64-bit 4；多 word **高 word 在前**；`String`/`DateTime` 无 wire encoding，启动映射校验明确拒绝
- Modbus TCP 启动失败路径清理 listener/network/CTS/datastore，避免端口残留
- 自动地址按四个地址空间（Coil/DiscreteInput/HoldingRegister/InputRegister）分别从 0 顺序推进，按声明类型宽度递增；启动前与 CanonicalDataType 回写后各执行一次 `ValidateMappings`（溢出/重叠检查）

### 3.2 数据正确性与 TagKey（P0 修复批次）
- **配置迁移不丢标签**：`SaveAllImmediate()` 先原子写 tags.json 再写 config 快照；热重载保持 `AppConfig` 根引用稳定
- **显式失败**：`DataTypeConverter.ConvertValue` 不再吞异常写零；`IGatewayModbusTcpServer.UpdateValue` 返回 `ModbusWriteResult`
- **DA/MB 快照分离**：`TagSnapshot` 分离 DA（DaValue/DaQuality/DaTimestamp）与 MB（ModbusValue/ModbusStatus/ModbusLastSuccess）
- `TagKey` 持久化于 `tags.json`；CSV 第一列用 `<序号>|<Base64Url(TagKey)>` 稳定身份，旧纯数字格式仅在无重复 ItemId 时兼容

### 3.3 看门狗修复
- 优雅退出后进入持久 `WaitingForManualStart` 状态，主程序缺席期间持续抑制重启；主程序重现后重新武装崩溃守卫
- 独立 `WatchdogRestartPolicy` 纯逻辑类，便于单元测试

### 3.4 V2.0.0 变更集审查修复（原 8 项 + 子代理复核 6 项）
- **Variant 中止启动**：`TryGetModbusDataType`/`TryGetEffectiveAddressWidth`/`ValidateMappings` 保守校验；`DataBridge` 拆 `RegisterModbusNodes()` + `OnClientConfigChanged()` 动态补注册
- **Stop 后可重启**：`OpcDaClient.Stop()` 不再置 `_disposedInt`，`Dispose()`（持锁）置 1，`Start` 首行加 `ObjectDisposedException` 守卫
- **Quality 三态**：`OnDataChanged` 签名 `bool isGood` → `OpcQualityKind`（`IOpcDaClient`/`OpcDaClient`/`FakeOpcDaClient` 同步）；`DataBridge` 非 Good 时 `daQuality = quality==Good ? "Bad" : quality.ToString()`、`ModbusStatus="BadQuality"`，Uncertain 时 MB 值保持不变
- **csproj 乱码**：`OpcDaToModbusGateway.csproj` GBK→UTF-8+BOM（0 FFFD）
- **死代码**：删 `MainForm.ParseModbusAddress`、`OpcQualityHelper.IsGood`（被 Quality 改造孤儿化）、`ItemSelectionDialog.InferRegisterType`/`FormatModbusAddress`（私有无调用者）
- **Watchdog 重复日志**：`Program.cs` 加 `waitingForManualStartLogged` 一次性日志，`Rearm`/`Restart` 复位
- **CSV 导入健壮性**：映射/点表导入统一跳过全逗号分隔空行（`line.Trim(',')` 判空），修复表头误判导致的导入错位与幽灵记录（映射导入 `",,,,,,"` 被当表头抛"序号/TagKey 格式无效"；点表导入 `",,,,"` 使真实表头变幽灵数据行 `ItemId="ItemId"`）
- **已核实驳回的误报**：#2 子代理称存在旧 9 列 Modbus 映射格式需列平移——`git show 472c1b3` 证实 V1.9.0 导出为 UA 格式与 5 列点表，无 9 列 Modbus 格式；#6 `BtnOK_Click` 地址溢出已有显式守卫且调用方有 catch
- **CI 恢复**：新建 `.github/workflows/build.yml`（restore → build → test → 打包 → `softprops/action-gh-release@v2`）

### 3.5 版本升级至 V2.1.0（2026-08-06）
- 三个生产项目 `Version=2.1.0`、`AssemblyVersion/FileVersion=2.1.0.0`；`AppConstants.AppVersion="2.1.0"`；Keygen banner `v2.1.0`
- README/开发指南/handoff 顶部当前版本与版本历史表首行已更新（2.0.0 及更早历史行保留）；开发指南版本边界切换至 V2.1.0
- config.json 完成 OpcUa→ModbusTcp 迁移并净化（去除运行时测试污染：Knight demo、a.a.g/f/e 测试标签）
- 已提交 `e64dbef`：51 文件，+3512/−2606

### 3.6 P0 端到端验证完成（2026-09-15）
- 连接真实 `Knight.OPC.Server.Demo`（TitaniumAS 驱动），42 个标签全部映射成功
- 原生 Modbus TCP 客户端逐一读取全部 42 标签地址，Double/Int32/UInt32/UInt16/Int16/Boolean 值解码正确，高 word 在前字节序符合规范
- Double 标签值实时变化（66.58 → 67.70），DA 数据源活跃确认
- Quality 传播、断线重连、看门狗现场验证因试用授权在 16:42 到期自动关闭 DA 连接而未完成（P2 后续）

### 3.7 V2.2.0 P1 发布治理与安全边界（2026-09-15）
- **P1-1 发布包隔离**：Keygen 源码移出仓库（`git rm`，本地保留，加 `.gitignore` `/Keygen/`）；主项目默认构建/发布不含 Keygen；CI 客户发布包白名单；sln/csproj 移除 Keygen 项目引用与构建 Target
- **P1-2 Modbus 网络边界**：默认监听地址 `0.0.0.0` → `127.0.0.1`（仅本机回环）；`GatewayModbusTcpServer`/`ConfigManager`/`TagConfig.GetEffectiveListenAddress`/`GetEndpointUrl`/`MainForm` UI 五处默认值同步；外部访问需显式配置 `0.0.0.0` 或指定内网 IP
- 提交 `docs/archify/` 架构图（html/json/png）
- 版本升级至 2.2.0：主程序 + Watchdog csproj（Version/AssemblyVersion/FileVersion）+ AppConstants.AppVersion 共 4 处（Keygen 本地维护，banner 手动对齐）

---

## 4. 关键文件

| 文件 | 说明 |
|------|------|
| `OpcDaToModbusGateway.csproj` | 主项目，net472/x86，Version 2.2.0；Costura.Fody 嵌入依赖 |
| `AppConstants.cs` | 全局常量，AppVersion=2.2.0 |
| `Models/DataTypeConverter.cs` | DA 类型解析、Modbus 类型规范化、宽度/高 word 编码 |
| `Models/OpcQualityHelper.cs` | OPC Quality 高两位三态分类（仅 `Classify`）|
| `Models/ModbusWriteResult.cs` | Modbus 写入结果枚举与错误消息 |
| `Models/SnapshotData.cs` | TagSnapshot：DA/MB 分离字段 |
| `Models/TagConfig.cs` | 标签配置、TagKey 持久化、有效宽度、`OpcDaConfig`/`ModbusTcpConfig`/`AppConfig` |
| `Models/LicenseAlgorithm.cs` | PCID + HMAC-SHA256 授权码（三层 XOR 混淆）|
| `OpcDaClient.cs` | OPC DA 客户端：订阅/轮询、CanonicalDataType 回写、Quality 三态传播 |
| `DataBridge.cs` | DA → Modbus 桥接：显式写入结果、分离快照、Start 幂等、动态补注册 |
| `GatewayModbusTcpServer.cs` | NModbus TCP 从站：声明类型编码、写结果、锁保护 |
| `Services/GatewayManager.cs` | 生命周期管理：三阶段启动、降级运行、CheckHealth 重连、映射校验 |
| `Services/ConfigManager.cs` | config.json/tags.json 原子保存、迁移、热重载 watcher |
| `Services/WatchdogManager.cs` | 看门狗子进程管理、心跳、SignalGracefulExit |
| `Services/MappingCsvIdentity.cs` | CSV 序号与 TagKey 编码/解析、旧格式兼容校验 |
| `Services/Interfaces/IOpcDaClient.cs` 等 | 三个接口 + `FakeOpcDaClient`（测试桩）|
| `Watchdog/Program.cs` | 看门狗进程主循环、重启策略执行 |
| `Watchdog/WatchdogRestartPolicy.cs` | 优雅退出状态机（可测试纯逻辑）|
| `Tests/` | MSTest 测试项目（33 项：ModbusCorrectness 24 / OpcQuality 5 / Config 2 / Watchdog 2）|
| `config.json` | 网关配置（不含 Tags，Tags 存 tags.json）|
| `tags.json` | 标签配置（运行时生成/迁移）|
| `README.md` | 项目说明与版本历史 |
| `OPC_DA转ModbusTCP网关开发指南.md` | V2.2.0 结构化开发指南（13 章）|
| `.github/workflows/build.yml` | Windows CI（restore→build→test→打包发布）|
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
5. **Modbus TCP 默认监听**：`127.0.0.1:502`（V2.2.0 起，仅本机回环；需外部访问时显式配置 `0.0.0.0` 或指定内网 IP）
6. **目标框架**：`net472` + `x86`（OPC DA COM 组件通常为 32 位）
7. **DLL 嵌入**：使用 Costura.Fody 实现单 EXE 部署
8. **窗口样式**：`FormBorderStyle = FormBorderStyle.FixedSingle` + `MaximizeBox = false`（主窗体禁止最大化）
9. **支持的 Modbus wire type**：Bool/Byte/SByte/Int16/UInt16/Int32/UInt32/Float/Double；**String/DateTime 没有 wire encoding，启动时拒绝**
10. **多字节编码**：高 word 在前；不支持配置 byte/word swap
11. **Quality 传播**：`IOpcDaClient.OnDataChanged` 签名 `(string, object, OpcQualityKind, DateTime)` 三态；`OpcQualityHelper.Classify` 按高两位分类；`DataBridge` 区分 Good / Uncertain / Bad（非 Good 时 `daQuality` 显示质量名、`ModbusStatus="BadQuality"`，Uncertain 时 MB 值保持不变）
12. **版本号修改点**：主程序 + Watchdog 两个 csproj（`Version`/`AssemblyVersion`/`FileVersion`）+ `AppConstants.AppVersion` 共 4 处，必须同步。Keygen 源码不随仓库分发（本地维护），其 `Keygen/Program.cs` banner 需手动对齐，勿全局字符串替换

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
- ~~提交 config.json 运行时版本~~ → config.json 会被程序改写（测试标签、AutoStart 等），提交前必须净化到迁移后默认值
- ~~提交 `.reasonix/` 目录~~ → 本地工具元数据，不入库（用 `git add -A -- . ':!.reasonix'` 排除）

---

## 7. 当前风险

### 🔴 P0 项：运行时验证（已完成 2026-09-15）
- 构建与单测通过；已连接真实 `Knight.OPC.Server.Demo` + Modbus TCP 客户端完成 42 标签全量读验证，各类型数据转发正确、Double 实时变化。
- **遗留子项**：Good/Uncertain/Bad Quality 传播、DA 断线自动重连、看门狗拉起因试用授权在验证期间到期自动关闭 DA 而未完成，移入 P2。

### 🟡 P1 项：发布治理与安全边界
- **发布包隔离**：✔ 已解决（V2.2.0）。Keygen 源码不随仓库分发（本地维护，`.gitignore` 排除），主程序默认构建/CI 客户发布包均不含 Keygen。
- **Modbus 网络边界**：✔ 已解决（V2.2.0）。默认监听改为 `127.0.0.1:502`，仅本机可访问；生产部署需外部 SCADA 访问时显式配置 `0.0.0.0` 或指定内网 IP，并配合防火墙/ACL 限定访问范围。
- **授权算法**：HMAC 共享密钥方案，深度逆向可被破解（源码已注明），长期应迁移 ECDSA 非对称签名（未启动）。

### 🟡 P2 项：已知限制
- `tags.json` 外部修改不会触发当前 watcher 热重载（仅监听 config.json）。
- 无 Modbus 写回 OPC DA 支持（当前为 DA → Modbus 只读方向）。
- README 第 87/110 行仍引用已删除的 `使用文档.md`（V2.0.0 文档整合时删除，README 引用未同步）——后续清理文档时需修正。
- `csharp-ls` 对 net472/WinForms 项目存在基础引用级联误报；实际 MSBuild/MSTest 通过，调试诊断需以构建为准。

---

## 8. 已经跑过的测试

| 测试项 | 结果 | 备注 |
|--------|------|------|
| Release 构建（V2.2.0）| ✔ 通过 | 0 错误 0 警告 |
| MSTest（net472/x86）| ✔ 33/33 通过 | ModbusCorrectness 24 / OpcQuality 5 / Config 2 / Watchdog 2 |
| exe 程序集版本 | ✔ 2.2.0.0 | 主程序 + Watchdog 两个 exe FileVersion 确认 |
| CSV 格式验证 | ✔ 通过 | GBK 编码，WPS 打开列分隔正常 |
| 导出点表格式 | ✔ 通过 | 首行固定格式 + 表头，Single→float |
| 导出映射格式 | ✔ 通过 | 7 列表格 + TagKey 身份，无 sep=, |
| 导入映射 | ✔ 通过 | 新格式按 TagKey；旧格式仅无重复时兼容；全逗号分隔行已修复 |
| 配置迁移 | ✔ 通过 | 旧内容 Tags 迁移不丢，写失败回滚 |
| Modbus 端口监听 | ✔ 通过 | netstat 确认 502 端口监听（历史验证） |
| 端到端运行时验证 | ✔ 已完成（2026-09-15） | 42 标签全量读通过；Quality/断线/看门狗子项因授权到期移入 P2 |

---

## 9. 下一步计划

优先级从高到低：

1. **P0 遗留子项**：完成授权激活后，验证 Good/Uncertain/Bad Quality 传播、DA 断线自动重连、看门狗拉起（2026-09-15 验证时试用授权中途到期导致 DA 自动断开）
2. **P1 已解决项回顾**：发布包隔离、Modbus 网络边界已在 V2.2.0 完成；授权 ECDSA 迁移仍为长期项
3. **P2 项：可选优化**
   - 看门狗进程保活与优雅退出现场验证
   - 日志清理与轮转验证
   - 大数据量监控表格虚拟模式性能
   - `tags.json` 热重载支持（需扩展 watcher 范围并处理与运行中映射的一致性）
   - 清理 README 对已删除 `使用文档.md` 的引用

---

## 10. 新窗口启动提示词

```
继续开发 OPC DA → Modbus TCP 网关项目。工作目录：D:\Documents\Code\OpcDa2Modbus
版本：V2.2.0

请先读取 handoff.md 确认上下文。
当前状态：
- Release 构建通过，0 错误 0 警告；MSTest 33/33 通过。
- P0 端到端验证已完成（42 标签真实 DA + Modbus 客户端全量读通过）。
- P1-1 发布包隔离已完成（Keygen 源码移出仓库，本地保留 + .gitignore）。
- P1-2 Modbus 网络边界已完成（默认监听 127.0.0.1）。
- 工作区：.archify/ 已提交 docs/archify；本地 Keygen/ 已 gitignore。
下一步任务：
1. P0 遗留：授权激活后验证 Quality 传播、DA 断线重连、看门狗拉起
2. P2：看门狗现场验证、日志轮转、tags.json 热重载、README 文档引用清理
关键经验：
- 版本升级改 4 处：主程序 + Watchdog csproj（Version/AssemblyVersion/FileVersion）+ AppConstants.AppVersion；Keygen 本地 banner 手动对齐，勿全局字符串替换
- 含中文文件一律用 Python（显式 encoding）或纯 ASCII 命令编辑，防 GBK 往返损坏
- config.json 提交前净化到迁移后默认值（去掉运行时测试污染）
编译命令：dotnet build OpcDaToModbusGateway.sln -c Release
测试命令：dotnet test Tests/OpcDaToModbusGateway.Tests.csproj -c Release
输出目录：bin\Release\net472\OpcDaToModbusGateway.exe
```
