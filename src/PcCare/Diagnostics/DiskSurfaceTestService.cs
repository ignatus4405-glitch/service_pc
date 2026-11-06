using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PcCare.Diagnostics
{
    public enum DiskBlockSpeed
    {
        Normal,
        Slow,
        VerySlow
    }

    public sealed class DiskScanProgress
    {
        public long TotalBytes { get; internal set; }
        public long BytesProcessed { get; internal set; }
        public long BytesRead { get; internal set; }
        public long BlocksProcessed { get; internal set; }
        public long ErrorBlocks { get; internal set; }
        public long SlowBlocks { get; internal set; }
        public long VerySlowBlocks { get; internal set; }
        public double LastBlockMilliseconds { get; internal set; }
        public double MaximumBlockMilliseconds { get; internal set; }

        public double PercentComplete
        {
            get { return TotalBytes <= 0 ? 0.0 : Math.Min(100.0, (double)BytesProcessed * 100.0 / TotalBytes); }
        }
    }

    public sealed class DiskScanResult
    {
        public DiskScanResult()
        {
            ErrorSamples = new List<string>();
        }

        public long TotalBytes { get; internal set; }
        public long BytesProcessed { get; internal set; }
        public long BytesRead { get; internal set; }
        public long BlocksProcessed { get; internal set; }
        public long ErrorBlocks { get; internal set; }
        public long SlowBlocks { get; internal set; }
        public long VerySlowBlocks { get; internal set; }
        public double MaximumBlockMilliseconds { get; internal set; }
        public bool Cancelled { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public List<string> ErrorSamples { get; private set; }

        public bool Completed
        {
            get { return !Cancelled && String.IsNullOrWhiteSpace(ErrorMessage) && TotalBytes > 0 && BytesProcessed >= TotalBytes; }
        }
    }

    /// <summary>
    /// Sequential, read-only scan of a Windows physical drive. It never writes, repairs,
    /// remaps or erases sectors. A single synchronous read can take a while if a drive retries.
    /// </summary>
    public sealed class DiskSurfaceTestService
    {
        public const int BlockSizeBytes = 1024 * 1024;
        public const double SlowBlockThresholdMilliseconds = 200.0;
        public const double VerySlowBlockThresholdMilliseconds = 1000.0;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint IoctlDiskGetLengthInfo = 0x0007405c;
        private const uint FileBegin = 0;
        private const int MaximumErrorSamples = 20;
        private const int MaximumConsecutiveReadErrors = 10;

        public static DiskBlockSpeed ClassifyLatency(double milliseconds)
        {
            if (Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds) || milliseconds < 0.0)
                throw new ArgumentOutOfRangeException("milliseconds");
            if (milliseconds >= VerySlowBlockThresholdMilliseconds)
                return DiskBlockSpeed.VerySlow;
            if (milliseconds >= SlowBlockThresholdMilliseconds)
                return DiskBlockSpeed.Slow;
            return DiskBlockSpeed.Normal;
        }

        public Task<DiskScanResult> RunAsync(int physicalDriveIndex,
            IProgress<DiskScanProgress> progress, CancellationToken cancellationToken)
        {
            if (physicalDriveIndex < 0)
                throw new ArgumentOutOfRangeException("physicalDriveIndex");

            return Task.Run(delegate
            {
                return Scan(physicalDriveIndex, progress, cancellationToken);
            }, CancellationToken.None);
        }

        private static DiskScanResult Scan(int physicalDriveIndex,
            IProgress<DiskScanProgress> progress, CancellationToken cancellationToken)
        {
            DiskScanResult result = new DiskScanResult();
            string devicePath = @"\\.\PhysicalDrive" + physicalDriveIndex;
            using (SafeFileHandle handle = CreateFile(devicePath, GenericRead,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                FileAttributeNormal | FileFlagSequentialScan, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    result.ErrorMessage = FormatWin32Error(Marshal.GetLastWin32Error());
                    return result;
                }

                long driveLength;
                uint bytesReturned;
                if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0,
                    out driveLength, (uint)Marshal.SizeOf(typeof(long)), out bytesReturned, IntPtr.Zero))
                {
                    result.ErrorMessage = "Не удалось определить размер физического диска: " +
                        FormatWin32Error(Marshal.GetLastWin32Error());
                    return result;
                }
                if (driveLength <= 0)
                {
                    result.ErrorMessage = "Windows вернула недопустимый размер физического диска.";
                    return result;
                }

                result.TotalBytes = driveLength;
                byte[] buffer = new byte[BlockSizeBytes];
                Stopwatch totalTimer = Stopwatch.StartNew();
                long lastReportMilliseconds = -1000;
                long offset = 0;
                int consecutiveErrors = 0;
                double lastBlockMilliseconds = 0.0;

                while (offset < driveLength)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    int requested = (int)Math.Min((long)buffer.Length, driveLength - offset);
                    long newPosition;
                    if (!SetFilePointerEx(handle, offset, out newPosition, FileBegin))
                    {
                        result.ErrorMessage = "Не удалось перейти к блоку диска 0x" + offset.ToString("X") + ": " +
                            FormatWin32Error(Marshal.GetLastWin32Error());
                        break;
                    }

                    Stopwatch blockTimer = Stopwatch.StartNew();
                    uint actualRead;
                    int errorCode;
                    bool readSucceeded = ReadExact(handle, buffer, requested, cancellationToken, out actualRead, out errorCode);
                    blockTimer.Stop();
                    double elapsedMilliseconds = blockTimer.Elapsed.TotalMilliseconds;
                    lastBlockMilliseconds = elapsedMilliseconds;

                    result.BlocksProcessed++;
                    result.BytesProcessed += requested;
                    if (readSucceeded)
                    {
                        consecutiveErrors = 0;
                        result.BytesRead += actualRead;
                        DiskBlockSpeed speed = ClassifyLatency(elapsedMilliseconds);
                        if (speed == DiskBlockSpeed.Slow)
                            result.SlowBlocks++;
                        if (speed == DiskBlockSpeed.VerySlow)
                            result.VerySlowBlocks++;
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        result.BytesProcessed -= requested;
                        result.BlocksProcessed--;
                        break;
                    }
                    else
                    {
                        consecutiveErrors++;
                        result.BytesRead += actualRead;
                        result.ErrorBlocks++;
                        if (result.ErrorSamples.Count < MaximumErrorSamples)
                            result.ErrorSamples.Add("Смещение 0x" + offset.ToString("X") + ", код Windows " + errorCode);
                    }

                    if (elapsedMilliseconds > result.MaximumBlockMilliseconds)
                        result.MaximumBlockMilliseconds = elapsedMilliseconds;
                    offset += requested;

                    long now = totalTimer.ElapsedMilliseconds;
                    if (progress != null && now - lastReportMilliseconds >= 500)
                    {
                        lastReportMilliseconds = now;
                        try { progress.Report(CreateProgress(result, elapsedMilliseconds)); }
                        catch { }
                    }

                    if (consecutiveErrors >= MaximumConsecutiveReadErrors)
                    {
                        result.ErrorMessage = "Проверка остановлена после десяти ошибок чтения подряд.";
                        break;
                    }
                }

                if (progress != null)
                {
                    try { progress.Report(CreateProgress(result, lastBlockMilliseconds)); }
                    catch { }
                }
            }
            return result;
        }

        private static bool ReadExact(SafeFileHandle handle, byte[] buffer, int requestedBytes,
            CancellationToken cancellationToken, out uint totalRead, out int errorCode)
        {
            totalRead = 0;
            errorCode = 0;
            while (totalRead < requestedBytes)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                uint bytesRead;
                uint remaining = (uint)(requestedBytes - totalRead);
                if (!ReadFile(handle, buffer, remaining, out bytesRead, IntPtr.Zero))
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return false;
                }
                if (bytesRead == 0)
                {
                    errorCode = 38; // ERROR_HANDLE_EOF
                    return false;
                }
                totalRead += bytesRead;
            }
            return true;
        }

        private static DiskScanProgress CreateProgress(DiskScanResult result, double lastBlockMilliseconds)
        {
            return new DiskScanProgress
            {
                TotalBytes = result.TotalBytes,
                BytesProcessed = result.BytesProcessed,
                BytesRead = result.BytesRead,
                BlocksProcessed = result.BlocksProcessed,
                ErrorBlocks = result.ErrorBlocks,
                SlowBlocks = result.SlowBlocks,
                VerySlowBlocks = result.VerySlowBlocks,
                LastBlockMilliseconds = lastBlockMilliseconds,
                MaximumBlockMilliseconds = result.MaximumBlockMilliseconds
            };
        }

        private static string FormatWin32Error(int errorCode)
        {
            if (errorCode == 5)
                return "Доступ к физическому диску запрещён. Если вы решили продолжить, запустите PC Care вручную с повышенными правами; приложение не запрашивает UAC автоматически.";
            if (errorCode == 0)
                return "Windows не предоставила код ошибки.";
            try { return new Win32Exception(errorCode).Message + " (код " + errorCode + ")"; }
            catch { return "ошибка Windows " + errorCode; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
            IntPtr inputBuffer, uint inputBufferSize, out long outputBuffer, uint outputBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(SafeFileHandle file, long distanceToMove,
            out long newFilePointer, uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint bytesToRead,
            out uint bytesRead, IntPtr overlapped);
    }
}
