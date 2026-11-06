using System;
using System.Diagnostics;

namespace PcCare.Diagnostics
{
    /// <summary>
    /// Opens Windows' own maintenance tools. The user remains in control of any changes.
    /// </summary>
    public static class WindowsMaintenance
    {
        public static void OpenOptimizeDrives()
        {
            StartExecutable("dfrgui.exe", String.Empty);
        }

        public static void OpenDiskCleanup()
        {
            StartExecutable("cleanmgr.exe", String.Empty);
        }

        public static void OpenTaskManager()
        {
            StartExecutable("taskmgr.exe", String.Empty);
        }

        public static void OpenStartupManager()
        {
            Version version = Environment.OSVersion.Version;
            if (version.Major >= 10)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
                    return;
                }
                catch
                {
                    // Fall back to Task Manager if the Settings URI is unavailable.
                }
            }

            if (version.Major == 6 && version.Minor >= 2)
                StartExecutable("taskmgr.exe", String.Empty);
            else
                StartExecutable("msconfig.exe", String.Empty);
        }

        private static void StartExecutable(string fileName, string arguments)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
        }
    }
}
