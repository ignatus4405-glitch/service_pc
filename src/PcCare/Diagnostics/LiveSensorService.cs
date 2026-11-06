using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace PcCare.Diagnostics
{
    public sealed class LiveSensorSnapshot
    {
        public LiveSensorSnapshot()
        {
            ThermalZones = new List<ThermalZoneReading>();
            Notes = new List<string>();
        }

        public DateTime CollectedAt { get; set; }
        public int? CpuLoadPercent { get; set; }
        public ulong? TotalMemoryBytes { get; set; }
        public ulong? AvailableMemoryBytes { get; set; }
        public List<ThermalZoneReading> ThermalZones { get; private set; }
        public List<string> Notes { get; private set; }
    }

    public sealed class ThermalZoneReading
    {
        public string Name { get; set; }
        public double TemperatureCelsius { get; set; }
    }

    /// <summary>
    /// Best-effort Windows performance and ACPI readings. ACPI thermal zones are not
    /// guaranteed to represent the CPU package; unsupported readings remain unknown.
    /// </summary>
    public sealed class LiveSensorService
    {
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

        public LiveSensorSnapshot Read()
        {
            LiveSensorSnapshot snapshot = new LiveSensorSnapshot();
            snapshot.CollectedAt = DateTime.Now;
            ReadCpuLoad(snapshot);
            ReadMemory(snapshot);
            ReadThermalZones(snapshot);
            return snapshot;
        }

        private static void ReadCpuLoad(LiveSensorSnapshot snapshot)
        {
            try
            {
                EnumerationOptions options = new EnumerationOptions();
                options.Timeout = QueryTimeout;
                options.Rewindable = false;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                using (ManagementObjectCollection results = searcher.Get(options))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            int? value = ReadInt32(row, "PercentProcessorTime");
                            if (value.HasValue)
                                snapshot.CpuLoadPercent = Math.Max(0, Math.Min(100, value.Value));
                            break;
                        }
                    }
                }
            }
            catch
            {
                snapshot.Notes.Add("Загрузка CPU недоступна через Windows WMI.");
            }
        }

        private static void ReadMemory(LiveSensorSnapshot snapshot)
        {
            try
            {
                EnumerationOptions options = new EnumerationOptions();
                options.Timeout = QueryTimeout;
                options.Rewindable = false;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = searcher.Get(options))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            ulong? totalKb = ReadUInt64(row, "TotalVisibleMemorySize");
                            ulong? availableKb = ReadUInt64(row, "FreePhysicalMemory");
                            if (totalKb.HasValue && availableKb.HasValue)
                            {
                                snapshot.TotalMemoryBytes = totalKb.Value * 1024UL;
                                snapshot.AvailableMemoryBytes = availableKb.Value * 1024UL;
                            }
                            break;
                        }
                    }
                }
            }
            catch
            {
                snapshot.Notes.Add("Данные об использовании памяти недоступны через Windows WMI.");
            }
        }

        private static void ReadThermalZones(LiveSensorSnapshot snapshot)
        {
            try
            {
                EnumerationOptions options = new EnumerationOptions();
                options.Timeout = QueryTimeout;
                options.Rewindable = false;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection results = searcher.Get(options))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            ulong? raw = ReadUInt64(row, "CurrentTemperature");
                            if (!raw.HasValue)
                                continue;

                            double celsius = raw.Value / 10.0 - 273.15;
                            if (celsius < -10.0 || celsius > 130.0)
                                continue;

                            snapshot.ThermalZones.Add(new ThermalZoneReading
                            {
                                Name = ReadString(row, "InstanceName"),
                                TemperatureCelsius = celsius
                            });
                        }
                    }
                }
            }
            catch
            {
                // ACPI temperature providers are optional; a missing zone is not a sensor error.
            }
        }

        private static int? ReadInt32(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            if (value == null)
                return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static ulong? ReadUInt64(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            if (value == null)
                return null;
            try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string ReadString(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }
    }
}
