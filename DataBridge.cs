using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Opc.Ua;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// OPC DA 到 OPC UA 的数据桥接器，实现桥接（Bridge）设计模式。
    /// 
    /// <para>核心职责：将 OPC DA 客户端的数据变化事件实时转发到 OPC UA 服务器，
    /// 使两个异构 OPC 协议之间实现透明的数据流通。</para>
    /// 
    /// <para>数据流向：
    /// OPC DA 服务器 → DA 客户端回调（线程池线程） → 类型转换 → UA 变量节点更新 + 快照缓存</para>
    /// 
    /// <para>线程安全模型：
    /// - DA 回调（<see cref="OnDaDataChanged"/>）在线程池线程上执行，可能多个回调并发触发；
    /// - UI 定时读取（<see cref="GetSnapshots"/>）在 WinForms 定时器线程上执行；
    /// - 所有共享状态均通过 <see cref="ConcurrentDictionary{TKey,TValue}"/>、
    ///   <see cref="Interlocked"/> 和 volatile 保证安全，无需显式加锁。</para>
    /// 
    /// <para>所有权语义：
    /// DataBridge 不拥有 <paramref name="daClient"/> 和 <paramref name="uaServer"/> 的生命周期，
    /// 它们由外部（MainForm）创建和销毁。DataBridge 仅持有引用用于数据转发，
    /// Dispose 时只取消事件订阅，不释放 DA 客户端或 UA 服务器。</para>
    /// </summary>
    public class DataBridge : IDataBridge
    {
        // DA 客户端引用（所有权归 MainForm，此处仅用于订阅事件和读取数据）
        private readonly OpcDaClient _daClient;
        // UA 服务器引用（所有权归 MainForm，此处仅用于更新变量节点）
        private readonly GatewayOpcUaServer _uaServer;

        // 按 TagKey 索引的标签配置映射，用于在数据变化时快速查找标签元数据
        private readonly ConcurrentDictionary<string, TagConfig> _tagMap;
        // TagKey 的有序列表（构造时确定，保证 GetSnapshots 返回顺序与 UI 表格行顺序一致）
        private readonly List<string> _orderedKeys;

        // 缓存每个标签的 BuiltInType（避免每次数据变化时对 DataType 字符串做重复解析）
        private readonly Dictionary<string, BuiltInType> _cachedTypes = new Dictionary<string, BuiltInType>();

        // 运行时统计计数器，使用 Interlocked 保证跨线程原子性
        private int _totalUpdates;
        private int _errorCount;
        // 最后更新时间的 Ticks 值（long），通过 Interlocked.Exchange 原子写入
        private long _lastUpdateTicks;
        // H-27 修复：使用 Interlocked.Exchange 原子操作替代 volatile bool 的 check-then-set。
        // 原因：volatile bool + if (_disposed) return; _disposed = true; 不是原子操作，
        // 两个并发 Dispose() 可能同时通过检查。Interlocked.Exchange 保证只有一个线程拿到旧值 0。
        // 与 OpcDaClient._disposedInt 和 GatewayOpcUaServer._disposedInt 保持一致。
        private int _disposedInt;

        // R-3 修复：预编译类型转换委托缓存，避免每次数据回调时 switch-case 分支预测开销。
        // 使用静态数组而非字典——BuiltInType 枚举值范围小（0~25），数组索引 O(1) 无哈希冲突。
        // 每个委托内联快速路径类型检查（is pattern），匹配时零分配返回，不匹配时调用 Convert.To*。
        private static readonly Func<object, object>[] TypeConverters;

        /// <summary>
        /// R-3 修复：静态构造器一次性填充所有类型转换委托。
        /// 线程安全保证：CLR 保证静态构造器在类型首次使用前执行且仅执行一次。
        /// 35K 标签回调场景下，避免每次回调都经过 switch-case 分支预测开销。
        /// </summary>
        static DataBridge()
        {
            TypeConverters = new Func<object, object>[32]; // BuiltInType 枚举值范围足够

            TypeConverters[(int)BuiltInType.Boolean]  = v => v is bool b ? b : Convert.ToBoolean(v);
            TypeConverters[(int)BuiltInType.SByte]    = v => v is sbyte sb ? sb : Convert.ToSByte(v);
            TypeConverters[(int)BuiltInType.Byte]     = v => v is byte by ? by : Convert.ToByte(v);
            TypeConverters[(int)BuiltInType.Int16]    = v => v is short s ? s : Convert.ToInt16(v);
            TypeConverters[(int)BuiltInType.Int32]    = v => v is int i ? i : Convert.ToInt32(v);
            TypeConverters[(int)BuiltInType.Int64]    = v => v is long l ? l : Convert.ToInt64(v);
            TypeConverters[(int)BuiltInType.UInt16]   = v => v is ushort us ? us : Convert.ToUInt16(v);
            TypeConverters[(int)BuiltInType.UInt32]   = v => v is uint ui ? ui : Convert.ToUInt32(v);
            TypeConverters[(int)BuiltInType.UInt64]   = v => v is ulong ul ? ul : Convert.ToUInt64(v);
            TypeConverters[(int)BuiltInType.Float]    = v => v is float f ? f : Convert.ToSingle(v);
            TypeConverters[(int)BuiltInType.Double]   = v => v is double d ? d : Convert.ToDouble(v);
            TypeConverters[(int)BuiltInType.String]   = v => Convert.ToString(v);
            TypeConverters[(int)BuiltInType.DateTime] = v => v is DateTime dt ? dt : Convert.ToDateTime(v);
        }

        /// <summary>获取累计成功更新次数（线程安全读取）。</summary>
        public int TotalUpdates => Volatile.Read(ref _totalUpdates);

        /// <summary>获取累计错误次数（线程安全读取）。</summary>
        public int ErrorCount => Volatile.Read(ref _errorCount);

        /// <summary>获取最后一次成功更新的时间（线程安全读取）。</summary>
        public DateTime LastUpdateTime => new DateTime(Volatile.Read(ref _lastUpdateTicks), DateTimeKind.Local);

        /// <summary>
        /// 当前值缓存，采用不可变快照（Immutable Snapshot）模式：
        /// 每次数据变化时构造一个全新的 <see cref="TagSnapshot"/> 对象替换旧对象，
        /// 而非原地修改属性。这样读取端（UI）获取到的快照始终是一致的完整状态，
        /// 无需加锁即可避免读到半更新的中间状态。
        /// </summary>
        private readonly ConcurrentDictionary<string, TagSnapshot> _snapshots
            = new ConcurrentDictionary<string, TagSnapshot>();

        /// <summary>
        /// 日志事件，由 MainForm 订阅后输出到界面日志区域。
        /// </summary>
        public event Action<string> OnLog;

        /// <summary>
        /// 初始化数据桥接器，建立标签映射和初始快照。
        /// </summary>
        /// <param name="daClient">OPC DA 客户端实例（由调用方管理生命周期）。</param>
        /// <param name="uaServer">OPC UA 网关服务器实例（由调用方管理生命周期）。</param>
        /// <param name="tags">要桥接的标签配置列表，顺序决定了 UI 显示顺序。</param>
        public DataBridge(OpcDaClient daClient, GatewayOpcUaServer uaServer, List<TagConfig> tags)
        {
            _daClient = daClient;
            _uaServer = uaServer;
            _tagMap = new ConcurrentDictionary<string, TagConfig>();
            _orderedKeys = new List<string>(tags.Count);

            foreach (var tag in tags)
            {
                _tagMap[tag.TagKey] = tag;
                _orderedKeys.Add(tag.TagKey);
                _cachedTypes[tag.TagKey] = GatewayOpcUaServer.ParseDataType(tag.DataType);
                _snapshots[tag.TagKey] = new TagSnapshot(
                    tag.ItemId, tag.DisplayName, "-", "Unknown", DateTime.MinValue);
            }
        }

        /// <summary>
        /// 启动桥接：在 OPC UA 服务器中注册所有变量节点，并订阅 DA 数据变化事件。
        /// 调用后，DA 端的任何数据变化都会自动转发到 UA 端。
        /// </summary>
        public void Start()
        {
            int tagCount = _tagMap.Count;
            Log($"正在创建 OPC UA 变量节点... (共 {tagCount} 个标签)");

            if (tagCount == 0)
            {
                Log("[警告] 标签列表为空，没有可创建的 UA 变量节点。请检查 config.json 中的 Tags 配置。");
            }

            int addedCount = 0;
            int failedCount = 0;
            var failedTags = new List<string>(); // 仅收集前 10 个失败详情，防止日志爆炸
            // N-7 修复：按 _orderedKeys 的顺序遍历，而非 _tagMap.Values（ConcurrentDictionary
            // 迭代顺序不保证与插入顺序一致），确保 UA 节点注册顺序与 DA 扫描顺序完全一致。
            foreach (string key in _orderedKeys)
            {
                if (!_tagMap.TryGetValue(key, out TagConfig tag)) continue;
                // P5 修复：直接使用构造时缓存的 BuiltInType，无需再次通过字符串查字典
                _cachedTypes.TryGetValue(tag.TagKey, out BuiltInType builtInType);

                try
                {
                    _uaServer.AddVariableNode(tag.TagKey, tag.ItemId, tag.DisplayName, builtInType, tag.UaNodeId);
                    addedCount++;
                    Log($"  UA 节点: {tag.DisplayName} ({tag.ItemId}) [TagKey={tag.TagKey}]");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    if (failedTags.Count < 10)
                        failedTags.Add($"{tag.DisplayName} ({tag.TagKey}): {ex.Message}");
                }
            }

            Log($"已创建 {addedCount}/{tagCount} 个 UA 变量节点");
            if (failedCount > 0)
            {
                Log($"[警告] {failedCount} 个节点创建失败:");
                foreach (string detail in failedTags)
                    Log($"  - {detail}");
                if (failedCount > 10)
                    Log($"  ...及其他 {failedCount - 10} 个失败");
            }

            // 诊断信息
            int actualVarCount = _uaServer.VariableCount;
            ushort nsIndex = _uaServer.NamespaceIndex;
            Log($"  命名空间索引: {nsIndex}, 实际变量数: {actualVarCount}");

            // 订阅 DA 数据变化事件
            _daClient.OnDataChanged += OnDaDataChanged;

            Log("数据桥接已启动，等待数据...");
        }

        /// <summary>
        /// 停止桥接并释放资源：取消 DA 数据变化事件订阅。
        /// <para>P2 修复：设置 <see cref="_disposed"/> 标志，确保 Dispose 后
        /// 仍在排队的线程池回调不会继续处理数据或更新 UA 节点。</para>
        /// <para>注意：此方法不释放 <see cref="_daClient"/> 和 <see cref="_uaServer"/>，
        /// 因为它们的生命周期由外部所有者（MainForm）管理。</para>
        /// </summary>
        public void Dispose()
        {
            // H-27 修复：原子 CAS 保护，确保并发 Dispose 只有一个线程执行清理
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;
            _daClient.OnDataChanged -= OnDaDataChanged;
        }

        /// <summary>
        /// OPC DA 数据变化回调，是桥接模式的核心转发逻辑。
        /// 
        /// <para>此方法由 DA 客户端在线程池线程上调用，可能多个回调并发执行。
        /// 执行流程：类型转换 → 更新 UA 变量节点 → 替换快照 → 更新统计计数器。</para>
        /// 
        /// <para>线程安全保障：
        /// - UA 服务器的 UpdateValue 方法本身线程安全；
        /// - 快照使用不可变对象替换（原子引用赋值）；
        /// - 统计计数器使用 Interlocked 原子操作。</para>
        /// </summary>
        /// <param name="tagKey">标签的唯一标识。</param>
        /// <param name="value">DA 端读取到的原始值。</param>
        /// <param name="isGood">OPC 质量码，true 表示 Good，false 表示 Bad。</param>
        /// <param name="timestamp">DA 端提供的时间戳。</param>
        private void OnDaDataChanged(string tagKey, object value, bool isGood, DateTime timestamp)
        {
            // H-27 修复：使用 Volatile.Read 原子读取，确保 Dispose 后不再处理数据
            if (Volatile.Read(ref _disposedInt) == 1) return;

            try
            {
                // 类型转换：使用构造时缓存的 BuiltInType，避免每次回调都做字符串解析
                object convertedValue = ConvertValue(tagKey, value);

                // 将转换后的值推送到 OPC UA 服务器对应的变量节点
                _uaServer.UpdateValue(tagKey, convertedValue, isGood, timestamp);

                // 用全新的不可变快照对象替换旧快照（引用赋值是原子操作，无需加锁）
                _snapshots[tagKey] = new TagSnapshot(
                    _tagMap.TryGetValue(tagKey, out var tc) ? tc.ItemId : tagKey,
                    _tagMap.TryGetValue(tagKey, out var dn) ? dn.DisplayName ?? dn.ItemId : tagKey,
                    convertedValue?.ToString() ?? "null",
                    isGood ? "Good" : "Bad",
                    timestamp);

                // 原子递增更新计数器和最后更新时间
                Interlocked.Increment(ref _totalUpdates);
                Interlocked.Exchange(ref _lastUpdateTicks, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                Log($"[错误] 更新 {tagKey}: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前所有标签的数据快照，供 UI 定时刷新显示。
        /// 
        /// <para>返回顺序与构造时传入的标签列表顺序一致（由 <see cref="_orderedKeys"/> 保证），
        /// 确保 UI 表格行与数据一一对应。</para>
        /// 
        /// <para>每个 <see cref="TagSnapshot"/> 是不可变对象，读取时无需加锁。</para>
        /// </summary>
        /// <returns>按配置顺序排列的标签快照列表。</returns>
        public IReadOnlyList<TagSnapshot> GetSnapshots()
        {
            var result = new List<TagSnapshot>(_orderedKeys.Count);
            foreach (string key in _orderedKeys)
            {
                if (_snapshots.TryGetValue(key, out var snap))
                    result.Add(snap);
            }
            return result;
        }

        /// <summary>
        /// R-3 修复：使用预编译委托缓存替代 switch-case，O(1) 数组索引无分支预测开销。
        /// R-1 修复：增加 DBNull、COM decimal、未知类型等边缘情况的兼容处理。
        /// 转换失败时返回原始值（而非抛异常），保证单个标签的类型错误
        /// 不会影响其他标签的数据转发。
        /// </summary>
        /// <param name="tagKey">标签唯一标识，用于查找缓存的目标类型。</param>
        /// <param name="value">DA 端返回的原始值。</param>
        /// <returns>转换后的值；若转换失败或目标类型未知，返回原始值。</returns>
        private object ConvertValue(string tagKey, object value)
        {
            // R-1: 处理 DBNull 和 COM 空值——直接返回 null，不再传递给后续转换
            if (value == null || value is DBNull) return null;

            if (!_cachedTypes.TryGetValue(tagKey, out BuiltInType targetType))
                return value;

            try
            {
                // R-3: 预编译委托替换 switch-case，消除分支预测开销
                int typeIndex = (int)targetType;
                if (typeIndex >= 0 && typeIndex < TypeConverters.Length)
                {
                    var converter = TypeConverters[typeIndex];
                    if (converter != null)
                        return converter(value);
                }

                // 未知 BuiltInType — 无转换器，原样返回
                return value;
            }
            catch
            {
                // 转换失败时降级返回原始值，保证数据不丢失
                return value;
            }
        }

        /// <summary>
        /// 输出带时间戳的日志消息到 <see cref="OnLog"/> 事件订阅者。
        /// </summary>
        /// <param name="message">日志内容。</param>
        private void Log(string message)
        {
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    /// <summary>
    /// 标签数据快照，采用不可变对象（Immutable Object）模式。
    /// 
    /// <para>所有属性在构造后不可更改，保证了跨线程读取的安全性：
    /// 写入线程用新实例替换整个引用（原子操作），读取线程获取到的始终是
    /// 构造完成的、内部一致的对象，无需任何同步机制。</para>
    /// 
    /// <para>用于 <see cref="DataBridge"/> 缓存最新值，供 UI 定时读取显示。</para>
    /// </summary>
    public class TagSnapshot
    {
        /// <summary>OPC DA 标签的 ItemId。</summary>
        public string ItemId { get; }

        /// <summary>标签的显示名称。</summary>
        public string DisplayName { get; }

        /// <summary>标签值的字符串表示。</summary>
        public string Value { get; }

        /// <summary>OPC 质量码的文本表示（"Good" 或 "Bad"）。</summary>
        public string Quality { get; }

        /// <summary>DA 端提供的时间戳。</summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 创建一个不可变的标签数据快照。
        /// </summary>
        /// <param name="itemId">OPC DA 标签 ItemId。</param>
        /// <param name="displayName">显示名称。</param>
        /// <param name="value">值的字符串表示。</param>
        /// <param name="quality">质量码文本。</param>
        /// <param name="timestamp">时间戳。</param>
        public TagSnapshot(string itemId, string displayName, string value, string quality, DateTime timestamp)
        {
            ItemId = itemId;
            DisplayName = displayName;
            Value = value;
            Quality = quality;
            Timestamp = timestamp;
        }
    }
}
