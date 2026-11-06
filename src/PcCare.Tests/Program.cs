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
            failures += Run("CPU stress block matches its known checksum", TestCpuStressChecksum);
            failures += Run("memory stress allocation is capped safely", TestMemoryStressTargetIsCapped);
            failures += Run("memory stress is disabled when free RAM is too low", TestMemoryStressRequiresHeadroom);

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

        private static void TestCpuStressChecksum()
        {
            uint actual = StressTestService.ComputeCpuCheckValue(0xA5C39E17U, 65536);
            Assert(actual == 0xD8572465U, "CPU workload must match the known verification checksum.");
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
