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
        public object LastValue { get; set; }
        public DateTime LastTimestamp { get; set; }
    }

    public class GatewayModbusTcpServer : IGatewayModbusTcpServer
    {
        private readonly ModbusTcpConfig _config;
        private IModbusTcpSlaveNetwork _network;
        private DefaultSlaveDataStore _dataStore;
        private readonly Dictionary<string, ModbusTagMapping> _tagMap = new Dictionary<string, ModbusTagMapping>();
        private volatile bool _isRunning;
        private int _disposedInt;

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

            try
            {
                byte slaveId = _config.SlaveId > 0 ? _config.SlaveId : (byte)1;
                string listenAddress = string.IsNullOrEmpty(_config.ListenAddress) ? "0.0.0.0" : _config.ListenAddress;
                int port = _config.Port > 0 ? _config.Port : 502;

                var factory = new ModbusFactory();
                _dataStore = new DefaultSlaveDataStore();
                var slave = factory.CreateSlave(slaveId, _dataStore);
                var listener = new TcpListener(IPAddress.Parse(listenAddress), port);
                _network = (IModbusTcpSlaveNetwork)factory.CreateSlaveNetwork(listener);
                _network.AddSlave(slave);
                _network.ListenAsync();
                _isRunning = true;

                OnStatusChanged?.Invoke($"Modbus TCP server started on {listenAddress}:{port}, slaveId={slaveId}");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Failed to start Modbus TCP server: {ex.Message}");
                throw;
            }
        }

        public Task StopAsync()
        {
            if (!_isRunning) return Task.CompletedTask;
            try
            {
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

        public void AddVariableNode(string tagKey, ushort modbusAddress, ModbusRegisterType registerType, object initialValue)
        {
            _tagMap[tagKey] = new ModbusTagMapping
            {
                TagKey = tagKey,
                Address = modbusAddress,
                RegisterType = registerType,
                LastValue = initialValue,
                LastTimestamp = DateTime.UtcNow
            };
        }

        public void UpdateValue(string tagKey, object value, bool isGood, DateTime timestamp)
        {
            if (!isGood || _dataStore == null) return;
            if (!_tagMap.TryGetValue(tagKey, out var mapping)) return;

            try
            {
                WriteToRegister(mapping.Address, mapping.RegisterType, value);
                mapping.LastValue = value;
                mapping.LastTimestamp = timestamp;
            }
            catch { }
        }

        private void WriteToRegister(ushort address, ModbusRegisterType registerType, object value)
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
                    _dataStore.HoldingRegisters.WritePoints(address, ConvertToRegisters(value));
                    break;
                case ModbusRegisterType.InputRegister:
                    _dataStore.InputRegisters.WritePoints(address, ConvertToRegisters(value));
                    break;
            }
        }

        private static bool ConvertToBool(object value)
        {
            if (value is bool b) return b;
            try { return Convert.ToBoolean(value); }
            catch { return false; }
        }

        private static ushort[] ConvertToRegisters(object value)
        {
            if (value is ushort[] us) return us;
            if (value is short s) return new ushort[] { (ushort)s, 0 };
            if (value is int i) return new ushort[] { (ushort)(i & 0xFFFF), (ushort)((i >> 16) & 0xFFFF) };
            if (value is uint ui) return new ushort[] { (ushort)(ui & 0xFFFF), (ushort)((ui >> 16) & 0xFFFF) };
            if (value is float f)
            {
                var bytes = BitConverter.GetBytes(f);
                return new ushort[] { BitConverter.ToUInt16(bytes, 0), BitConverter.ToUInt16(bytes, 2) };
            }
            if (value is double d)
            {
                var bytes = BitConverter.GetBytes(d);
                return new ushort[] { BitConverter.ToUInt16(bytes, 0), BitConverter.ToUInt16(bytes, 2),
                                      BitConverter.ToUInt16(bytes, 4), BitConverter.ToUInt16(bytes, 6) };
            }
            try { return new ushort[] { Convert.ToUInt16(value), 0 }; }
            catch { return new ushort[] { 0, 0 }; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) != 0) return;
            StopAsync().GetAwaiter().GetResult();
            _tagMap.Clear();
        }
        public static string ComputeModbusPath(string itemId, string displayName)
        {
            return string.IsNullOrEmpty(displayName) ? itemId : displayName;
        }
    }
}