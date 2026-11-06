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
            TemperatureSensors = new List<TemperatureSensorReading>();
            Notes = new List<string>();
        }

        public DateTime CollectedAt { get; set; }
        public int? CpuLoadPercent { get; set; }
        public ulong? TotalMemoryBytes { get; set; }
        public ulong? AvailableMemoryBytes { get; set; }
        public List<TemperatureSensorReading> TemperatureSensors { get; private set; }
        public List<string> Notes { get; private set; }
    }

    public sealed class TemperatureSensorReading
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public bool IsCpuSensor { get; set; }
        public double TemperatureCelsius { get; set; }
    }

    /// <summary>
    /// Best-effort Windows performance and temperature readings. A temperature is shown
    /// as CPU temperature only when its source is explicitly labelled as CPU; generic
    /// ACPI zones remain separate because they may represent another part of the system.
    /// </summary>
    public sealed class LiveSensorService
    {
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

        public LiveSensorSnapshot Read()
        {
            LiveSensorSnapshot snapshot = new LiveSensorSnapshot();
            ReadCpuLoad(snapshot);
            ReadMemory(snapshot);
            ReadTemperatureProbes(snapshot);
            ReadHardwareMonitorSensors(snapshot, @"root\LibreHardwareMonitor", "LibreHardwareMonitor");
            ReadHardwareMonitorSensors(snapshot, @"root\OpenHardwareMonitor", "OpenHardwareMonitor");
            ReadThermalZones(snapshot);
            snapshot.CollectedAt = DateTime.Now;
            return snapshot;
        }

        private static void ReadCpuLoad(LiveSensorSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                using (ManagementObjectCollection results = GetTimedResults(searcher))
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
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = GetTimedResults(searcher))
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

        private static void ReadTemperatureProbes(LiveSensorSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, Description, CurrentReading FROM Win32_TemperatureProbe"))
                using (ManagementObjectCollection results = GetTimedResults(searcher))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            int? raw = ReadInt32(row, "CurrentReading");
                            if (!raw.HasValue)
                                continue;

                            double celsius;
                            if (!TryConvertDeciKelvinToCelsius(raw.Value, out celsius))
                                continue;

                            string name = ReadString(row, "Name");
                            string description = ReadString(row, "Description");
                            if (String.IsNullOrWhiteSpace(name))
                                name = String.IsNullOrWhiteSpace(description) ? "Temperature probe" : description;
                            snapshot.TemperatureSensors.Add(new TemperatureSensorReading
                            {
                                Name = name,
                                Source = "Win32_TemperatureProbe",
                                IsCpuSensor = IsExplicitCpuSensor(name, description),
                                TemperatureCelsius = celsius
                            });
                        }
                    }
                }
            }
            catch
            {
                // Many Windows drivers do not expose Win32_TemperatureProbe; keep the reading unknown.
            }
        }

        private static void ReadHardwareMonitorSensors(LiveSensorSnapshot snapshot, string namespacePath, string providerName)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    namespacePath, "SELECT * FROM Sensor"))
                using (ManagementObjectCollection results = GetTimedResults(searcher))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            string sensorType = ReadString(row, "SensorType");
                            if (!String.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                                continue;

                            double? value = ReadDouble(row, "Value");
                            if (!value.HasValue || value.Value < -10.0 || value.Value > 130.0)
                                continue;

                            string name = ReadString(row, "Name");
                            string identifier = ReadString(row, "Identifier");
                            snapshot.TemperatureSensors.Add(new TemperatureSensorReading
                            {
                                Name = !String.IsNullOrWhiteSpace(name) ? name :
                                    (!String.IsNullOrWhiteSpace(identifier) ? identifier : providerName + " temperature sensor"),
                                Source = providerName + " WMI",
                                IsCpuSensor = IsExplicitCpuSensor(name, identifier),
                                TemperatureCelsius = value.Value
                            });
                        }
                    }
                }
            }
            catch
            {
                // Third-party WMI sensor providers are optional and are never installed or started by PC Care.
            }
        }

        private static void ReadThermalZones(LiveSensorSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection results = GetTimedResults(searcher))
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            ulong? raw = ReadUInt64(row, "CurrentTemperature");
                            if (!raw.HasValue)
                                continue;

                            double celsius;
                            if (!TryConvertDeciKelvinToCelsius(raw.Value, out celsius))
                                continue;

                            string instanceName = ReadString(row, "InstanceName");
                            snapshot.TemperatureSensors.Add(new TemperatureSensorReading
                            {
                                Name = instanceName,
                                Source = "MSAcpi_ThermalZoneTemperature",
                                IsCpuSensor = IsExplicitCpuSensor(instanceName, instanceName),
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

        public static bool TryConvertDeciKelvinToCelsius(double deciKelvin, out double celsius)
        {
            celsius = deciKelvin / 10.0 - 273.15;
            if (Double.IsNaN(celsius) || Double.IsInfinity(celsius) || celsius < -10.0 || celsius > 130.0)
            {
                celsius = 0.0;
                return false;
            }
            return true;
        }

        public static bool IsExplicitCpuSensor(string name, string description)
        {
            string label = ((name ?? String.Empty) + " " + (description ?? String.Empty)).ToLowerInvariant();
            return label.Contains("cpu") || label.Contains("processor") || label.Contains("процессор") ||
                label.Contains("tctl") || label.Contains("tdie") || label.Contains("cpu package");
        }

        private static ManagementObjectCollection GetTimedResults(ManagementObjectSearcher searcher)
        {
            EnumerationOptions options = new EnumerationOptions();
            options.Timeout = QueryTimeout;
            options.Rewindable = false;
            searcher.Options = options;
            return searcher.Get();
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

        private static double? ReadDouble(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            if (value == null)
                return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string ReadString(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }
    }
}
