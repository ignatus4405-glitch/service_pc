using System;
using System.Collections.Generic;
using System.IO;
using PcCare.Diagnostics;
using PcCare.Models;

namespace PcCare.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            int failures = 0;
            failures += Run("SMART predicts failure => critical recommendation", TestSmartFailureIsCritical);
            failures += Run("low disk space => critical recommendation", TestLowDiskSpaceIsCritical);
            failures += Run("Windows disk failure status => critical recommendation", TestDiskFailureStatusIsCritical);
            failures += Run("low available RAM => warning recommendation", TestLowMemoryIsWarning);
            failures += Run("high momentary CPU load => warning recommendation", TestHighCpuLoadIsWarning);
            failures += Run("missing SMART data => unknown, not healthy", TestMissingSmartDataIsUnknown);
            failures += Run("healthy available data => informational result", TestHealthySnapshotHasNoWarning);
            failures += Run("cleanup removes only files older than one day", TestCleanupRemovesOnlyOldFiles);
            failures += Run("cleanup rejects a drive root", TestCleanupRejectsDriveRoot);
            failures += Run("combined stress workload selections are validated", TestStressModeSelection);
            failures += Run("CPU stress block matches its known checksum", TestCpuStressChecksum);
            failures += Run("FPU stress block matches its known checksum", TestFpuStressChecksum);
            failures += Run("cache stress pattern detects changed bytes", TestCacheStressPattern);
            failures += Run("memory stress allocation is capped safely", TestMemoryStressTargetIsCapped);
            failures += Run("memory stress is disabled when free RAM is too low", TestMemoryStressRequiresHeadroom);
            failures += Run("ATA SMART attributes and temperature are parsed", TestSmartAttributesAreParsed);
            failures += Run("disk scan latency buckets use documented thresholds", TestDiskLatencyClassification);
            failures += Run("temperature probes convert deci-Kelvin and reject sentinel values", TestTemperatureConversion);
            failures += Run("live sensor values reject unknown types and implausible readings", TestLiveSensorValueFiltering);
            failures += Run("live chart scrolls to 60 seconds and completed chart keeps full history", TestLiveChartWindow);
            failures += Run("only explicitly labelled probes count as CPU temperature", TestCpuSensorIdentification);

            if (failures == 0)
            {
                Console.WriteLine("All tests passed.");
                return 0;
            }

            Console.Error.WriteLine(failures + " test(s) failed.");
            return 1;
        }

        private static int Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS: " + name);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + name + " — " + ex.Message);
                return 1;
            }
        }

        private static void TestSmartFailureIsCritical()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.SmartQuerySucceeded = true;
            snapshot.SmartDevices.Add(new SmartDeviceStatus
            {
                InstanceName = "Disk0",
                PredictsFailure = true
            });

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsSeverity(results, RecommendationSeverity.Critical), "Expected a critical SMART recommendation.");
        }

        private static void TestLowDiskSpaceIsCritical()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.Volumes.Add(new LogicalVolumeInfo
            {
                DeviceId = "C:",
                SizeBytes = 1000,
                FreeBytes = 40
            });

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "Мало свободного места"), "Expected a low-disk-space recommendation.");
            Assert(ContainsSeverity(results, RecommendationSeverity.Critical), "Expected the 4% free-space result to be critical.");
        }

        private static void TestDiskFailureStatusIsCritical()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.PhysicalDisks.Add(new PhysicalDiskInfo
            {
                Index = 0,
                Model = "Test disk",
                Status = "Pred Fail"
            });

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "проблеме накопителя"), "Expected a recommendation for the Windows disk status.");
            Assert(ContainsSeverity(results, RecommendationSeverity.Critical), "Pred Fail should be critical.");
        }

        private static void TestLowMemoryIsWarning()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.TotalMemoryBytes = 8UL * 1024UL * 1024UL * 1024UL;
            snapshot.AvailableMemoryBytes = 512UL * 1024UL * 1024UL;
            snapshot.MemoryDataKnown = true;

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "Оперативная память"), "Expected a low-memory recommendation.");
            Assert(ContainsSeverity(results, RecommendationSeverity.Warning), "Low available RAM should be a warning.");
        }

        private static void TestHighCpuLoadIsWarning()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.CpuLoadPercent = 85;

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "Высокая загрузка процессора"), "Expected a high-CPU recommendation.");
            Assert(ContainsSeverity(results, RecommendationSeverity.Warning), "High CPU load should be a warning.");
        }

        private static void TestMissingSmartDataIsUnknown()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.SmartQuerySucceeded = true;

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "SMART не получено"), "SMART absence must be explained as unknown.");

            snapshot.SmartDevices.Add(new SmartDeviceStatus { InstanceName = "Disk0", PredictsFailure = null });
            results = new RecommendationEngine().Create(snapshot);
            Assert(ContainsTitle(results, "SMART не получено"), "An indeterminate SMART row must remain unknown.");
            Assert(!ContainsTitle(results, "Критических SMART-предупреждений не получено"), "An indeterminate row must not be described as healthy.");
        }

        private static void TestHealthySnapshotHasNoWarning()
        {
            SystemSnapshot snapshot = NewSnapshot();
            snapshot.SmartQuerySucceeded = true;
            snapshot.SmartDevices.Add(new SmartDeviceStatus { InstanceName = "Disk0", PredictsFailure = false });
            snapshot.TotalMemoryBytes = 16UL * 1024UL * 1024UL * 1024UL;
            snapshot.AvailableMemoryBytes = 8UL * 1024UL * 1024UL * 1024UL;
            snapshot.MemoryDataKnown = true;
            snapshot.CpuLoadPercent = 15;
            snapshot.Volumes.Add(new LogicalVolumeInfo
            {
                DeviceId = "C:",
                SizeBytes = 1000,
                FreeBytes = 500
            });

            IList<Recommendation> results = new RecommendationEngine().Create(snapshot);
            Assert(!ContainsSeverity(results, RecommendationSeverity.Warning), "Healthy test data should not produce warnings.");
            Assert(!ContainsSeverity(results, RecommendationSeverity.Critical), "Healthy test data should not produce critical alerts.");
        }

        private static void TestCleanupRemovesOnlyOldFiles()
        {
            string previousTemp = Environment.GetEnvironmentVariable("TEMP");
            string testTemp = Path.Combine(Path.GetTempPath(), "PcCare-Test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testTemp);
            string oldFile = Path.Combine(testTemp, "old.tmp");
            string recentFile = Path.Combine(testTemp, "recent.tmp");

            try
            {
                File.WriteAllText(oldFile, "old");
                File.WriteAllText(recentFile, "recent");
                File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-2));
                File.SetLastWriteTime(recentFile, DateTime.Now.AddHours(-1));
                Environment.SetEnvironmentVariable("TEMP", testTemp);

                CleanupResult result = new CleanupService().CleanOldTemporaryFiles();
                Assert(!File.Exists(oldFile), "The old temporary file should be removed.");
                Assert(File.Exists(recentFile), "A recent temporary file must be preserved.");
                Assert(result.DeletedFiles == 1, "Exactly one old file should be reported as deleted.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TEMP", previousTemp);
                try { Directory.Delete(testTemp, true); }
                catch { }
            }
        }

        private static void TestCleanupRejectsDriveRoot()
        {
            string previousTemp = Environment.GetEnvironmentVariable("TEMP");
            string root = Path.GetPathRoot(Path.GetTempPath());
            try
            {
                Environment.SetEnvironmentVariable("TEMP", root);
                bool blocked = false;
                try
                {
                    new CleanupService().CleanOldTemporaryFiles();
                }
                catch (InvalidOperationException)
                {
                    blocked = true;
                }
                Assert(blocked, "Cleanup must refuse to walk a drive root.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TEMP", previousTemp);
            }
        }

        private static void TestStressModeSelection()
        {
            StressTestMode all = StressTestMode.Cpu | StressTestMode.Fpu |
                StressTestMode.Cache | StressTestMode.Memory;
            Assert(StressTestService.IsValidModeSelection(all),
                "A combined selection of supported workloads should be accepted.");
            Assert(!StressTestService.IsValidModeSelection(StressTestMode.None),
                "An empty workload selection should be rejected.");
            Assert(!StressTestService.IsValidModeSelection((StressTestMode)16),
                "Unknown workload flags should be rejected.");
        }

        private static void TestCpuStressChecksum()
        {
            uint actual = StressTestService.ComputeCpuCheckValue(0xA5C39E17U, 65536);
            Assert(actual == 0xD8572465U, "CPU workload must match the known verification checksum.");
        }

        private static void TestFpuStressChecksum()
        {
            double actual = StressTestService.ComputeFpuCheckValue(8192);
            Assert(Math.Abs(actual - 2758.280819831906) < 0.00000001,
                "FPU workload must match the known floating-point verification value.");
        }

        private static void TestCacheStressPattern()
        {
            byte[] buffer = new byte[128];
            StressTestService.FillCacheTestPattern(buffer, 3);
            Assert(StressTestService.CountCacheTestMismatches(buffer, 3) == 0,
                "An intact cache working-set pattern must verify without mismatches.");
            buffer[17] ^= 0x01;
            Assert(StressTestService.CountCacheTestMismatches(buffer, 3) == 1,
                "A modified byte must be reported by the cache working-set verifier.");
        }

        private static void TestMemoryStressTargetIsCapped()
        {
            ulong eightGigabytes = 8UL * 1024UL * 1024UL * 1024UL;
            int target = StressTestService.CalculateMemoryTargetMegabytes(eightGigabytes, 1024);
            Assert(target == 1024, "The default memory stress allocation must not exceed the 1 GB cap.");
            int x86Target = StressTestService.CalculateMemoryTargetMegabytes(eightGigabytes, 512);
            Assert(x86Target == 512, "The 32-bit process allocation must respect its smaller address-space cap.");

            ulong oneGigabyte = 1024UL * 1024UL * 1024UL;
            target = StressTestService.CalculateMemoryTargetMegabytes(oneGigabyte, 1024);
            Assert(target == 256, "The test should reserve at most one quarter of currently available RAM.");
        }

        private static void TestMemoryStressRequiresHeadroom()
        {
            ulong lessThan256Megabytes = 255UL * 1024UL * 1024UL;
            int target = StressTestService.CalculateMemoryTargetMegabytes(lessThan256Megabytes, 1024);
            Assert(target == 0, "The memory test must not allocate when Windows has less than 256 MB free.");
        }

        private static void TestSmartAttributesAreParsed()
        {
            byte[] data = new byte[512];
            data[2] = 194;
            data[5] = 100;
            data[6] = 90;
            data[7] = 45;
            data[14] = 197;
            data[17] = 100;
            data[18] = 80;
            data[19] = 3;

            byte[] thresholdData = new byte[512];
            thresholdData[2] = 194;
            thresholdData[3] = 30;
            thresholdData[14] = 197;
            thresholdData[15] = 10;

            IDictionary<int, int> thresholds = SmartAttributeParser.ParseThresholds(thresholdData);
            IList<SmartAttribute> attributes = SmartAttributeParser.ParseAttributes(data, thresholds);
            Assert(attributes.Count == 2, "Expected both nonzero SMART attributes to be parsed.");
            Assert(attributes[0].Id == 194 && attributes[0].Threshold == 30,
                "Expected the temperature attribute and threshold to be matched by ID.");
            Assert(attributes[0].RawValue == 45 && attributes[0].RawHex == "00000000002D",
                "Expected the six-byte ATA raw value to be decoded little-endian.");
            Assert(SmartAttributeParser.ParseAtaTemperature(attributes) == 45,
                "Expected the common ATA temperature byte to be exposed as a best-effort reading.");
        }

        private static void TestDiskLatencyClassification()
        {
            Assert(DiskSurfaceTestService.ClassifyLatency(199.9) == DiskBlockSpeed.Normal,
                "A block below 200 ms should not be classified as slow.");
            Assert(DiskSurfaceTestService.ClassifyLatency(200.0) == DiskBlockSpeed.Slow,
                "A block at 200 ms should be classified as slow.");
            Assert(DiskSurfaceTestService.ClassifyLatency(1000.0) == DiskBlockSpeed.VerySlow,
                "A block at 1000 ms should be classified as very slow.");
        }

        private static void TestTemperatureConversion()
        {
            double celsius;
            Assert(LiveSensorService.TryConvertDeciKelvinToCelsius(2982, out celsius),
                "A plausible deci-Kelvin reading should convert to Celsius.");
            Assert(Math.Abs(celsius - 25.05) < 0.01,
                "298.2 K should convert to approximately 25.05 C.");
            Assert(!LiveSensorService.TryConvertDeciKelvinToCelsius(0, out celsius),
                "An empty/sentinel reading must remain unavailable rather than display an extreme temperature.");
            Assert(Double.IsNaN(celsius),
                "An unavailable sensor reading must not be replaced with a zero-degree value.");
        }

        private static void TestLiveSensorValueFiltering()
        {
            Assert(LiveSensorService.IsPlausibleSensorValue("Voltage", 1.2),
                "A finite, plausible sensor value should remain available.");
            Assert(!LiveSensorService.IsPlausibleSensorValue("Voltage", Double.NaN),
                "NaN must be treated as an unavailable sensor value.");
            Assert(!LiveSensorService.IsPlausibleSensorValue("Voltage", 500.0),
                "An implausible voltage must not be displayed as a real reading.");
            Assert(LiveSensorService.IsPlausibleSensorValue("Fan", 0.0),
                "A zero fan speed reported by a real sensor can be a valid stopped-fan reading.");
            Assert(!LiveSensorService.IsPlausibleSensorValue("Unknown", 10.0),
                "An unsupported sensor type must not be assigned an assumed unit.");
        }

        private static void TestLiveChartWindow()
        {
            DateTime first = new DateTime(2026, 11, 6, 10, 0, 0);
            DateTime plotStart;
            double duration;

            LiveChartWindow.Calculate(first, first, true, out plotStart, out duration);
            Assert(plotStart == first.AddSeconds(-2) && Math.Abs(duration - 2.0) < 0.001,
                "A single live sample should use a small nonzero plotting window.");

            LiveChartWindow.Calculate(first, first.AddSeconds(30), true, out plotStart, out duration);
            Assert(plotStart == first && Math.Abs(duration - 30.0) < 0.001,
                "A short live test should show its elapsed interval without a large empty window.");

            DateTime last = first.AddSeconds(90);
            LiveChartWindow.Calculate(first, last, true, out plotStart, out duration);
            Assert(plotStart == last.AddSeconds(-60) && Math.Abs(duration - 60.0) < 0.001,
                "A long live test should scroll across only the most recent 60 seconds.");

            LiveChartWindow.Calculate(first, last, false, out plotStart, out duration);
            Assert(plotStart == first && Math.Abs(duration - 90.0) < 0.001,
                "A completed test should retain the complete time range.");
        }

        private static void TestCpuSensorIdentification()
        {
            Assert(LiveSensorService.IsExplicitCpuSensor("CPU Package", String.Empty),
                "A sensor explicitly labelled CPU should be identified as a CPU reading.");
            Assert(!LiveSensorService.IsExplicitCpuSensor("ACPI\\ThermalZone\\TZ00", String.Empty),
                "A generic ACPI zone must not be mislabelled as CPU temperature.");
        }

        private static SystemSnapshot NewSnapshot()
        {
            return new SystemSnapshot
            {
                OperatingSystem = "Test Windows",
                CpuName = "Test CPU"
            };
        }

        private static bool ContainsSeverity(IList<Recommendation> items, RecommendationSeverity severity)
        {
            foreach (Recommendation item in items)
            {
                if (item.Severity == severity)
                    return true;
            }
            return false;
        }

        private static bool ContainsTitle(IList<Recommendation> items, string title)
        {
            foreach (Recommendation item in items)
            {
                if (item.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
