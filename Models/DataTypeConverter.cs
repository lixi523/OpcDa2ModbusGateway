using System;

namespace OpcDaToModbusGateway.Models
{
    /// <summary>
    /// Data type identifier enum for DA value conversion.
    /// Replaces the OPC UA BuiltInType dependency.
    /// </summary>
    public enum DataTypeId
    {
        Boolean,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float,
        Double,
        String,
        DateTime
    }

    /// <summary>
    /// Data type converter — maps string names to DataTypeId and provides default values.
    /// Thread-safe: all dictionaries are populated once in the static constructor.
    /// </summary>
    public static class DataTypeConverter
    {
        /// <summary>String name to DataTypeId mapping (case-insensitive).</summary>
        private static readonly System.Collections.Generic.Dictionary<string, DataTypeId> NameToType;

        /// <summary>DataTypeId to default value mapping.</summary>
        private static readonly System.Collections.Generic.Dictionary<DataTypeId, object> DefaultValues;

        static DataTypeConverter()
        {
            NameToType = new System.Collections.Generic.Dictionary<string, DataTypeId>(StringComparer.OrdinalIgnoreCase)
            {
                ["boolean"] = DataTypeId.Boolean,
                ["bool"] = DataTypeId.Boolean,
                ["sbyte"] = DataTypeId.SByte,
                ["byte"] = DataTypeId.Byte,
                ["int16"] = DataTypeId.Int16,
                ["short"] = DataTypeId.Int16,
                ["uint16"] = DataTypeId.UInt16,
                ["ushort"] = DataTypeId.UInt16,
                ["int32"] = DataTypeId.Int32,
                ["int"] = DataTypeId.Int32,
                ["uint32"] = DataTypeId.UInt32,
                ["uint"] = DataTypeId.UInt32,
                ["float"] = DataTypeId.Float,
                ["real4"] = DataTypeId.Float,
                ["double"] = DataTypeId.Double,
                ["real8"] = DataTypeId.Double,
                ["string"] = DataTypeId.String,
                ["datetime"] = DataTypeId.DateTime,
            };

            DefaultValues = new System.Collections.Generic.Dictionary<DataTypeId, object>
            {
                [DataTypeId.Boolean] = false,
                [DataTypeId.SByte] = (sbyte)0,
                [DataTypeId.Byte] = (byte)0,
                [DataTypeId.Int16] = (short)0,
                [DataTypeId.UInt16] = (ushort)0,
                [DataTypeId.Int32] = 0,
                [DataTypeId.UInt32] = 0u,
                [DataTypeId.Float] = 0.0f,
                [DataTypeId.Double] = 0.0,
                [DataTypeId.String] = "",
                [DataTypeId.DateTime] = DateTime.MinValue,
            };
        }

        /// <summary>
        /// Parse a string to DataTypeId.
        /// </summary>
        public static DataTypeId ParseDataType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return DataTypeId.Int32;
            // Strip "System." prefix if present (e.g., "System.Int32" -> "Int32")
            string normalized = typeName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                ? typeName.Substring(7)
                : typeName;
            return NameToType.TryGetValue(normalized, out var result) ? result : DataTypeId.Int32;
        }

        /// <summary>
        /// Get the default value for a given DataTypeId.
        /// </summary>
        public static object GetDefaultValue(DataTypeId typeId)
        {
            return DefaultValues.TryGetValue(typeId, out var value) ? value : 0;
        }

        /// <summary>
        /// Convert a value to the target type. Returns null if conversion fails.
        /// </summary>
        public static object ConvertValue(object value, DataTypeId targetType)
        {
            if (value == null) return GetDefaultValue(targetType);
            try
            {
                switch (targetType)
                {
                    case DataTypeId.Boolean: return System.Convert.ToBoolean(value);
                    case DataTypeId.SByte: return System.Convert.ToSByte(value);
                    case DataTypeId.Byte: return System.Convert.ToByte(value);
                    case DataTypeId.Int16: return System.Convert.ToInt16(value);
                    case DataTypeId.UInt16: return System.Convert.ToUInt16(value);
                    case DataTypeId.Int32: return System.Convert.ToInt32(value);
                    case DataTypeId.UInt32: return System.Convert.ToUInt32(value);
                    case DataTypeId.Float: return System.Convert.ToSingle(value);
                    case DataTypeId.Double: return System.Convert.ToDouble(value);
                    case DataTypeId.String: return value?.ToString() ?? "";
                    case DataTypeId.DateTime: return System.Convert.ToDateTime(value);
                    default: return value;
                }
            }
            catch
            {
                return GetDefaultValue(targetType);
            }
        }
    }
}
