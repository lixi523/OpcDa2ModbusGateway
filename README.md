# OPC DA → OPC UA 网关（OpcDaToUaGateway）

![Build](https://github.com/lixi523/OpcDaToUaGateway/actions/workflows/build.yml/badge.svg)

> 版本：**V1.9.0** ｜ 协议转换网关：将 OPC DA 数据源实时映射为 OPC UA 服务器，供上位 SCADA/MES/工业平台订阅。

---

## 1. 项目简介

OpcDaToUaGateway 是一款运行于 Windows 的轻量级工业协议网关，解决"存量 OPC DA 设备无法直接接入现代 OPC UA 体系"的痛点：

- **输入端**：通过 OPC DA 自动发现并订阅现场标签（支持 Matrikon、Kepware、力控 pSpace OPCServer 等 DA 服务器）。
- **输出端**：对外暴露标准 **OPC UA** 服务器（默认端口 `4840`），以 `folder/tag` 层级结构发布实时值、质量戳与时间戳。
- **目标场景**：7×24 小时持续运行的数据采集前置机、协议转换桥接、老旧 SCADA 系统上云/接入 UA 客户端的过渡方案。

---

## 2. 核心特性

| 能力 | 说明 |
|---|---|
| OPC DA 扫描与订阅 | 自动枚举 DA 服务器/分支/标签，支持手动添加与点表批量导入导出（CSV） |
| 数据类型转换 | 内置 `DataTypeConverter`，DA → UA 类型安全映射（见 `Models/DataTypeConverter.cs`） |
| OPC UA 服务 | 证书自动生成与续期（`GatewayOpcUaServer.cs`），支持 Browse/Read/Subscribe |
| 授权管理 | `LicenseManager` 授权校验，配套 `Keygen` 工具计算授权码 |
| 看门狗守护 | `Watchdog` 子进程心跳监护，异常退出自动拉起（60s/3 次重启保护） |
| 健康快照 | `HealthSnapshot` 采集内存/连接状态，超标（20%/50%）告警 |
| 托盘运行 | 最小化为系统托盘，支持开机自启 |

---

## 3. 系统要求

- **操作系统**：Windows 10 / 11 / Windows Server（需 .NET Framework 4.7.2 运行库）。
- **构建环境**（开发/CI）：Windows + Visual Studio（勾选「.NET 桌面开发」工作负载）或 .NET SDK + .NET Framework 4.7.2 目标包。
- **依赖**：OPC DA 客户端需目标 DA 服务器可访问；OPC UA 客户端需放行 `4840` 端口（或自定义端口）。

---

## 4. 目录结构

```
OpcDa2Ua/
├── OpcDaToUaGateway.sln          # 解决方案（主程序 + Keygen + Watchdog）
├── OpcDaToUaGateway.csproj       # 主程序工程（SDK 风格 net472 WinForms）
├── Program.cs / MainForm.cs      # 入口与主控窗体
├── OpcDaClient.cs                # OPC DA 客户端封装
├── GatewayOpcUaServer.cs         # OPC UA 服务器（证书/节点管理）
├── DataBridge.cs                 # DA→UA 数据桥接
├── OpcServerScanner.cs           # DA 服务器/标签扫描
├── AppConstants.cs / Theme.cs    # 常量与主题
├── Models/                       # 标签配置、数据类型转换、快照模型
├── Services/                     # GatewayManager / LicenseManager / HealthSnapshot / LogManager / ConfigManager / WatchdogManager
│   └── Interfaces/               # IOpcDaClient / IGatewayOpcUaServer / IDataBridge / IHealthSnapshot / FakeOpcDaClient
├── Keygen/                       # 授权码计算工具（独立子工程）
├── Watchdog/                     # 看门狗守护进程（独立子工程）
├── config.json                   # 运行配置（演示配置，可改）
└── 文档/                         # 见第 7 节
```

---

## 5. 构建

### 本地构建
```bat
dotnet build OpcDaToUaGateway.sln -c Release -v minimal
```
构建产物：`bin/Release/net472/OpcDaToUaGateway.exe`、`Keygen/bin/Release/...`、`Watchdog/bin/Release/...`。

> 详细常见报错与处理（net472 引用缺失、离线还原、x86 COM）见 [`本地编译步骤.md`](本地编译步骤.md)。

### 云端构建（GitHub Actions）
仓库已内置工作流 [`.github/workflows/build.yml`](.github/workflows/build.yml)：推送至 `main`/`master` 后，在 `windows-latest` 运行器自动 `dotnet restore` + `dotnet build -c Release`，产物以 Artifact 形式可下载。

---

## 6. 快速开始

1. 运行 `bin/Release/net472/OpcDaToUaGateway.exe`。
2. 「服务器」→ 选择/扫描 OPC DA 服务器（或「手动添加」标签）。
3. 导入/编辑点表，确认 UA 节点层级。
4. 启动后 UA 客户端连接 `opc.tcp://<本机IP>:4840` 订阅数据。
5. 授权到期前在「关于」中填入 `Keygen` 生成的授权码。

完整操作、配置字段、授权与排障见 [`使用文档.md`](使用文档.md)。

---

## 6.1 版本历史

| 版本 | 日期 | 变更摘要 |
|---|---|---|
| **V1.9.0** | 2026-07-17 | **全面代码审查 + P0 修复**：修复 `HealthSnapshot.Capture()` Monitor.TryEnter/Exit 不配对导致 `SynchronizationLockException`（2处）；实施 P1 改进：`DataBridge.StartAsync` _uaServer 快照防竞态、`LicenseManager` _disposed 防 Tick 后执行、`LogManager.Dispose` 超时改 `_log.Append` 告警、`ConfigManager` 实现 `IDisposable`、`AppVersion` 同步。编译 0 警告 0 错误。 |
| V1.8.1 | 2026-07-17 | **启动卡顿最终修复**：3.5 万次 `AddVariableNode` 移至后台线程（`Task.Run`），UI 线程通过 `SynchronizationContext.Post` 安全输出进度日志。真实环境验证窗口保持响应。 |
| V1.8.0 | 2026-07-16 | 移除 V1.7.0 新增的「已连接客户端」列表功能；修复启动窗口「未响应」卡顿根因（逐节点 `Diag()` 导致 O(n²) 日志洪泛）。编译 0 警告 0 错误。 |
| V1.7.0 | 2026-07-15 | 定稿发布：修复 3 个运行时缺陷（`SafeBeginInvoke` 句柄防护、UTC 时间戳显示转换、DA 质量判定修正 `value.Quality.Status & 0xC0`）；UA 设置区 UI 调整。 |
| V1.6.0 | — | 新增 OPC DA 同步/异步获取模式。 |
| V1.5.1 | — | 修复 `RunningStateChanged` 跨线程异常、`SafeInvoke` 封送与缺句柄防护。 |

---

## 7.1 文档索引

| 文档 | 用途 | 读者 |
|---|---|---|
| [`使用文档.md`](使用文档.md) | 安装部署、配置、操作、授权、日志、FAQ | 现场实施/运维 |
| [`故障恢复预案.md`](故障恢复预案.md) | 9 类故障场景识别与恢复步骤 | 运维/值班 |
| [`OPC_DA转UA网关开发指南.md`](OPC_DA转UA网关开发指南.md) | 架构、模块、版本演进史 | 开发者 |
| [`handoff.md`](handoff.md) | 交接说明与边界约定 | 接手开发者 |
| [`PLAN.md`](PLAN.md) | 迭代计划与优先级 | 项目管理者 |
| [`STATUS.md](STATUS.md) 当前状态与风险（含版本历史、编译状态、风险点） | 团队 |
| [`本地编译步骤.md`](本地编译步骤.md) | 本机/CI 编译实操 | 构建负责人 |

---

---

## 8. 风险与建议

| 风险 | 状态 | 说明 |
|---|---|---|
| 事件订阅泄漏 / 跨线程异常 | ✅ 已解决 | `RunningStateChanged` 补 `SafeInvoke` 封送；`SafeInvoke` 增加 `IsDisposed/IsHandleCreated` 防护 |
| SafeInvoke 死锁 | ✅ 已缓解 | 保持 `Invoke`（与 `LogManager` 一致），增加句柄防护规避关闭期 `ObjectDisposedException` |
| 频繁翻转 bool 标签不更新 | ✅ 已解决 | `UpdateValue` 单调时间戳下限改为网关 UTC 时钟；快照时间戳改用网关接收时刻 |
| 未运行时测试 | 🔴 高 | 无 OPC DA 真实环境验证；编译 0 警告 0 错误，逻辑已静态核对 |
| [Conditional("DEBUG")] 排障信息丢失 | 🟡 中 | Release 模式移除诊断日志，生产环境排障需临时用 Debug 构建 |

**建议**：
1. 在实际 OPC DA 环境中启动网关，验证 3.5 万节点场景窗口保持响应
2. 如需新功能：按后续指令执行

---


程序含试用授权，到期需授权码激活。`Keygen` 子项目为离线授权码计算工具（需合法授权参数）。授权逻辑见 `Services/LicenseManager.cs`。

---

## 9. 联系

问题反馈、授权与技术支持：

📧 **408738480@qq.com**

---

## 10. 许可证

本项目为内部工业软件交付物，许可证条款以实际分发约定为准。
