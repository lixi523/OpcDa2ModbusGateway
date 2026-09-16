using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpcDaToModbusGateway.Models;

namespace OpcDaToModbusGateway.Services
{
    /// <summary>
    /// 配置管理器 — 负责应用配置文件（config.json）的完整生命周期管理。
    ///
    /// == 配置生命周期 ==
    ///   1. Load()      — 从应用目录读取 config.json，反序列化为 AppConfig 对象，
    ///                    然后调用 ApplyBackwardCompatDefaults() 为旧版本缺失的字段填充安全默认值，
    ///                    最后为每个 Tag 分配运行时唯一键（AssignTagKeys）。
    ///   2. 运行时修改  — UI 层直接修改 Config 对象属性（如选择 DA 服务器、修改端口号等）。
    ///   3. Save()      — 防抖保存：500ms 内的多次调用合并为一次磁盘写入，
    ///                    减少 UI 频繁变更时的 I/O 压力。
    ///   4. SaveImmediate() — 立即保存（跳过防抖），用于程序退出等关键场景。
    ///
    /// == 原子写入策略 ==
    ///   写入采用"临时文件 + File.Replace"模式：先写入 .tmp 文件，再原子替换目标文件。
    ///   确保读取端永远不会看到写了一半的 JSON（torn write），即使写入过程中断电也安全。
    ///
    /// == 并发保存保护 ==
    ///   使用 Monitor（lock）+ 手动 Monitor.Enter 的组合保护 DoSave()，
    ///   确保防抖 Timer 回调与 SaveImmediate() 不会并发执行序列化+写入。
    ///   Monitor 是可重入的，同一线程多次 Enter 不会死锁。
    /// </summary>
    public class ConfigManager : IDisposable
    {
        private readonly LogManager _log;
        private readonly string _baseDirectory;

        /// <summary>
        /// 保存操作的全局锁。
        /// 保护 Save() 的防抖 Timer 创建/重置、SaveImmediate() 的直接写入、以及 DoSave() 内部的
        /// 序列化+文件写入流程。所有涉及 config.json 磁盘写入的路径都必须持有此锁。
        /// </summary>
        private readonly object _saveLock = new object();

        /// <summary>
        /// 防抖定时器 — Save() 调用时重置计时，500ms 无新调用后触发 DoSave()。
        /// 复用同一个 Timer 实例（通过 Change 重置），避免反复创建/销毁带来的 GC 压力。
        /// </summary>
        private System.Threading.Timer _debounceTimer;

        /// <summary>
        /// H-40: 文件系统监视器 — 监听 config.json 的外部修改。
        /// </summary>
        private FileSystemWatcher _configWatcher;
        private System.Threading.Timer _watchDebounce;
        private int _watchGeneration;
        private readonly object _watchCallbackLock = new object();
        private int _activeWatchCallbacks;
        private readonly ManualResetEventSlim _watchCallbacksIdle = new ManualResetEventSlim(true);
        private volatile bool _suppressWatch;

        /// <summary>当前应用配置对象，Load() 后可读，UI 层可直接修改其属性</summary>
        public AppConfig Config { get; private set; }

        /// <summary>
        /// 加载时的原始 JSON 文本。
        /// 保留原始文本的目的是支持向后兼容判断：通过检查 JSON 中是否包含某个字段名，
        /// 区分"用户显式设置了默认值"和"旧版本配置根本没有这个字段"。
        /// </summary>
        public string RawJson { get; private set; }

        /// <summary>
        /// H-40: 配置文件被外部修改时触发。MainForm 订阅后根据网关状态决定自动重载或提示。
        /// </summary>
        public event Action ConfigFileChanged;

        /// <summary>
        /// 初始化配置管理器。
        /// </summary>
        /// <param name="log">日志管理器，用于记录配置操作日志（不可为 null）</param>
        /// <exception cref="ArgumentNullException">log 为 null 时抛出</exception>
        public ConfigManager(LogManager log)
            : this(log, AppDomain.CurrentDomain.BaseDirectory)
        {
        }

        public ConfigManager(LogManager log, string baseDirectory)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? throw new ArgumentException("配置目录不能为空", nameof(baseDirectory))
                : baseDirectory;
        }

        /// <summary>
        /// 从应用目录加载 config.json 配置文件。
        ///
        /// 加载流程：
        ///   1. 检查文件是否存在，不存在则弹窗提示并返回 false
        ///   2. 读取文件内容并反序列化为 AppConfig
        ///   3. 尝试从 tags.json 加载标签数据（P1-1 增量保存优化）
        ///   4. 调用 ApplyBackwardCompatDefaults() 填充旧版本缺失字段的默认值
        ///   5. 为 OPC DA 标签列表分配运行时唯一键
        ///
        /// 加载失败时通过 MessageBox 向用户提示错误原因。
        /// </summary>
        /// <returns>加载成功返回 true；文件不存在或解析失败返回 false</returns>
        public bool Load()
        {
            string configPath = GetConfigPath();

            if (!File.Exists(configPath))
            {
                MessageBox.Show(
                    "找不到配置文件 config.json！\n请确保该文件与程序在同一目录下。",
                    "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                RawJson = File.ReadAllText(configPath);
                var loadedConfig = JsonConvert.DeserializeObject<AppConfig>(RawJson) ?? new AppConfig();
                if (Config == null)
                {
                    Config = loadedConfig;
                }
                else
                {
                    // 保持根对象引用稳定，确保 GatewayManager 等长期持有者看到热重载后的配置。
                    Config.OpcDa = loadedConfig.OpcDa;
                    Config.ModbusTcp = loadedConfig.ModbusTcp;
                    Config.LastConnectedProgId = loadedConfig.LastConnectedProgId;
                    Config.AutoConnectDa = loadedConfig.AutoConnectDa;
                    Config.AutoStartModbus = loadedConfig.AutoStartModbus;
                    Config.AutoStartWithWindows = loadedConfig.AutoStartWithWindows;
                    Config.EnableWatchdog = loadedConfig.EnableWatchdog;
                    Config.AuthorizationCode = loadedConfig.AuthorizationCode;
                }

                // P1-1: 优先从独立的 tags.json 加载标签数据（增量保存优化）
                // 如果 tags.json 不存在，回退到 config.json 中的内联 Tags（向后兼容）
                string tagsPath = GetTagsPath();
                bool loadedInlineTags = !File.Exists(tagsPath);
                if (!loadedInlineTags)
                {
                    try
                    {
                        string tagsJson = File.ReadAllText(tagsPath);
                        var tagsWrapper = JsonConvert.DeserializeAnonymousType(tagsJson,
                            new { Tags = new List<TagConfig>() });
                        if (tagsWrapper?.Tags != null)
                        {
                            if (Config.OpcDa == null) Config.OpcDa = new OpcDaConfig();
                            Config.OpcDa.Tags = tagsWrapper.Tags;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Append($"[配置] 加载 tags.json 失败，回退到 config.json: {ex.Message}");
                        // 保留 config.json 中的内联 Tags（如果存在）
                    }
                }

                // 为旧版本配置中新增的字段填充安全默认值
                ApplyBackwardCompatDefaults();
                // N-8: 为尚未分配 TagKey 的标签生成唯一键并持久化。
                bool keysAssigned = TagConfig.AssignTagKeys(Config.OpcDa?.Tags);
                _suppressWatch = true;
                try
                {
                    if (loadedInlineTags && Config.OpcDa?.Tags?.Count > 0)
                    {
                        if (SaveAllImmediate())
                            _log?.Append("[配置] 已将内联标签迁移到 tags.json");
                        else
                            _log?.Append("[配置] 内联标签迁移失败，已保留原 config.json");
                    }
                    else if (keysAssigned)
                    {
                        if (SaveAllImmediate())
                            _log?.Append("[配置] 已为新标签分配 TagKey 并持久化");
                        else
                            _log?.Append("[配置] TagKey 持久化失败");
                    }
                }
                finally
                {
                    _suppressWatch = false;
                }

                // H-40: 启动配置文件监视
                StartWatching();

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载配置失败:\n{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// 为旧版本配置文件中新引入的字段填充向后兼容默认值。
        ///
        /// 设计原则：
        ///   - 对于不影响安全的功能字段（如 ListenAddress、Port），使用便利默认值让用户开箱即用
        ///   - 对于安全敏感字段（如 AutoAcceptCertificates），通过检查 RawJson 中是否存在该字段名
        ///     来区分"旧配置缺失"和"用户显式设置"：缺失时默认 false（安全优先），
        ///     用户需在 UI 中显式启用
        ///   - 对于数值型字段，值为 0 或负数视为"未设置"，填充合理的默认值
        /// </summary>
        private void ApplyBackwardCompatDefaults()
        {
            // 确保 ModbusTcp 配置节点存在（旧版本可能没有相关配置）
            if (Config.ModbusTcp == null)
            {
                Config.ModbusTcp = new ModbusTcpConfig();
            }

            var mb = Config.ModbusTcp;

            // 监听地址默认 127.0.0.1（安全默认值：仅本机回环，防止未配置时暴露到全网）
            // 生产环境需要外部客户端访问时，显式配置为 0.0.0.0 或指定内网 IP
            if (string.IsNullOrEmpty(mb.ListenAddress))
                mb.ListenAddress = "127.0.0.1";

            // 端口号默认 502（Modbus TCP 标准端口），防止 Port 为 0 时生成无效的端点地址
            if (mb.Port <= 0)
                mb.Port = 502;

            // 确保 OpcDa 配置节点存在
            if (Config.OpcDa == null)
            {
                Config.OpcDa = new OpcDaConfig();
            }

            // DA 数据更新频率默认 1000ms — 适合大多数工业场景的采集周期
            if (Config.OpcDa.UpdateRateMs <= 0)
            {
                Config.OpcDa.UpdateRateMs = 1000;
            }

            // 首次运行：未配置 ProgId 时填充默认值，让用户开箱即用
            if (string.IsNullOrEmpty(Config.OpcDa.ServerProgId))
            {
                Config.OpcDa.ServerProgId = "Matrikon.OPC.Simulation.1";
            }
        }

        /// <summary>
        /// 将当前配置保存到 config.json（防抖模式）。
        ///
        /// 防抖机制：调用后启动/重置一个 500ms 的单次定时器，
        /// 如果 500ms 内没有新的 Save() 调用，定时器回调执行 DoSave()。
        /// 如果 500ms 内有新调用，定时器被重置，从最后一次调用开始重新计时。
        /// 这样可以合并 UI 中连续多次属性变更（如拖动滑块），只写一次磁盘。
        ///
        /// 线程安全：在 _saveLock 内操作 Timer，防止并发 Save 导致 Timer 竞态。
        /// </summary>
        public void Save()
        {
            if (Config == null) return;

            lock (_saveLock)
            {
                // 复用 Timer 实例：首次创建，后续通过 Change() 重置触发时间。
                // 避免每次 Save 都 new Timer，减少 GC 压力和系统资源消耗。
                if (_debounceTimer == null)
                {
                    // dueTime=500ms 后触发一次 DoSave，period=Infinite 表示不周期触发
                    _debounceTimer = new System.Threading.Timer(_ => DoSave(), null, AppConstants.ConfigDebounceMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    // 重置 Timer：从此刻开始重新计时 500ms
                    _debounceTimer.Change(AppConstants.ConfigDebounceMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// 立即保存当前配置到 config.json（跳过防抖）。
        ///
        /// 适用场景：程序退出、用户点击"保存"按钮等需要确保数据立即落盘的关键时刻。
        /// 调用时会先销毁待执行的防抖 Timer（如果存在），防止防抖回调与本次写入并发。
        ///
        /// 线程安全：在 _saveLock 内执行，与 Save() 和 DoSave() 互斥。
        /// </summary>
        public void SaveImmediate()
        {
            if (Config == null) return;
            lock (_saveLock)
            {
                // 取消待执行的防抖写入，防止 Timer 回调与本次立即写入并发执行 DoSave
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                DoSave();
            }
        }

        /// <summary>
        /// 执行实际的配置序列化与文件写入。
        ///
        /// P1-1 增量保存：将 Tags 数组从 config.json 分离到独立的 tags.json。
        /// config.json 仅包含网关设置（端口、安全策略等，约 2KB），
        /// tags.json 仅包含标签数据（35K 标签场景下约 8MB）。
        /// 这样 UI 设置变更（频繁）只触发 config.json 的小文件写入，
        /// 标签数据（仅导入时变更）只在 SaveTagsImmediate 时单独写入。
        ///
        /// 为什么使用 Monitor.Enter 而非 lock 语句：
        ///   DoSave() 可能被 Timer 回调线程调用（Save 的防抖触发），也可能被 UI 线程直接调用
        ///   （SaveImmediate）。两者都需要与 _saveLock 互斥。
        ///   Monitor 是可重入的（同一线程多次 Enter 不阻塞），所以这里显式使用
        ///   Monitor.Enter/Exit 确保无论从哪条路径调用都能正确持有锁。
        ///
        /// 写入策略：
        ///   1. 在锁内将 Config 序列化为 JSON 字符串快照（Tags 临时置 null 以排除）
        ///   2. 将快照写入临时文件（.tmp）
        ///   3. 用 File.Replace 原子替换目标文件（不存在时 File.Move）
        ///   tags.json 由 SaveTagsImmediate 单独写入（不在 DoSave 中，避免全量序列化）
        /// </summary>
        private void DoSave()
        {
            if (Config == null) return;

            Monitor.Enter(_saveLock);
            try
            {
                var configSnapshot = JObject.FromObject(Config);
                (configSnapshot["OpcDa"] as JObject)?.Remove("Tags");
                AtomicWrite(GetConfigPath(), configSnapshot.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log.Append($"保存配置失败: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(_saveLock);
            }
        }

        /// <summary>
        /// P1-1: 立即保存标签数据到独立的 tags.json 文件。
        /// 仅在标签导入/变更时调用，不触发全量 config.json 序列化。
        /// 原子写入策略与 DoSave 保持一致（临时文件 + File.Replace）。
        /// </summary>
        public void SaveTagsImmediate()
        {
            if (Config?.OpcDa?.Tags == null) return;

            lock (_saveLock)
            {
                try
                {
                    string tagsPath = GetTagsPath();
                    var tagsWrapper = new { Tags = Config.OpcDa.Tags };
                    string tagsJson = JsonConvert.SerializeObject(tagsWrapper, Formatting.Indented);

                    AtomicWrite(tagsPath, tagsJson);

                    _log?.Append($"[配置] 已保存 {Config.OpcDa.Tags.Count} 个标签到 tags.json");
                }
                catch (Exception ex)
                {
                    _log?.Append($"保存标签配置失败: {ex.Message}");
                }
            }
        }

        public bool SaveAllImmediate()
        {
            if (Config?.OpcDa?.Tags == null) return false;

            lock (_saveLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                string tagsBackup = null;
                bool tagsExisted = File.Exists(GetTagsPath());
                try
                {
                    string tagsJson = JsonConvert.SerializeObject(
                        new { Tags = Config.OpcDa.Tags }, Formatting.Indented);
                    var configSnapshot = JObject.FromObject(Config);
                    (configSnapshot["OpcDa"] as JObject)?.Remove("Tags");

                    if (tagsExisted) tagsBackup = File.ReadAllText(GetTagsPath());
                    AtomicWrite(GetTagsPath(), tagsJson);
                    try
                    {
                        AtomicWrite(GetConfigPath(), configSnapshot.ToString(Formatting.Indented));
                    }
                    catch
                    {
                        if (tagsExisted)
                            AtomicWrite(GetTagsPath(), tagsBackup);
                        else if (File.Exists(GetTagsPath()))
                            File.Delete(GetTagsPath());
                        throw;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _log?.Append($"保存完整配置失败: {ex.Message}");
                    return false;
                }
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }

        /// <summary>
        /// 更新 OPC DA 服务器的 ProgId 并立即保存。
        ///
        /// 变更和保存在 _saveLock 内分两步执行：先在锁内修改 Config 属性（确保与 DoSave 的
        /// 序列化互斥，不会读到 torn state），然后调用 SaveImmediate 在锁外触发保存。
        /// </summary>
        /// <param name="progId">OPC DA 服务器的 ProgId（如 "Matrikon.OPC.Simulation.1"）</param>
        public void SaveProgId(string progId)
        {
            if (Config == null) return;
            lock (_saveLock)
            {
                if (Config.OpcDa == null)
                    Config.OpcDa = new OpcDaConfig();
                Config.OpcDa.ServerProgId = progId;
            }
            SaveImmediate();
            _log.Append($"已保存服务器 ProgId: {progId}");
        }

        /// <summary>
        /// 设置或取消 Windows 开机自动启动。
        /// 通过在"启动"文件夹中创建/删除 .lnk 快捷方式实现。
        /// </summary>
        /// <param name="enable">true 添加开机启动；false 移除</param>
        public void SetAutoStart(bool enable)
        {
            try
            {
                if (enable)
                {
                    CreateStartupShortcut();
                    _log.Append("[开机启动] 已添加开机启动快捷方式");
                }
                else
                {
                    RemoveAutoStartShortcut();
                    _log.Append("[开机启动] 已移除开机启动快捷方式");
                }
            }
            catch (Exception ex)
            {
                _log.Append($"[开机启动] 设置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 在 Windows "启动"文件夹中创建 .lnk 快捷方式，实现开机自启动。
        ///
        /// 实现方式：通过 COM 互操作调用 WScript.Shell 的 CreateShortcut 方法。
        /// 由于 WScript.Shell 是 late-bound（无类型库引用），使用反射调用 COM 成员。
        /// finally 块中通过 Marshal.ReleaseComObject 显式释放 COM 对象，防止引用泄漏。
        /// 快捷方式参数包含 --minimized，使程序启动时最小化到系统托盘。
        /// </summary>
        private void CreateStartupShortcut()
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "OpcDaToModbusGateway.lnk");

            string exePath = Application.ExecutablePath;

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });

                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                scType.InvokeMember("Arguments",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--minimized" });
                scType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(exePath) });
                // WindowStyle = 1 表示正常窗口（非最小化/最大化）
                scType.InvokeMember("WindowStyle",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { 1 });
                scType.InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    new object[] { "OPC DA to Modbus TCP Gateway" });
                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                // 显式释放 COM 对象：.NET GC 不保证及时回收 COM 引用，
                // 手动 ReleaseComObject 防止 WScript.Shell 进程残留
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        /// <summary>
        /// 删除"启动"文件夹中的 .lnk 快捷方式，取消开机自启动。
        /// 文件不存在时静默返回（幂等操作）。
        /// </summary>
        private void RemoveAutoStartShortcut()
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "OpcDaToModbusGateway.lnk");

            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }

        /// <summary>
        /// 获取配置文件的完整路径（应用目录下的 config.json）。
        /// </summary>
        /// <returns>配置文件的绝对路径</returns>
        private string GetConfigPath()
            => Path.Combine(_baseDirectory, "config.json");

        /// <summary>
        /// P1-1: 获取标签数据文件的完整路径（应用目录下的 tags.json）。
        /// </summary>
        private string GetTagsPath()
            => Path.Combine(_baseDirectory, "tags.json");

        // ================================================================
        //  H-40: 配置文件热加载感知
        // ================================================================

        /// <summary>
        /// 启动 FileSystemWatcher 监听 config.json 的外部修改。
        /// 防抖 500ms：编辑器保存时可能触发多次 Changed 事件。
        /// </summary>
        private void StartWatching()
        {
            // Load() 也可能由 watcher 回调触发；已有实例时复用，避免重载后重复创建 watcher/timer。
            if (Volatile.Read(ref _configWatcher) != null) return;

            try
            {
                string configPath = GetConfigPath();
                string dir = Path.GetDirectoryName(configPath);
                string file = Path.GetFileName(configPath);

                _configWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false  // 先不启动，等防抖设置完
                };

                int generation = Interlocked.Increment(ref _watchGeneration);
                _watchDebounce = new System.Threading.Timer(_ =>
                {
                    lock (_watchCallbackLock)
                    {
                        if (Volatile.Read(ref _watchGeneration) != generation) return;
                        _activeWatchCallbacks++;
                        _watchCallbacksIdle.Reset();
                    }

                    try
                    {
                        _log?.Append("[配置] 检测到 config.json 外部修改");
                        ConfigFileChanged?.Invoke();
                    }
                    finally
                    {
                        lock (_watchCallbackLock)
                        {
                            if (--_activeWatchCallbacks == 0)
                                _watchCallbacksIdle.Set();
                        }
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);

                _configWatcher.Changed += (s, e) =>
                {
                    if (_suppressWatch) return;
                    var debounce = _watchDebounce;
                    if (debounce == null) return;
                    try { debounce.Change(500, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                };

                _configWatcher.EnableRaisingEvents = true;
                _log?.Append("[配置] 文件监视已启动");
            }
            catch (Exception ex)
            {
                _log?.Append($"[配置] 文件监视启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止文件监视并释放资源。
        /// </summary>
        public void StopWatching()
        {
            // 与回调共用锁：返回后保证没有旧回调仍会触发 ConfigFileChanged。
            lock (_watchCallbackLock)
            {
                Interlocked.Increment(ref _watchGeneration);
            }
            var watcher = Interlocked.Exchange(ref _configWatcher, null);
            if (watcher != null)
            {
                try { watcher.EnableRaisingEvents = false; } catch { }
                try { watcher.Dispose(); } catch { }
            }

            var debounce = Interlocked.Exchange(ref _watchDebounce, null);
            try { debounce?.Dispose(); } catch { }
            _watchCallbacksIdle.Wait();
        }

        // H-40 改进：实现 IDisposable，确保 FileSystemWatcher 和 Timer 资源被正确释放
        public void Dispose()
        {
            StopWatching();
            _watchCallbacksIdle.Dispose();
        }
    }
}
