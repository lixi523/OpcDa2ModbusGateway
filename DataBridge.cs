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
        private readonly OpcDaClient _daClient;
        private readonly GatewayModbusTcpServer _modbusServer;

        private readonly ConcurrentDictionary<string, TagConfig> _tagMap;
        private readonly List<string> _orderedKeys;

        private readonly Dictionary<string, DataTypeId> _cachedTypes = new Dictionary<string, DataTypeId>();

        private int _totalUpdates;
        private int _errorCount;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private volatile bool _disposed;

        public int TotalUpdates => _totalUpdates;
        public int ErrorCount => _errorCount;
        public DateTime LastUpdateTime => _lastUpdateTime;

        public event Action<string> OnLog;

        public DataBridge(OpcDaClient daClient, GatewayModbusTcpServer modbusServer, List<TagConfig> tags)
        {
            _daClient = daClient ?? throw new ArgumentNullException(nameof(daClient));
            _modbusServer = modbusServer ?? throw new ArgumentNullException(nameof(modbusServer));

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
            if (_disposed) return;

            // Register all tags as Modbus registers
            foreach (var tag in _tagMap.Values)
            {
                if (tag.TagKey == null) continue;
                ushort addr = tag.ModbusAddress;
                var regType = tag.GetEffectiveRegisterType();
                object defaultValue = DataTypeConverter.GetDefaultValue(DataTypeConverter.ParseDataType(tag.DataType));
                _modbusServer.AddVariableNode(tag.TagKey, addr, regType, defaultValue);
            }

            _daClient.OnDataChanged += OnDaDataChanged;

            int count = _tagMap.Count;
            OnLog?.Invoke($"[Bridge] Modbus bridge started: {count} tags");
        }

        private void OnDaDataChanged(string tagKey, object value, bool isGood, DateTime timestamp)
        {
            if (_disposed) return;

            if (!_tagMap.TryGetValue(tagKey, out var tag))
                return;

            object convertedValue = value;
            if (isGood && value != null && _cachedTypes.TryGetValue(tagKey, out var targetType))
            {
                convertedValue = DataTypeConverter.ConvertValue(value, targetType);
            }

            _modbusServer.UpdateValue(tagKey, convertedValue, isGood, timestamp);

            Interlocked.Increment(ref _totalUpdates);
            _lastUpdateTime = DateTime.Now;
        }

        public IReadOnlyList<TagSnapshot> GetSnapshots()
        {
            var snapshots = new List<TagSnapshot>(_orderedKeys.Count);
            for (int i = 0; i < _orderedKeys.Count; i++)
            {
                string key = _orderedKeys[i];
                if (!_tagMap.TryGetValue(key, out var tag)) continue;

                snapshots.Add(new TagSnapshot
                {
                    TagKey = key,
                    ItemId = tag.ItemId,
                    DisplayName = tag.DisplayName ?? tag.ItemId,
                    DataType = tag.DataType,
                    ModbusAddress = tag.ModbusAddress,
                    ModbusRegisterType = tag.ModbusRegisterType ?? "HoldingRegister",
                });
            }

            return snapshots;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_daClient != null)
                _daClient.OnDataChanged -= OnDaDataChanged;
        }
    }
}