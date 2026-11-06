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
            Readings = new List<LiveSensorValue>();
            Notes = new List<string>();
        }

        public DateTime CollectedAt { get; set; }
        public int? CpuLoadPercent { get; set; }
        public ulong? TotalMemoryBytes { get; set; }
        public ulong? AvailableMemoryBytes { get; set; }
        public List<TemperatureSensorReading> TemperatureSensors { get; private set; }
        public List<LiveSensorValue> Readings { get; private set; }
        public List<string> Notes { get; private set; }
    }

    public sealed class TemperatureSensorReading
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public bool IsCpuSensor { get; set; }
        public double TemperatureCelsius { get; set; }
    }

    /// <summary>A real sensor or performance-counter value with its source and unit.</summary>
    public sealed class LiveSensorValue
    {
        public string Name { get; set; }
        public string SensorType { get; set; }
        public string Unit { get; set; }
        public string Source { get; set; }
        public string Identifier { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// Best-effort Windows performance and sensor readings. A temperature is shown as
    /// CPU temperature only when its source is explicitly labelled as CPU; generic ACPI
    /// zones remain separate because they may represent another part of the system.
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
                            if (value.HasValue && value.Value >= 0 && value.Value <= 100)
                            {
                                snapshot.CpuLoadPercent = value.Value;
                                AddReading(snapshot, "CPU (общая)", "Load", "%",
                                    "Windows WMI PerfOS", "_Total", snapshot.CpuLoadPercent.Value);
                            }
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
                            if (totalKb.HasValue && availableKb.HasValue && totalKb.Value > 0 &&
                                availableKb.Value <= totalKb.Value)
                            {
                                snapshot.TotalMemoryBytes = totalKb.Value * 1024UL;
                                snapshot.AvailableMemoryBytes = availableKb.Value * 1024UL;

                                double totalGigabytes = totalKb.Value / (1024.0 * 1024.0);
                                double availableGigabytes = availableKb.Value / (1024.0 * 1024.0);
                                AddReading(snapshot, "ОЗУ всего", "Memory", "ГБ",
                                    "Win32_OperatingSystem", "TotalVisibleMemorySize", totalGigabytes);
                                AddReading(snapshot, "ОЗУ свободно", "Memory", "ГБ",
                                    "Win32_OperatingSystem", "FreePhysicalMemory", availableGigabytes);
                                ulong usedKb = totalKb.Value - availableKb.Value;
                                double usedGigabytes = usedKb / (1024.0 * 1024.0);
                                double usedPercent = usedKb * 100.0 / totalKb.Value;
                                AddReading(snapshot, "ОЗУ занято", "Memory", "ГБ",
                                    "Win32_OperatingSystem", "UsedPhysicalMemory", usedGigabytes);
                                AddReading(snapshot, "Загрузка ОЗУ", "Load", "%",
                                    "Win32_OperatingSystem", "PhysicalMemoryLoad", usedPercent);
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
                            AddTemperatureReading(snapshot, name, "Win32_TemperatureProbe",
                                IsExplicitCpuSensor(name, description), celsius, description);
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
                            string unit;
                            if (!TryGetSensorUnit(sensorType, out unit))
                                continue;

                            double? value = ReadDouble(row, "Value");
                            if (!value.HasValue || !IsPlausibleSensorValue(sensorType, value.Value))
                                continue;

                            string name = ReadString(row, "Name");
                            string identifier = ReadString(row, "Identifier");
                            if (String.IsNullOrWhiteSpace(name))
                                name = !String.IsNullOrWhiteSpace(identifier) ? identifier : providerName + " sensor";
                            string source = providerName + " WMI";

                            if (String.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                            {
                                AddTemperatureReading(snapshot, name, source,
                                    IsExplicitCpuSensor(name, identifier), value.Value, identifier);
                            }
                            else
                            {
                                AddReading(snapshot, name, sensorType, unit, source, identifier, value.Value);
                            }
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
                            AddTemperatureReading(snapshot, instanceName,
                                "MSAcpi_ThermalZoneTemperature",
                                IsExplicitCpuSensor(instanceName, instanceName), celsius, instanceName);
                        }
                    }
                }
            }
            catch
            {
                // ACPI temperature providers are optional; a missing zone is not a sensor error.
            }
        }

        private static void AddTemperatureReading(LiveSensorSnapshot snapshot, string name,
            string source, bool isCpuSensor, double celsius, string identifier)
        {
            if (Double.IsNaN(celsius) || Double.IsInfinity(celsius) || celsius < -10.0 || celsius > 130.0)
                return;

            string safeName = String.IsNullOrWhiteSpace(name) ? "Temperature sensor" : name;
            string safeSource = String.IsNullOrWhiteSpace(source) ? "WMI" : source;
            snapshot.TemperatureSensors.Add(new TemperatureSensorReading
            {
                Name = safeName,
                Source = safeSource,
                IsCpuSensor = isCpuSensor,
                TemperatureCelsius = celsius
            });
            AddReading(snapshot, safeName, "Temperature", "°C", safeSource, identifier, celsius);
        }

        private static void AddReading(LiveSensorSnapshot snapshot, string name,
            string sensorType, string unit, string source, string identifier, double value)
        {
            if (snapshot == null || Double.IsNaN(value) || Double.IsInfinity(value))
                return;
            snapshot.Readings.Add(new LiveSensorValue
            {
                Name = name ?? String.Empty,
                SensorType = sensorType ?? String.Empty,
                Unit = unit ?? String.Empty,
                Source = source ?? String.Empty,
                Identifier = identifier ?? String.Empty,
                Value = value
            });
        }

        private static bool TryGetSensorUnit(string sensorType, out string unit)
        {
            if (String.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                unit = "°C";
            else if (String.Equals(sensorType, "Voltage", StringComparison.OrdinalIgnoreCase))
                unit = "В";
            else if (String.Equals(sensorType, "Clock", StringComparison.OrdinalIgnoreCase))
                unit = "МГц";
            else if (String.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase))
                unit = "%";
            else if (String.Equals(sensorType, "Fan", StringComparison.OrdinalIgnoreCase))
                unit = "об/мин";
            else if (String.Equals(sensorType, "Power", StringComparison.OrdinalIgnoreCase))
                unit = "Вт";
            else
            {
                unit = String.Empty;
                return false;
            }
            return true;
        }

        public static bool IsPlausibleSensorValue(string sensorType, double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value))
                return false;
            if (String.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                return value >= -10.0 && value <= 130.0;
            if (String.Equals(sensorType, "Voltage", StringComparison.OrdinalIgnoreCase))
                return value >= 0.0 && value <= 50.0;
            if (String.Equals(sensorType, "Clock", StringComparison.OrdinalIgnoreCase))
                return value >= 0.0 && value <= 20000.0;
            if (String.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase))
                return value >= 0.0 && value <= 100.0;
            if (String.Equals(sensorType, "Fan", StringComparison.OrdinalIgnoreCase))
                return value >= 0.0 && value <= 100000.0;
            if (String.Equals(sensorType, "Power", StringComparison.OrdinalIgnoreCase))
                return value >= 0.0 && value <= 10000.0;
            return false;
        }

        public static bool TryConvertDeciKelvinToCelsius(double deciKelvin, out double celsius)
        {
            celsius = deciKelvin / 10.0 - 273.15;
            if (Double.IsNaN(celsius) || Double.IsInfinity(celsius) || celsius < -10.0 || celsius > 130.0)
            {
                celsius = Double.NaN;
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
            object value;
            try { value = row[propertyName]; }
            catch { return null; }
            if (value == null)
                return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static ulong? ReadUInt64(ManagementBaseObject row, string propertyName)
        {
            object value;
            try { value = row[propertyName]; }
            catch { return null; }
            if (value == null)
                return null;
            try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static double? ReadDouble(ManagementBaseObject row, string propertyName)
        {
            object value;
            try { value = row[propertyName]; }
            catch { return null; }
            if (value == null)
                return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string ReadString(ManagementBaseObject row, string propertyName)
        {
            object value;
            try { value = row[propertyName]; }
            catch { return String.Empty; }
            if (value == null)
                return String.Empty;
            try { return Convert.ToString(value, CultureInfo.InvariantCulture).Trim(); }
            catch { return String.Empty; }
        }
    }
}
