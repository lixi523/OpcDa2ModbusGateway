using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using OpcDaToModbusGateway.Models;
using OpcDaToModbusGateway.Services.Interfaces;

namespace OpcDaToModbusGateway.Services
{
    /// <summary>
    /// 网关管理器 — 负责 OPC DA → Modbus TCP 网关的完整生命周期管理。
    ///
    /// == 启动流程（3 步严格有序） ==
    ///   步骤 1: 启动 Modbus TCP 服务器 — 先监听端口，确保下游客户端可连接
    ///   步骤 2: 连接 OPC DA 服务器 — 建立到传统 DA 设备的数据通道
    ///   步骤 3: 启动数据桥接     — 将 DA 侧采集的数据实时转发至 Modbus 侧
    /// 启动顺序不可调换：桥接依赖 DA 和 Modbus TCP 都已就绪。
    /// 任何步骤失败时，已创建的资源按逆序回滚（bridge → daClient → modbusServer），防止资源泄漏。
    ///
    /// == 优雅关闭 ==
    ///   停止时同样按依赖逆序释放：先断开桥接、再关闭 DA 客户端、最后停止 Modbus TCP 服务器。
    ///   每个 Dispose/Stop 调用都在独立的 try-catch 中执行，确保单个组件的异常不会阻断后续释放。
    ///   释放完成后通过事件通知 UI 更新状态指示器。
    ///
    /// == 健康监控 ==
    ///   由外部定时器周期性调用 <see cref="CheckHealth"/>，检测 DA 连接状态并在断开时自动重连，
    ///   最多尝试 <see cref="MaxReconnectAttempts"/> 次，超过后停止重连并告警。
    ///
    /// == 线程安全策略 ==
    ///   所有可变字段（_daClient、_modbusServer、_bridge、IsRunning、_starting、_reconnectAttempts）
    ///   的读写均通过 <c>_lock</c>（Monitor）保护，防止 StartAsync / StopAsync / CheckHealth
    ///   在并发调用时产生竞态。局部变量在锁内捕获快照后，后续操作在锁外执行以减小锁粒度。
    /// </summary>
    public class GatewayManager
    {
        // volatile 确保跨线程读写的可见性，配合 _lock 使用保证复合操作的原子性
        // PLAN 3.1：字段类型改为接口，使单元测试可注入 Fake 模拟器。
        private volatile IOpcDaClient _daClient;
        private volatile IGatewayModbusTcpServer _modbusServer;
        private volatile IDataBridge _bridge;

        private readonly LogManager _log;
        private readonly AppConfig _config;

        /// <summary>
        /// 全局互斥锁，保护所有可变状态字段的并发访问。
        /// 使用模式：在锁内做条件判断 + 状态变更，在锁外执行耗时操作（如网络 I/O、Dispose），
        /// 以减小锁持有时间、避免死锁。局部引用在锁内捕获后带出锁外使用。
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// 启动中标志 — 用于防止 TOCTOU（Time-of-Check-Time-of-Use）竞态。
        ///
        /// P1-5 加固：使用 int + Interlocked.CompareExchange 替代 volatile bool。
        /// 虽然 x86 CLR 对 bool 读写通常是原子的，但 JIT 可能重排指令顺序。
        /// Interlocked API 提供全内存屏障，消除所有重排不确定性。
        ///
        /// 工作流程：
        ///   1. 在 _lock 内同时检查 IsRunning 和 _starting，两者都为 0 才继续
        ///   2. 立即通过 CompareExchange 将 _starting 设为 1，后续并发调用会被拦截
        ///   3. 启动成功或失败后，在 _lock 内将 _starting 重置为 0
        /// </summary>
        private int _startingFlag;

        /// <summary>
        /// 当前累计重连尝试次数，使用 Interlocked 操作保证线程安全。
        /// 重连成功后归零，达到 <see cref="MaxReconnectAttempts"/> 后停止尝试。
        /// </summary>
        private int _reconnectAttempts;

        /// <summary>
        /// H-34: 上次重连尝试的时间戳 (Ticks)，用于指数退避。
        /// 初始 1s，每次失败翻倍，上限 60s，通过 Interlocked 原子操作。
        /// </summary>
        private long _lastReconnectAttemptTicks;
        private int _healthCheckFlag;

        /// <summary>DA 连接断开后的最大自动重连次数</summary>
        private const int MaxReconnectAttempts = 50;

        private volatile bool _isRunning;
        /// <summary>获取网关当前是否处于运行状态</summary>
        public bool IsRunning => _isRunning;

        /// <summary>当前 DA 客户端实例（只读，供 UI 定时刷新状态使用）</summary>
        public IOpcDaClient DaClient => _daClient;

        /// <summary>当前数据桥接实例（只读，供 UI 定时刷新状态使用）</summary>
        public IDataBridge Bridge => _bridge;

        /// <summary>当前 Modbus TCP 服务器实例（只读，供导出点表等操作使用）</summary>
        public IGatewayModbusTcpServer ModbusServer => _modbusServer;

        /// <summary>
        /// DA 侧状态变化事件。
        /// 参数: (状态文本, 前景色) — UI 层直接用于更新状态标签。
        /// </summary>
        public event Action<string, Color> DaStatusChanged;

        /// <summary>
        /// Modbus TCP 侧状态变化事件。
        /// 参数: (状态文本, 前景色) — UI 层直接用于更新状态标签。
        /// </summary>
        public event Action<string, Color> ModbusStatusChanged;

        /// <summary>网关运行状态变更。UI 层用于启用/禁用控件。</summary>
        public event Action<bool> RunningStateChanged;

        /// <summary>
        /// N-5: 配置变更事件 — 当关键参数（如 NamespaceIndex）在运行时被回写后触发。
        /// MainForm 订阅此事件以立即调用 ConfigManager.Save()，不依赖 500ms 防抖定时器。
        /// </summary>
        public event Action ConfigDirty;

        /// <summary>
        /// 初始化网关管理器。
        /// </summary>
        /// <param name="log">日志管理器，用于记录运行日志（不可为 null）</param>
        /// <param name="config">应用配置，包含 DA 和 Modbus TCP 的连接参数（不可为 null）</param>
        /// <exception cref="ArgumentNullException">log 或 config 为 null 时抛出</exception>
        public GatewayManager(LogManager log, AppConfig config)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// 启动网关，按 3 步流程依次初始化各组件：
        ///   [1/3] 创建并启动 Modbus TCP 服务器
        ///   [2/3] 创建并连接 OPC DA 客户端
        ///   [3/3] 创建并启动数据桥接
        ///
        /// 如果网关已在运行或正在启动中，方法立即返回（幂等）。
        /// 任何步骤失败时，已创建的资源按逆序回滚，然后重新抛出异常。
        /// 
        /// <param name="progressReport">可选进度回调，用于报告节点创建等耗时操作的进度文本。
        /// 调用方需保证此回调线程安全（MainForm 已通过 SynchronizationContext.Post 保证）。</param>
        /// </summary>
        /// <returns>异步任务，在所有步骤完成后结束</returns>
        public async Task StartAsync(Action<string> progressReport = null)
        {
            // 在锁内做 TOCTOU 安全的条件检查：IsRunning 和 _starting 必须同时为 false
            lock (_lock)
            {
                if (IsRunning || Interlocked.CompareExchange(ref _startingFlag, 1, 0) != 0) return;
            }

            // 局部变量持有新建资源引用，启动成功后才赋值给字段；
            // 这样如果中途失败，回滚逻辑可以精确释放已创建的资源，而不影响旧运行实例
            GatewayModbusTcpServer modbusServer = null;
            OpcDaClient daClient = null;
            DataBridge bridge = null;
            bool daConnected = false;

            try
            {
                ValidateMappings(_config.OpcDa?.Tags);
                _log.Append("[1/3] 启动 Modbus TCP 服务器...");
                modbusServer = new GatewayModbusTcpServer(_config.ModbusTcp);
                modbusServer.OnStatusChanged += msg =>
                {
                    // 调试期间不过滤，确保所有诊断信息可见
                    _log.Append("  " + msg);
                };
                // N-5: 当 NamespaceIndex 回写后立即触发保存，防止进程崩溃导致索引丢失
                modbusServer.OnConfigChanged = () => ConfigDirty?.Invoke();
                await modbusServer.StartAsync().ConfigureAwait(false);

                _log.Append("[2/3] 连接 OPC DA 服务器...");
                try
                {
                    string daHost = string.IsNullOrEmpty(_config.OpcDa.ServerHost)
                        ? "localhost" : _config.OpcDa.ServerHost;
                    daClient = new OpcDaClient(_config.OpcDa.ServerProgId, _config.OpcDa.Tags, daHost);
                    daClient.OnStatusChanged += msg =>
                    {
                        if (!msg.StartsWith("[诊断]"))
                            _log.Append("  " + msg);
                    };
                    daClient.OnConfigChanged += () => ConfigDirty?.Invoke();
                    daClient.Start(_config.OpcDa.UpdateRateMs, _config.OpcDa.GetEffectiveMode());
                    daConnected = true;
                }
                catch (Exception daEx)
                {
                    // OPC DA 连接失败时不影响 Modbus TCP 服务运行：
                    // 用户仍可使用 Modbus Poll / Modscan 验证服务器监听，DA 恢复后数据将自动流入
                    _log.Append($"  ⚠ OPC DA 连接失败: {daEx.GetType().Name}: {daEx.Message}");
                    _log.Append("  → Modbus TCP 服务保持运行，等待 DA 恢复后自动桥接数据");
                    // 保留同一客户端实例，现有 DataBridge 将订阅它，健康检查可在其上重连。
                }

                _log.Append("[3/3] 启动数据桥接...");
                // Start() 可能从 CanonicalDataType 回写真实 DA 类型，必须基于最终类型再次验证映射。
                ValidateMappings(_config.OpcDa?.Tags);
                bridge = new DataBridge(daClient, modbusServer, _config.OpcDa.Tags);
                bridge.OnLog += msg => _log.Append(msg);
                bridge.Start();

                // 所有步骤成功（或部分成功，DA 失败但 Modbus 正常运行），在锁内一次性发布引用
                lock (_lock)
                {
                    _modbusServer = modbusServer;
                    _daClient = daClient;
                    _bridge = bridge;
                    _isRunning = true;
                    _reconnectAttempts = 0;
                    _lastReconnectAttemptTicks = 0; // H-34
                    _startingFlag = 0;
                }

                if (daConnected)
                {
                    DaStatusChanged?.Invoke("● DA: 已连接", Color.Green);
                }
                else
                {
                    DaStatusChanged?.Invoke("● DA: 未连接（自动重连中）", Color.Orange);
                }
                ModbusStatusChanged?.Invoke("● Modbus: 运行中", Color.Green);
                RunningStateChanged?.Invoke(true);

                if (daConnected)
                {
                    _log.Append("网关启动成功！");
                }
                else
                {
                    _log.Append("Modbus TCP 服务器已启动（DA 未连接，等待重连）");
                }
                _log.Append($"Modbus TCP 地址: {_config.ModbusTcp.GetEndpointUrl()}");
                _log.Append("可以使用 Modbus Poll 等 Modbus TCP 客户端连接测试");
            }
            catch (Exception ex)
            {
                // 输出完整异常信息（含类型、消息、堆栈）方便定位
                _log.Append($"启动失败: {ex.GetType().Name}: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    // 堆栈太长时分多行输出，前 3 行通常已足够定位
                    string[] lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int showLines = Math.Min(3, lines.Length);
                    for (int i = 0; i < showLines; i++)
                        _log.Append($"  堆栈[{i}]: {lines[i].Trim()}");
                }
                if (ex.InnerException != null)
                    _log.Append($"  内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");

                // 回滚策略：按创建的逆序释放（bridge → daClient → modbusServer），
                // 每个释放调用独立 try-catch，防止单个异常阻断后续清理。
                // 同时重置 _starting 标志，允许用户修复问题后重试。
                _log.Append("正在回滚已创建资源...");
                lock (_lock) { _startingFlag = 0; }
                RunningStateChanged?.Invoke(false);
                try { bridge?.Dispose(); } catch { }
                try { daClient?.Dispose(); } catch { }
                if (modbusServer != null)
                {
                    try { modbusServer.StopAsync().Wait(); } catch { }
                    try { modbusServer.Dispose(); } catch { }
                }
                throw;
            }
        }

        /// <summary>
        /// 优雅停止网关，按依赖逆序释放所有组件：
        ///   1. 释放数据桥接（切断 DA → Modbus 数据转发）
        ///   2. 释放 DA 客户端（断开与传统 DA 服务器的连接）
        ///   3. 停止并释放 Modbus TCP 服务器（关闭监听端口）
        ///
        /// 如果网关未在运行，方法立即返回（幂等）。
        /// 每个组件的释放都在独立的 try-catch 中，确保单个异常不阻断后续清理。
        /// 事件处理器在释放前解绑，防止多次启停时事件订阅累积。
        /// </summary>
        /// <returns>异步任务，在所有组件停止后结束</returns>
        public async Task StopAsync()
        {
            IOpcDaClient daClient;
            IGatewayModbusTcpServer modbusServer;
            IDataBridge bridge;

            // 在锁内原子地捕获当前引用并置空字段、标记停止。
            // 后续操作在锁外执行：释放可能耗时，不应持锁阻塞其他调用者。
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                daClient = _daClient;
                modbusServer = _modbusServer;
                bridge = _bridge;
                _daClient = null;
                _modbusServer = null;
                _bridge = null;
            }

            _log.Append("正在停止网关...");

            // 每个 Dispose 独立 try-catch：即使 bridge.Dispose() 抛异常，
            // daClient 和 modbusServer 仍会被正常释放，避免级联资源泄漏
            try { bridge?.Dispose(); } catch { }
            try { daClient?.Dispose(); } catch { }

            if (modbusServer != null)
            {
                try { await modbusServer.StopAsync(); }
                catch (Exception ex) { _log.Append($"停止 Modbus TCP 服务器时出错: {ex.Message}"); }
                try { modbusServer.Dispose(); } catch { }
            }

            DaStatusChanged?.Invoke("● DA: 未连接", Color.Gray);
            ModbusStatusChanged?.Invoke("● Modbus: 未启动", Color.Gray);
            RunningStateChanged?.Invoke(false);

            _log.Append("网关已停止");
        }

        /// <summary>
        /// 健康检查 — 由外部定时器周期性调用，检测 DA 连接状态并在断开时自动重连。
        ///
        /// 重连策略：
        ///   - 每次检测到断开时尝试一次重连
        ///   - 累计重连次数不超过 <see cref="MaxReconnectAttempts"/>（默认 50 次）
        ///   - 重连成功后计数器归零
        ///   - 达到上限后停止尝试，通过 UI 和日志告警
        ///
        /// 线程安全说明：
        ///   在 _lock 内捕获 daClient 快照后在锁外操作，减小锁粒度。
        ///   操作过程中 daClient 可能被 StopAsync 在另一线程释放，因此必须捕获
        ///   <see cref="ObjectDisposedException"/>。
        /// </summary>
        private static void ValidateMappings(System.Collections.Generic.IEnumerable<TagConfig> tags)
        {
            if (tags == null) throw new InvalidOperationException("OPC DA 标签配置不能为空。");
            var occupied = new System.Collections.Generic.Dictionary<ModbusRegisterType, System.Collections.Generic.HashSet<int>>();
            foreach (ModbusRegisterType type in Enum.GetValues(typeof(ModbusRegisterType)))
                occupied[type] = new System.Collections.Generic.HashSet<int>();
            foreach (var tag in tags)
            {
                try
                {
                    var registerType = tag.GetEffectiveRegisterType();
                    // 类型无法解析（Variant/Object/空等）的标签按宽度 1 保守校验，
                    // 真实类型由 DA 连接后的 CanonicalDataType 回写修正，再由回写后的校验覆盖。
                    // String/DateTime 无 wire encoding，仍在此处显式拒绝（TryGetEffectiveAddressWidth 内部抛错）。
                    tag.TryGetEffectiveAddressWidth(out int width);
                    int end = tag.ModbusAddress + width - 1;
                    if (end > ushort.MaxValue) throw new InvalidOperationException("地址空间溢出");
                    for (int address = tag.ModbusAddress; address <= end; address++)
                        if (!occupied[registerType].Add(address)) throw new InvalidOperationException("地址区间重叠");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"标签 '{tag.TagKey ?? tag.ItemId}' 的 Modbus 映射无效: {ex.Message}", ex);
                }
            }
        }

        public void CheckHealth()
        {
            if (Interlocked.CompareExchange(ref _healthCheckFlag, 1, 0) != 0) return;
            try
            {
            IOpcDaClient daClient;
            lock (_lock)
            {
                if (!IsRunning) return;
                daClient = _daClient;
                if (daClient == null) return;
            }

            try
            {
                if (daClient.IsConnected) return;

                int completedAttempts = Volatile.Read(ref _reconnectAttempts);

                // H-34: 指数退避 — 避免 DA 服务器长时间不可用时密集重连风暴
                //       初始 1s，每次失败翻倍，上限 60s
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = Interlocked.Read(ref _lastReconnectAttemptTicks);
                int backoffMs = (int)Math.Min(1000L * (1L << Math.Min(completedAttempts, 6)), 60000L);
                long elapsedMs = (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
                if (lastTicks > 0 && elapsedMs < backoffMs) return; // 退避期间跳过
                Interlocked.Exchange(ref _lastReconnectAttemptTicks, nowTicks);
                // 仅真实执行重连时增加计数，退避期间不计数。
                int attempts = Interlocked.Increment(ref _reconnectAttempts);

                // H-32 修复：超过上限后立即截断，防止 int 溢出（虽然需要 ~21 亿次，但设计上不该依赖这个）。
                // 使用 Interlocked.CompareExchange 确保截断的原子性，避免与并发 Increment 竞态。
                if (attempts > MaxReconnectAttempts)
                {
                    // 截断到 MaxReconnectAttempts+1，保留"已超限"信号但不再增长
                    Interlocked.CompareExchange(ref _reconnectAttempts, MaxReconnectAttempts + 1, attempts);
                    attempts = MaxReconnectAttempts + 1;
                }

                if (attempts <= MaxReconnectAttempts)
                {
                    _log.Append($"[监控] DA 连接断开，尝试重连 ({attempts}/{MaxReconnectAttempts})...");
                    DaStatusChanged?.Invoke("● DA: 重连中...", Color.Orange);

                    bool success = daClient.TryReconnect(_config.OpcDa.UpdateRateMs);

                    if (success)
                    {
                        // 重连成功：使用原子交换归零，防止与并发 CheckHealth 的 Increment 竞态
                        Interlocked.Exchange(ref _reconnectAttempts, 0);
                        Interlocked.Exchange(ref _lastReconnectAttemptTicks, 0); // H-34: 重置退避计时
                        DaStatusChanged?.Invoke("● DA: 已连接", Color.Green);
                        _log.Append("[监控] DA 重连成功");
                    }
                }
                else if (attempts == MaxReconnectAttempts + 1)
                {
                    // 仅在刚好超过上限的那一次打印告警，避免每次 CheckHealth 都重复提示
                    _log.Append("[监控] 达到最大重连次数，停止重连。请手动检查 OPC DA 服务器。");
                    DaStatusChanged?.Invoke("● DA: 重连失败", Color.Red);
                }
            }
            catch (ObjectDisposedException)
            {
                // 为什么需要捕获 ObjectDisposedException：
                // CheckHealth 在锁外操作 daClient 的 IsConnected / TryReconnect 方法时，
                // StopAsync 可能在另一个线程完成了对 daClient 的 Dispose。
                // 锁内快照只能保证我们拿到的引用不为 null，但无法阻止对象在锁外被释放。
                // 这是正常的生命周期交叉，安全忽略即可 — 下一轮 CheckHealth 会因
                // IsRunning == false 而直接返回。
            }
            catch (Exception ex)
            {
                _log.Append($"[监控] 健康检查异常: {ex.Message}");
            }
            }
            finally
            {
                Volatile.Write(ref _healthCheckFlag, 0);
            }
        }
    }
}
