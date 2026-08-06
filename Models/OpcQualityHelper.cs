namespace OpcDaToModbusGateway.Models
{
    public enum OpcQualityKind
    {
        Bad,
        Uncertain,
        Good
    }

    public static class OpcQualityHelper
    {
        public static OpcQualityKind Classify(int status)
        {
            switch (status & 0xC0)
            {
                case 0xC0: return OpcQualityKind.Good;
                case 0x40: return OpcQualityKind.Uncertain;
                default: return OpcQualityKind.Bad;
            }
        }
    }
}
