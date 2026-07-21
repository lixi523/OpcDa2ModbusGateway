using System;

namespace OpcDaToModbusGateway.Models
{
    /// <summary>
    /// 运行状态快照数据结构（序列化到 health_*.json）。
    /// </summary>
    /// <summary>Tag value snapshot for UI display.</summary>
public class TagSnapshot
{
    public string TagKey { get; set; }
    public string ItemId { get; set; }
    public string DisplayName { get; set; }
    public string DataType { get; set; }
    public ushort ModbusAddress { get; set; }
    public string ModbusRegisterType { get; set; }
    public object Value { get; set; }
    public string Quality { get; set; }
    public string Timestamp { get; set; }
}

public class SnapshotData
    {
        public string Timestamp { get; set; }
        public string TimestampUtc { get; set; }

        // 内存
        public double WorkingSetMB { get; set; }
        public double PrivateMemoryMB { get; set; }
        public double GcTotalMemoryMB { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }

        // 网关
        public bool IsRunning { get; set; }
        public bool DaConnected { get; set; }
        public int TotalUpdates { get; set; }
        public double UpdateRatePerSec { get; set; }
        public int ErrorCount { get; set; }
        public string LastUpdateTime { get; set; }
        public int ModbusVariableCount { get; set; }
        public int ModbusSlaveId { get; set; }
        
        // 线程池
        public int ThreadPoolWorkerBusy { get; set; }
        public int ThreadPoolWorkerMax { get; set; }
    }
}
