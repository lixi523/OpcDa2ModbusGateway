namespace OpcDaToModbusGateway.Models
{
    public enum ModbusWriteStatus
    {
        Success,
        BadQuality,
        NotRunning,
        NotMapped,
        EncodingError,
        WriteError
    }

    public sealed class ModbusWriteResult
    {
        public ModbusWriteStatus Status { get; }
        public string ErrorMessage { get; }
        public bool IsSuccess => Status == ModbusWriteStatus.Success;

        private ModbusWriteResult(ModbusWriteStatus status, string errorMessage = null)
        {
            Status = status;
            ErrorMessage = errorMessage;
        }

        public static ModbusWriteResult Succeeded() => new ModbusWriteResult(ModbusWriteStatus.Success);
        public static ModbusWriteResult Failed(ModbusWriteStatus status, string errorMessage = null)
            => new ModbusWriteResult(status, errorMessage);
    }
}
