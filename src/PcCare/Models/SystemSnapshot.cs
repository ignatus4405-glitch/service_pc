using System;
using System.Collections.Generic;

namespace PcCare.Models
{
    public sealed class SystemSnapshot
    {
        public SystemSnapshot()
        {
            PhysicalDisks = new List<PhysicalDiskInfo>();
            Volumes = new List<LogicalVolumeInfo>();
            SmartDevices = new List<SmartDeviceStatus>();
            Notes = new List<string>();
        }

        public DateTime CollectedAt { get; set; }
        public string OperatingSystem { get; set; }
        public string OsVersion { get; set; }
        public string OsBuild { get; set; }
        public string Architecture { get; set; }
        public string CpuName { get; set; }
        public int? CpuLoadPercent { get; set; }
        public int? CpuCores { get; set; }
        public int? CpuLogicalProcessors { get; set; }
        public int? CpuMaxClockMhz { get; set; }
        public ulong TotalMemoryBytes { get; set; }
        public ulong AvailableMemoryBytes { get; set; }
        public bool MemoryDataKnown { get; set; }
        public bool SmartQuerySucceeded { get; set; }
        public string SmartError { get; set; }
        public List<PhysicalDiskInfo> PhysicalDisks { get; private set; }
        public List<LogicalVolumeInfo> Volumes { get; private set; }
        public List<SmartDeviceStatus> SmartDevices { get; private set; }
        public List<string> Notes { get; private set; }
    }

    public sealed class PhysicalDiskInfo
    {
        public int Index { get; set; }
        public string Model { get; set; }
        public string InterfaceType { get; set; }
        public string Status { get; set; }
        public ulong SizeBytes { get; set; }
    }

    public sealed class LogicalVolumeInfo
    {
        public string DeviceId { get; set; }
        public string Label { get; set; }
        public ulong SizeBytes { get; set; }
        public ulong FreeBytes { get; set; }

        public double FreePercent
        {
            get
            {
                if (SizeBytes == 0)
                    return 0;

                return (double)FreeBytes * 100.0 / SizeBytes;
            }
        }
    }

    public sealed class SmartDeviceStatus
    {
        public SmartDeviceStatus()
        {
            Attributes = new List<SmartAttribute>();
        }

        public string InstanceName { get; set; }
        public int? PhysicalDriveIndex { get; set; }
        public bool? PredictsFailure { get; set; }
        public uint? ReasonCode { get; set; }
        public double? TemperatureCelsius { get; set; }
        public List<SmartAttribute> Attributes { get; private set; }
    }

    /// <summary>
    /// ATA SMART attribute as exposed by the Windows provider. Raw values and common names
    /// are vendor-specific hints and must not be treated as a universal health verdict.
    /// </summary>
    public sealed class SmartAttribute
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Current { get; set; }
        public int Worst { get; set; }
        public int? Threshold { get; set; }
        public ulong RawValue { get; set; }
        public string RawHex { get; set; }
    }

    public enum RecommendationSeverity
    {
        Information,
        Warning,
        Critical
    }

    public sealed class Recommendation
    {
        public Recommendation(RecommendationSeverity severity, string title, string details)
        {
            Severity = severity;
            Title = title;
            Details = details;
        }

        public RecommendationSeverity Severity { get; private set; }
        public string Title { get; private set; }
        public string Details { get; private set; }
    }
}
