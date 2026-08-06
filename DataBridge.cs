using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpcDaToModbusGateway.Models;
using OpcDaToModbusGateway.Services.Interfaces;

namespace OpcDaToModbusGateway
{
    /// <summary>
    /// OPC DA to Modbus TCP data bridge.
    /// Forwards OPC DA data change events to Modbus TCP registers in real-time.
    /// </summary>
    public class DataBridge : IDataBridge
    {
        private readonly IOpcDaClient _daClient; // 可能为 null（DA 未连接场景）
        private readonly IGatewayModbusTcpServer _modbusServer;

        private readonly ConcurrentDictionary<string, TagConfig> _tagMap;
        private readonly List<string> _orderedKeys;
        private readonly ConcurrentDictionary<string, TagSnapshot> _valueCache =
            new ConcurrentDictionary<string, TagSnapshot>();

        private readonly ConcurrentDictionary<string, DataTypeId> _cachedTypes =
            new ConcurrentDictionary<string, DataTypeId>();

        // 已提示过"类型无法解析"的标签，避免每次注册尝试都重复告警。
        private readonly HashSet<string> _warnedPending = new HashSet<string>();

        private int _totalUpdates;
        private int _errorCount;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private volatile bool _disposed;
        private int _started;

        public int TotalUpdates => _totalUpdates;
        public int ErrorCount => _errorCount;
        public DateTime LastUpdateTime => _lastUpdateTime;

        public event Action<string> OnLog;

        public DataBridge(IOpcDaClient daClient, IGatewayModbusTcpServer modbusServer, List<TagConfig> tags)
        {
            _daClient = daClient ?? throw new ArgumentNullException(nameof(daClient));
            _modbusServer = modbusServer ?? throw new ArgumentNullException(nameof(modbusServer));
            if (tags == null) throw new ArgumentNullException(nameof(tags));

            _tagMap = new ConcurrentDictionary<string, TagConfig>();
            _orderedKeys = new List<string>(tags.Count);

            foreach (var tag in tags)
            {
                if (tag.TagKey == null) continue;
                _tagMap[tag.TagKey] = tag;
                _orderedKeys.Add(tag.TagKey);
                _cachedTypes[tag.TagKey] = DataTypeConverter.ParseDataType(tag.DataType);
            }
        }

        public void Start()
        {
            if (_disposed || Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

            // 注册所有标签为 Modbus 寄存器。
            // 类型无法解析（Variant/Object/空等）的标签暂时跳过，待 DA 连接回写真实类型后由
            // OnClientConfigChanged 补注册——这样 DA 未连接时 Modbus 服务仍能正常启动（降级运行）。
            RegisterModbusNodes();

            _daClient.OnDataChanged += OnDaDataChanged;
            _daClient.OnConfigChanged += OnClientConfigChanged;

            int count = _tagMap.Count;
            OnLog?.Invoke($"[Bridge] Modbus bridge started: {count} tags");
        }

        private void RegisterModbusNodes()
        {
            foreach (var tag in _tagMap.Values)
            {
                if (tag.TagKey == null) continue;

                if (!tag.TryGetEffectiveModbusDataType(out string modbusType))
                {
                    if (_warnedPending.Add(tag.TagKey))
                        OnLog?.Invoke($"[Bridge] 标签 [{tag.TagKey}] 的数据类型无法解析，等待 DA 连接后自动注册");
                    continue;
                }

                _warnedPending.Remove(tag.TagKey);
                ushort addr = tag.ModbusAddress;
                var regType = tag.GetEffectiveRegisterType();
                _cachedTypes[tag.TagKey] = DataTypeConverter.ParseDataType(tag.DataType);
                object defaultValue = DataTypeConverter.GetDefaultValue(DataTypeConverter.ParseDataType(tag.DataType));
                _modbusServer.AddVariableNode(tag.TagKey, addr, regType, modbusType, defaultValue);
            }
        }

        private void OnClientConfigChanged()
        {
            // DA 连接/重连后 CanonicalDataType 回写真实类型：
            // 补注册之前无法解析的标签，并刷新类型转换目标，保证重连后数据按真实类型编码。
            if (_disposed) return;
            RegisterModbusNodes();
        }

        private void OnDaDataChanged(string tagKey, object value, OpcQualityKind quality, DateTime timestamp)
        {
            if (_disposed || !_tagMap.TryGetValue(tagKey, out var tag)) return;

            string timestampText = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            TagSnapshot previous = _valueCache.TryGetValue(tagKey, out var cached) ? cached : CreateSnapshot(tag);

            if (quality != OpcQualityKind.Good || value == null)
            {
                string daQuality = quality == OpcQualityKind.Good ? "Bad" : quality.ToString();
                _valueCache[tagKey] = CopySnapshot(previous, value, daQuality, timestampText,
                    previous.ModbusValue, "BadQuality", previous.ModbusLastSuccessTimestamp);
                return;
            }

            object convertedValue;
            try
            {
                convertedValue = _cachedTypes.TryGetValue(tagKey, out var targetType)
                    ? DataTypeConverter.ConvertValue(value, targetType)
                    : value;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                OnLog?.Invoke($"[Bridge] 类型转换失败 [{tagKey}]: {ex.Message}");
                _valueCache[tagKey] = CopySnapshot(previous, value, "Good", timestampText,
                    previous.ModbusValue, "ConversionError", previous.ModbusLastSuccessTimestamp);
                return;
            }

            ModbusWriteResult result;
            try
            {
                result = _modbusServer.UpdateValue(tagKey, convertedValue, true, timestamp);
            }
            catch (Exception ex)
            {
                result = ModbusWriteResult.Failed(ModbusWriteStatus.WriteError, ex.Message);
            }

            if (result.IsSuccess)
            {
                _valueCache[tagKey] = CopySnapshot(previous, convertedValue, "Good", timestampText,
                    convertedValue, "Good", timestampText);
                Interlocked.Increment(ref _totalUpdates);
                _lastUpdateTime = DateTime.Now;
                return;
            }

            if (result.Status != ModbusWriteStatus.BadQuality)
            {
                Interlocked.Increment(ref _errorCount);
                OnLog?.Invoke($"[Bridge] Modbus 更新失败 [{tagKey}] ({result.Status}): {result.ErrorMessage}");
            }
            _valueCache[tagKey] = CopySnapshot(previous, convertedValue, "Good", timestampText,
                previous.ModbusValue, "WriteError", previous.ModbusLastSuccessTimestamp);
        }

        private static TagSnapshot CreateSnapshot(TagConfig tag)
        {
            return new TagSnapshot
            {
                TagKey = tag.TagKey,
                ItemId = tag.ItemId,
                DisplayName = tag.DisplayName ?? tag.ItemId,
                DataType = tag.DataType,
                ModbusAddress = tag.ModbusAddress,
                ModbusRegisterType = tag.ModbusRegisterType ?? "HoldingRegister",
                DaValue = "-",
                DaQuality = "Waiting",
                DaTimestamp = "-",
                ModbusValue = "-",
                ModbusStatus = "Waiting",
                ModbusLastSuccessTimestamp = "-"
            };
        }

        private static TagSnapshot CopySnapshot(TagSnapshot source, object daValue, string daQuality,
            string daTimestamp, object modbusValue, string modbusStatus, string modbusTimestamp)
        {
            return new TagSnapshot
            {
                TagKey = source.TagKey,
                ItemId = source.ItemId,
                DisplayName = source.DisplayName,
                DataType = source.DataType,
                ModbusAddress = source.ModbusAddress,
                ModbusRegisterType = source.ModbusRegisterType,
                DaValue = daValue,
                DaQuality = daQuality,
                DaTimestamp = daTimestamp,
                ModbusValue = modbusValue,
                ModbusStatus = modbusStatus,
                ModbusLastSuccessTimestamp = modbusTimestamp
            };
        }

        public IReadOnlyList<TagSnapshot> GetSnapshots()
        {
            var snapshots = new List<TagSnapshot>(_orderedKeys.Count);
            for (int i = 0; i < _orderedKeys.Count; i++)
            {
                string key = _orderedKeys[i];
                if (!_tagMap.TryGetValue(key, out var tag)) continue;

                // 优先使用值缓存（包含最新的 Value/Quality/Timestamp）
                if (_valueCache.TryGetValue(key, out var cached))
                {
                    snapshots.Add(cached);
                }
                else
                {
                    // 缓存未命中（尚未收到首次数据）— 返回空快照
                    snapshots.Add(CreateSnapshot(tag));
                }
            }

            return snapshots;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Volatile.Read(ref _started) != 0)
            {
                _daClient.OnDataChanged -= OnDaDataChanged;
                _daClient.OnConfigChanged -= OnClientConfigChanged;
            }
        }
    }
}