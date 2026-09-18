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

        // #3 修复：Start/Stop/Dispose 并发保护。
        // 状态机（Stopped/Starting/Running）替代裸 check-then-act 的 if (_isRunning) return;，
        // 消除并发调用时 TcpListener/_cts 泄漏或重复 Dispose 的竞态。
        // 锁内只做状态判断与状态变更，网络 I/O（listener.Start/ListenAsync）在锁外执行。
        private enum ServerState { Stopped, Starting, Running }
        private readonly object _startStopLock = new object();
        private ServerState _state = ServerState.Stopped;
        private volatile bool _isRunning;
        private int _disposedInt;
        private CancellationTokenSource _cts;

        public event Action<string> OnStatusChanged;
        public Action OnConfigChanged { get; set; }
        public bool IsRunning => _isRunning;
        public int VariableCount
        {
            get
            {
                lock (_lock)
                {
                    return _tagMap.Count;
                }
            }
        }
        public byte SlaveId => _config?.SlaveId ?? (byte)1;

        public GatewayModbusTcpServer(ModbusTcpConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public Task StartAsync()
        {
            // #3：锁内原子地做状态判断与 Starting 置位，防止并发 Start 双启。
            lock (_startStopLock)
            {
                if (_state != ServerState.Stopped)
                    return Task.CompletedTask; // 已运行或正在启动，幂等返回
                _state = ServerState.Starting;
            }

            TcpListener listener = null;
            try
            {
                byte slaveId = _config.SlaveId > 0 ? _config.SlaveId : (byte)1;
                string listenAddress = string.IsNullOrEmpty(_config.ListenAddress) ? "127.0.0.1" : _config.ListenAddress;
                int port = _config.Port > 0 ? _config.Port : 502;

                // 检测监听地址是否为 0.0.0.0（全网卡暴露），UI 需警告
                if (string.IsNullOrEmpty(listenAddress) || listenAddress == "0.0.0.0" || listenAddress == ":0")
                {
                    OnStatusChanged?.Invoke("⚠️ 警告: 监听地址为 0.0.0.0，Modbus TCP 服务暴露到所有网卡，请确认网络安全隔离！");
                }

                // 监听 0.0.0.0 且配置了 IP 白名单时，提示用户白名单仅日志提示，实际拦截需配置防火墙
                string allowedIps = _config.AllowedIps;
                if (!string.IsNullOrEmpty(allowedIps) && (listenAddress == "0.0.0.0" || listenAddress == ":0"))
                {
                    OnStatusChanged?.Invoke($"⚠️ 已配置 IP 白名单: {allowedIps}，但监听 0.0.0.0 时白名单仅日志提示，实际拦截需配置防火墙");
                }

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
                // #9 修复：用监控 Task 包裹 ListenAsync，监听循环异常退出时（端口被夺/句柄耗尽）
                // 置 _isRunning=false 并触发 OnStatusChanged，消除"假运行"（状态与 UI 误判健康）。
                // NModbus 3.x 的 ListenAsync 内部启动后台任务接收客户端，无需 await；
                // 此处只捕获故障结果，不阻塞 StartAsync。
                var listenTask = _network.ListenAsync(_cts.Token);
                _ = listenTask.ContinueWith(t =>
                {
                    if (t.IsCanceled)
                        return; // 正常停止（StopAsync 触发 cancel），非故障
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.GetBaseException() ?? t.Exception;
                        _isRunning = false;
                        lock (_startStopLock)
                        {
                            _state = ServerState.Stopped;
                        }
                        OnStatusChanged?.Invoke($"Modbus TCP listener failed: {ex?.Message}. Service stopped.");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.OnlyOnCanceled);

                // 锁内原子置 Running，后续 Stop/Dispose 才能观察到
                lock (_startStopLock)
                {
                    _state = ServerState.Running;
                }
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
                // 失败回退到 Stopped，允许调用方修复问题后重试
                lock (_startStopLock)
                {
                    _state = ServerState.Stopped;
                }
                OnStatusChanged?.Invoke($"Failed to start Modbus TCP server: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            // #3：锁内原子地判断状态并置 Stopped，防止与并发 Start 交错（check-then-act 竞态）。
            bool shouldStop;
            lock (_startStopLock)
            {
                shouldStop = _state == ServerState.Running;
                if (shouldStop)
                    _state = ServerState.Stopped;
            }
            if (!shouldStop) return Task.CompletedTask;

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

            // #8 修复：锁内只做 _tagMap 查找与状态判断，编码（WriteToRegister）移到锁外，
            // 避免高频 OPC 更新被全串行化、阻塞 UI 查询。_dataStore 写依赖 NModbus 内部锁。
            ModbusTagMapping mapping;
            DefaultSlaveDataStore dataStore;
            lock (_lock)
            {
                if (!_isRunning || _dataStore == null)
                    return ModbusWriteResult.Failed(ModbusWriteStatus.NotRunning);
                if (!_tagMap.TryGetValue(tagKey, out mapping))
                    return ModbusWriteResult.Failed(ModbusWriteStatus.NotMapped);
                dataStore = _dataStore;
            }

            try
            {
                WriteToRegister(dataStore, mapping.Address, mapping.RegisterType, mapping.ModbusDataType, value);
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

        private static void WriteToRegister(DefaultSlaveDataStore dataStore, ushort address, ModbusRegisterType registerType, string modbusDataType, object value)
        {
            switch (registerType)
            {
                case ModbusRegisterType.Coil:
                    dataStore.CoilDiscretes.WritePoints(address, new[] { ConvertToBool(value) });
                    break;
                case ModbusRegisterType.DiscreteInput:
                    dataStore.CoilInputs.WritePoints(address, new[] { ConvertToBool(value) });
                    break;
                case ModbusRegisterType.HoldingRegister:
                    dataStore.HoldingRegisters.WritePoints(address, DataTypeConverter.EncodeModbusRegisters(value, modbusDataType));
                    break;
                case ModbusRegisterType.InputRegister:
                    dataStore.InputRegisters.WritePoints(address, DataTypeConverter.EncodeModbusRegisters(value, modbusDataType));
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
            // #3：Dispose 后显式置 Stopped，阻止后续 Start 重启已释放实例
            lock (_startStopLock)
            {
                _state = ServerState.Stopped;
            }
        }
        public static string ComputeModbusPath(string itemId, string displayName)
        {
            return string.IsNullOrEmpty(displayName) ? itemId : displayName;
        }
    }
}