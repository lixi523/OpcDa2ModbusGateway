using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;
using NModbus.Device;
using NModbus.Data;
using OpcDaToModbusGateway.Models;
using OpcDaToModbusGateway.Services.Interfaces;

namespace OpcDaToModbusGateway
{
    public enum ModbusRegisterType
    {
        Coil,
        DiscreteInput,
        HoldingRegister,
        InputRegister
    }

    public class ModbusTagMapping
    {
        public string TagKey { get; set; }
        public ushort Address { get; set; }
        public ModbusRegisterType RegisterType { get; set; }
        public string ModbusDataType { get; set; }
        public object LastValue { get; set; }
        public DateTime LastTimestamp { get; set; }
    }

    public class GatewayModbusTcpServer : IGatewayModbusTcpServer
    {
        private readonly ModbusTcpConfig _config;
        private IModbusTcpSlaveNetwork _network;
        private DefaultSlaveDataStore _dataStore;
        private readonly Dictionary<string, ModbusTagMapping> _tagMap = new Dictionary<string, ModbusTagMapping>();
        private readonly object _lock = new object();
        private volatile bool _isRunning;
        private int _disposedInt;
        private CancellationTokenSource _cts;

        public event Action<string> OnStatusChanged;
        public Action OnConfigChanged { get; set; }
        public bool IsRunning => _isRunning;
        public int VariableCount => _tagMap.Count;
        public byte SlaveId => _config?.SlaveId ?? (byte)1;

        public GatewayModbusTcpServer(ModbusTcpConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task StartAsync()
        {
            if (_isRunning) return;

            TcpListener listener = null;
            try
            {
                byte slaveId = _config.SlaveId > 0 ? _config.SlaveId : (byte)1;
                string listenAddress = string.IsNullOrEmpty(_config.ListenAddress) ? "0.0.0.0" : _config.ListenAddress;
                int port = _config.Port > 0 ? _config.Port : 502;

                // 先尝试释放可能残留的 TIME_WAIT 占用 — 使用 TcpListener 显式指定 ExclusiveAddressUse=false
                // 502 端口是知名端口，旧进程释放后可能仍在 TIME_WAIT 状态，没有 SO_REUSEADDR 会绑定失败
                listener = new TcpListener(IPAddress.Parse(listenAddress), port)
                {
                    ExclusiveAddressUse = false
                };
                listener.Start();
                var factory = new ModbusFactory();
                _dataStore = new DefaultSlaveDataStore();
                var slave = factory.CreateSlave(slaveId, _dataStore);
                _network = factory.CreateSlaveNetwork(listener);
                listener = null; // ownership transferred to the slave network
                _network.AddSlave(slave);

                _cts = new CancellationTokenSource();
                // NModbus 3.x 的 ListenAsync 内部启动后台任务接收客户端，无需 await；
                // 此处不 await 避免阻塞 StartAsync，异常会被 ListenAsync 内部吞掉
                _ = _network.ListenAsync(_cts.Token);
                _isRunning = true;

                OnStatusChanged?.Invoke($"Modbus TCP server started on {listenAddress}:{port}, slaveId={slaveId}");
            }
            catch (Exception ex)
            {
                listener?.Stop();
                _cts?.Dispose();
                _cts = null;
                _network?.Dispose();
                _network = null;
                _dataStore = null;
                _isRunning = false;
                OnStatusChanged?.Invoke($"Failed to start Modbus TCP server: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public Task StopAsync()
        {
            if (!_isRunning) return Task.CompletedTask;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _network?.Dispose();
                _network = null;
                _isRunning = false;
                OnStatusChanged?.Invoke("Modbus TCP server stopped.");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Error stopping Modbus TCP server: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public void AddVariableNode(string tagKey, ushort modbusAddress, ModbusRegisterType registerType, string modbusDataType, object initialValue)
        {
            lock (_lock)
            {
                _tagMap[tagKey] = new ModbusTagMapping
                {
                    TagKey = tagKey,
                    Address = modbusAddress,
                    RegisterType = registerType,
                    ModbusDataType = DataTypeConverter.NormalizeModbusDataType(modbusDataType),
                    LastValue = initialValue,
                    LastTimestamp = DateTime.UtcNow
                };
            }
        }

        public ModbusWriteResult UpdateValue(string tagKey, object value, bool isGood, DateTime timestamp)
        {
            if (!isGood) return ModbusWriteResult.Failed(ModbusWriteStatus.BadQuality);
            lock (_lock)
            {
                if (!_isRunning || _dataStore == null)
                    return ModbusWriteResult.Failed(ModbusWriteStatus.NotRunning);
                if (!_tagMap.TryGetValue(tagKey, out var mapping))
                    return ModbusWriteResult.Failed(ModbusWriteStatus.NotMapped);

                try
                {
                    WriteToRegister(mapping.Address, mapping.RegisterType, mapping.ModbusDataType, value);
                    mapping.LastValue = value;
                    mapping.LastTimestamp = timestamp;
                    return ModbusWriteResult.Succeeded();
                }
                catch (FormatException ex)
                {
                    return ModbusWriteResult.Failed(ModbusWriteStatus.EncodingError, ex.Message);
                }
                catch (OverflowException ex)
                {
                    return ModbusWriteResult.Failed(ModbusWriteStatus.EncodingError, ex.Message);
                }
                catch (InvalidCastException ex)
                {
                    return ModbusWriteResult.Failed(ModbusWriteStatus.EncodingError, ex.Message);
                }
                catch (NotSupportedException ex)
                {
                    return ModbusWriteResult.Failed(ModbusWriteStatus.EncodingError, ex.Message);
                }
                catch (Exception ex)
                {
                    return ModbusWriteResult.Failed(ModbusWriteStatus.WriteError, ex.Message);
                }
            }
        }

        private void WriteToRegister(ushort address, ModbusRegisterType registerType, string modbusDataType, object value)
        {
            switch (registerType)
            {
                case ModbusRegisterType.Coil:
                    _dataStore.CoilDiscretes.WritePoints(address, new[] { ConvertToBool(value) });
                    break;
                case ModbusRegisterType.DiscreteInput:
                    _dataStore.CoilInputs.WritePoints(address, new[] { ConvertToBool(value) });
                    break;
                case ModbusRegisterType.HoldingRegister:
                    _dataStore.HoldingRegisters.WritePoints(address, DataTypeConverter.EncodeModbusRegisters(value, modbusDataType));
                    break;
                case ModbusRegisterType.InputRegister:
                    _dataStore.InputRegisters.WritePoints(address, DataTypeConverter.EncodeModbusRegisters(value, modbusDataType));
                    break;
            }
        }

        private static bool ConvertToBool(object value)
        {
            if (value is bool b) return b;
            return Convert.ToBoolean(value);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) != 0) return;
            StopAsync().GetAwaiter().GetResult();
            lock (_lock)
            {
                _tagMap.Clear();
                _dataStore = null;
            }
        }
        public static string ComputeModbusPath(string itemId, string displayName)
        {
            return string.IsNullOrEmpty(displayName) ? itemId : displayName;
        }
    }
}