using System;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using PcCare.Models;

namespace PcCare.Diagnostics
{
    /// <summary>
    /// Reads a best-effort snapshot from Windows WMI. A missing WMI provider (especially
    /// for SMART/NVMe/USB devices) is reported as unavailable data, never as a disk failure.
    /// </summary>
    public sealed class DiagnosticService
    {
        private static readonly Regex PhysicalDrivePattern = new Regex(
            @"(?:PHYSICALDRIVE|_)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public SystemSnapshot Run()
        {
            SystemSnapshot snapshot = new SystemSnapshot();
            snapshot.CollectedAt = DateTime.Now;

            ReadOperatingSystem(snapshot);
            ReadProcessor(snapshot);
            ReadMemory(snapshot);
            ReadPhysicalDisks(snapshot);
            ReadVolumes(snapshot);
            ReadSmartStatus(snapshot);

            return snapshot;
        }

        private static void ReadOperatingSystem(SystemSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            snapshot.OperatingSystem = ReadString(row, "Caption");
                            snapshot.OsVersion = ReadString(row, "Version");
                            snapshot.OsBuild = ReadString(row, "BuildNumber");
                            snapshot.Architecture = ReadString(row, "OSArchitecture");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.Notes.Add("Не удалось прочитать сведения о Windows: " + ex.Message);
            }

            if (String.IsNullOrWhiteSpace(snapshot.OperatingSystem))
            {
                snapshot.OperatingSystem = Environment.OSVersion.Platform.ToString();
                snapshot.OsVersion = Environment.OSVersion.Version.ToString();
                snapshot.Architecture = Environment.Is64BitOperatingSystem ? "64-разрядная" : "32-разрядная";
            }
        }

        private static void ReadProcessor(SystemSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, LoadPercentage, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            if (String.IsNullOrWhiteSpace(snapshot.CpuName))
                                snapshot.CpuName = ReadString(row, "Name");

                            int? load = ReadInt32(row, "LoadPercentage");
                            if (load.HasValue)
                                snapshot.CpuLoadPercent = Math.Max(0, Math.Min(100, load.Value));

                            int? cores = ReadInt32(row, "NumberOfCores");
                            if (cores.HasValue)
                                snapshot.CpuCores = (snapshot.CpuCores ?? 0) + cores.Value;

                            int? processors = ReadInt32(row, "NumberOfLogicalProcessors");
                            if (processors.HasValue)
                                snapshot.CpuLogicalProcessors = (snapshot.CpuLogicalProcessors ?? 0) + processors.Value;

                            if (!snapshot.CpuMaxClockMhz.HasValue)
                                snapshot.CpuMaxClockMhz = ReadInt32(row, "MaxClockSpeed");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.Notes.Add("Не удалось прочитать сведения о процессоре: " + ex.Message);
            }

            if (String.IsNullOrWhiteSpace(snapshot.CpuName))
                snapshot.CpuName = "Сведения недоступны";
        }

        private static void ReadMemory(SystemSnapshot snapshot)
        {
            string readError = null;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            ulong? totalKb = ReadUInt64(row, "TotalVisibleMemorySize");
                            ulong? availableKb = ReadUInt64(row, "FreePhysicalMemory");
                            if (totalKb.HasValue)
                                snapshot.TotalMemoryBytes = totalKb.Value * 1024UL;
                            if (availableKb.HasValue)
                                snapshot.AvailableMemoryBytes = availableKb.Value * 1024UL;
                            snapshot.MemoryDataKnown = totalKb.HasValue && availableKb.HasValue;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                readError = ex.Message;
            }

            if (!snapshot.MemoryDataKnown)
                snapshot.Notes.Add(String.IsNullOrWhiteSpace(readError)
                    ? "Данные об оперативной памяти получены не полностью."
                    : "Не удалось прочитать сведения об оперативной памяти: " + readError);
        }

        private static void ReadPhysicalDisks(SystemSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Index, Model, InterfaceType, Status, Size FROM Win32_DiskDrive"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            PhysicalDiskInfo disk = new PhysicalDiskInfo();
                            disk.Index = ReadInt32(row, "Index") ?? snapshot.PhysicalDisks.Count;
                            disk.Model = ReadString(row, "Model");
                            disk.InterfaceType = ReadString(row, "InterfaceType");
                            disk.Status = ReadString(row, "Status");
                            disk.SizeBytes = ReadUInt64(row, "Size") ?? 0UL;
                            snapshot.PhysicalDisks.Add(disk);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.Notes.Add("Не удалось прочитать список физических дисков: " + ex.Message);
            }
        }

        private static void ReadVolumes(SystemSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            LogicalVolumeInfo volume = new LogicalVolumeInfo();
                            volume.DeviceId = ReadString(row, "DeviceID");
                            volume.Label = ReadString(row, "VolumeName");
                            volume.SizeBytes = ReadUInt64(row, "Size") ?? 0UL;
                            volume.FreeBytes = ReadUInt64(row, "FreeSpace") ?? 0UL;
                            snapshot.Volumes.Add(volume);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.Notes.Add("Не удалось прочитать сведения о разделах диска: " + ex.Message);
            }
        }

        private static void ReadSmartStatus(SystemSnapshot snapshot)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    snapshot.SmartQuerySucceeded = true;
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            SmartDeviceStatus status = new SmartDeviceStatus();
                            status.InstanceName = ReadString(row, "InstanceName");
                            status.PhysicalDriveIndex = ParsePhysicalDriveIndex(status.InstanceName);
                            status.PredictsFailure = ReadBoolean(row, "PredictFailure");
                            status.ReasonCode = ReadUInt32(row, "Reason");
                            snapshot.SmartDevices.Add(status);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.SmartQuerySucceeded = false;
                snapshot.SmartError = ex.Message;
            }
        }

        private static int? ParsePhysicalDriveIndex(string instanceName)
        {
            if (String.IsNullOrWhiteSpace(instanceName))
                return null;

            Match match = PhysicalDrivePattern.Match(instanceName);
            int index;
            if (match.Success && Int32.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out index))
                return index;

            return null;
        }

        private static string ReadString(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
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

        private static uint? ReadUInt32(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            if (value == null)
                return null;

            try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static bool? ReadBoolean(ManagementBaseObject row, string propertyName)
        {
            object value = row[propertyName];
            if (value == null)
                return null;

            if (value is bool)
                return (bool)value;

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            bool parsed;
            if (Boolean.TryParse(text, out parsed))
                return parsed;

            int number;
            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number != 0;

            return null;
        }
    }
}
