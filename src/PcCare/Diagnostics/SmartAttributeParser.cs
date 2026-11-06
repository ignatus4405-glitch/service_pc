using System;
using System.Collections.Generic;
using System.Globalization;
using PcCare.Models;

namespace PcCare.Diagnostics
{
    /// <summary>
    /// Parses ATA SMART rows returned by the Windows WMI storage provider. Attribute IDs,
    /// names and raw-value encodings remain vendor-specific; parsing never implies health.
    /// </summary>
    public static class SmartAttributeParser
    {
        public static Dictionary<int, int> ParseThresholds(byte[] data)
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            if (data == null)
                return result;

            for (int offset = 2; offset + 1 < data.Length && offset < 362; offset += 12)
            {
                int id = data[offset];
                int threshold = data[offset + 1];
                if (id != 0 && threshold != 0)
                    result[id] = threshold;
            }
            return result;
        }

        public static List<SmartAttribute> ParseAttributes(byte[] data, IDictionary<int, int> thresholds)
        {
            List<SmartAttribute> result = new List<SmartAttribute>();
            if (data == null)
                return result;

            for (int offset = 2; offset + 11 < data.Length && offset < 362; offset += 12)
            {
                int id = data[offset];
                if (id == 0)
                    continue;

                ulong raw = 0;
                for (int rawByte = 0; rawByte < 6; rawByte++)
                    raw |= (ulong)data[offset + 5 + rawByte] << (rawByte * 8);

                int threshold;
                int? thresholdValue = thresholds != null && thresholds.TryGetValue(id, out threshold)
                    ? (int?)threshold : null;
                result.Add(new SmartAttribute
                {
                    Id = id,
                    Name = GetName(id),
                    Current = data[offset + 3],
                    Worst = data[offset + 4],
                    Threshold = thresholdValue,
                    RawValue = raw,
                    RawHex = raw.ToString("X12", CultureInfo.InvariantCulture)
                });
            }
            return result;
        }

        public static int? ParseAtaTemperature(IEnumerable<SmartAttribute> attributes)
        {
            if (attributes == null)
                return null;

            foreach (SmartAttribute attribute in attributes)
            {
                if (attribute == null || (attribute.Id != 194 && attribute.Id != 190))
                    continue;

                int temperature = (int)(attribute.RawValue & 0xffUL);
                if (temperature >= 0 && temperature <= 100)
                    return temperature;
            }
            return null;
        }

        public static string GetName(int id)
        {
            switch (id)
            {
                case 1: return "Read Error Rate (vendor-specific)";
                case 3: return "Spin-Up Time";
                case 5: return "Reallocated Sectors Count";
                case 9: return "Power-On Hours";
                case 10: return "Spin Retry Count";
                case 12: return "Power Cycle Count";
                case 187: return "Reported Uncorrectable Errors";
                case 188: return "Command Timeout";
                case 190: return "Airflow Temperature (vendor-specific)";
                case 194: return "Temperature (vendor-specific)";
                case 196: return "Reallocation Event Count";
                case 197: return "Current Pending Sector Count";
                case 198: return "Offline Uncorrectable Sector Count";
                case 199: return "UDMA CRC Error Count";
                case 241: return "Total LBAs Written (vendor-specific)";
                case 242: return "Total LBAs Read (vendor-specific)";
                default: return "Атрибут производителя";
            }
        }
    }
}
