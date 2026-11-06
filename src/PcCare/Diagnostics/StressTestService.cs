using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PcCare.Diagnostics
{
    public enum StressTestMode
    {
        Cpu,
        Memory,
        CpuAndMemory
    }

    public sealed class StressTestOptions
    {
        public StressTestMode Mode { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public sealed class StressTestProgress
    {
        public TimeSpan Elapsed { get; internal set; }
        public long CpuBlocksCompleted { get; internal set; }
        public long CpuErrors { get; internal set; }
        public int CpuWorkers { get; internal set; }
        public int MemoryMegabytes { get; internal set; }
        public long MemoryPassesCompleted { get; internal set; }
        public long MemoryErrors { get; internal set; }
    }

    public sealed class StressTestResult
    {
        public StressTestMode Mode { get; internal set; }
        public TimeSpan Duration { get; internal set; }
        public bool Cancelled { get; internal set; }
        public int CpuWorkers { get; internal set; }
        public long CpuBlocksCompleted { get; internal set; }
        public long CpuErrors { get; internal set; }
        public int MemoryMegabytes { get; internal set; }
        public long MemoryPassesCompleted { get; internal set; }
        public long MemoryErrors { get; internal set; }
        public string FirstMemoryError { get; internal set; }
        public string ErrorMessage { get; internal set; }

        public bool HasErrors
        {
            get { return CpuErrors > 0 || MemoryErrors > 0 || !String.IsNullOrWhiteSpace(ErrorMessage); }
        }
    }

    /// <summary>
    /// User-initiated, bounded CPU and RAM stability checks. This does not test GPU/VRAM,
    /// power delivery or temperatures and never runs automatically.
    /// </summary>
    public sealed class StressTestService
    {
        private const int CpuBlockIterations = 65536;
        private const uint CpuCheckSeed = 0xA5C39E17U;
        private const uint ExpectedCpuCheckValue = 0xD8572465U;
        private const int MemoryChunkBytes = 16 * 1024 * 1024;
        private const int MinimumMemoryTestMegabytes = 64;
        private const int MaximumMemoryTestMegabytes64Bit = 1024;
        private const int MaximumMemoryTestMegabytes32Bit = 512;
        private const int MaximumCpuWorkers = 64;

        private static readonly byte[] MemoryPatterns = { 0x00, 0xff, 0xaa, 0x55, 0xcc, 0x33, 0xf0, 0x0f };

        public async Task<StressTestResult> RunAsync(StressTestOptions options,
            IProgress<StressTestProgress> progress, CancellationToken cancellationToken)
        {
            ValidateOptions(options);

            int cpuWorkers = IncludesCpu(options.Mode)
                ? Math.Max(1, Math.Min(Environment.ProcessorCount, MaximumCpuWorkers))
                : 0;
            RunState state = new RunState();
            state.CpuWorkers = cpuWorkers;
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> workers = new List<Task>();

            using (CancellationTokenSource runCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                CancellationToken token = runCancellation.Token;
                if (IncludesCpu(options.Mode))
                {
                    for (int workerIndex = 0; workerIndex < cpuWorkers; workerIndex++)
                    {
                        workers.Add(StartWorker(delegate
                        {
                            RunCpuWorker(state, token);
                        }, runCancellation, state));
                    }
                }

                if (IncludesMemory(options.Mode))
                {
                    workers.Add(StartWorker(delegate
                    {
                        RunMemoryWorker(state, token);
                    }, runCancellation, state));
                }

                Task allWorkers = Task.WhenAll(workers.ToArray());
                Task reporter = Task.Factory.StartNew(delegate
                {
                    ReportProgress(state, stopwatch, progress, token);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task durationTimer = Task.Delay(options.Duration, token);

                Task firstCompleted = await Task.WhenAny(allWorkers, durationTimer).ConfigureAwait(false);
                if (firstCompleted == durationTimer && !token.IsCancellationRequested)
                    runCancellation.Cancel();

                await allWorkers.ConfigureAwait(false);
                runCancellation.Cancel();
                await reporter.ConfigureAwait(false);
            }

            stopwatch.Stop();
            string errorMessage;
            string firstMemoryError;
            lock (state.SyncRoot)
            {
                errorMessage = state.ErrorMessage;
                firstMemoryError = state.FirstMemoryError;
            }

            return new StressTestResult
            {
                Mode = options.Mode,
                Duration = stopwatch.Elapsed,
                Cancelled = cancellationToken.IsCancellationRequested,
                CpuWorkers = state.CpuWorkers,
                CpuBlocksCompleted = Interlocked.Read(ref state.CpuBlocksCompleted),
                CpuErrors = Interlocked.Read(ref state.CpuErrors),
                MemoryMegabytes = state.MemoryMegabytes,
                MemoryPassesCompleted = Interlocked.Read(ref state.MemoryPassesCompleted),
                MemoryErrors = Interlocked.Read(ref state.MemoryErrors),
                FirstMemoryError = firstMemoryError,
                ErrorMessage = errorMessage
            };
        }

        /// <summary>Deterministic CPU workload block, exposed to the test runner for verification.</summary>
        public static uint ComputeCpuCheckValue(uint seed, int iterations)
        {
            if (iterations < 0)
                throw new ArgumentOutOfRangeException("iterations");

            unchecked
            {
                uint value = seed;
                for (int i = 0; i < iterations; i++)
                {
                    value ^= value << 13;
                    value ^= value >> 17;
                    value ^= value << 5;
                    value = value * 0x85EBCA6BU + 0xC2B2AE35U;
                }
                return value;
            }
        }

        /// <summary>
        /// Leaves most currently available RAM to Windows and other applications. The test
        /// reserves at most 25 percent of free physical memory and applies a process-size cap.
        /// </summary>
        public static int CalculateMemoryTargetMegabytes(ulong availableBytes, int maximumMegabytes)
        {
            if (maximumMegabytes < 0)
                throw new ArgumentOutOfRangeException("maximumMegabytes");

            ulong minimumBytes = (ulong)MinimumMemoryTestMegabytes * 1024UL * 1024UL;
            if (availableBytes < minimumBytes * 4UL || maximumMegabytes < MinimumMemoryTestMegabytes)
                return 0;

            ulong quarterMegabytes = availableBytes / (4UL * 1024UL * 1024UL);
            ulong targetMegabytes = Math.Min(quarterMegabytes, (ulong)maximumMegabytes);
            targetMegabytes = targetMegabytes / 16UL * 16UL;
            return targetMegabytes < MinimumMemoryTestMegabytes ? 0 : (int)targetMegabytes;
        }

        private static void ValidateOptions(StressTestOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            if (!Enum.IsDefined(typeof(StressTestMode), options.Mode))
                throw new ArgumentOutOfRangeException("options.Mode");
            if (options.Duration < TimeSpan.FromMinutes(1) || options.Duration > TimeSpan.FromMinutes(15))
                throw new ArgumentOutOfRangeException("options.Duration", "Продолжительность теста должна быть от 1 до 15 минут.");
        }

        private static bool IncludesCpu(StressTestMode mode)
        {
            return mode == StressTestMode.Cpu || mode == StressTestMode.CpuAndMemory;
        }

        private static bool IncludesMemory(StressTestMode mode)
        {
            return mode == StressTestMode.Memory || mode == StressTestMode.CpuAndMemory;
        }

        private static Task StartWorker(Action work, CancellationTokenSource cancellation, RunState state)
        {
            return Task.Factory.StartNew(delegate
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    lock (state.SyncRoot)
                    {
                        if (String.IsNullOrWhiteSpace(state.ErrorMessage))
                            state.ErrorMessage = ex.Message;
                    }
                    try { cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void RunCpuWorker(RunState state, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                uint value = ComputeCpuCheckValue(CpuCheckSeed, CpuBlockIterations);
                if (value != ExpectedCpuCheckValue)
                    Interlocked.Increment(ref state.CpuErrors);
                Interlocked.Increment(ref state.CpuBlocksCompleted);
            }
        }

        private static void RunMemoryWorker(RunState state, CancellationToken cancellationToken)
        {
            ulong availableBytes = GetAvailablePhysicalMemory();
            int maximumMegabytes = Environment.Is64BitProcess
                ? MaximumMemoryTestMegabytes64Bit
                : MaximumMemoryTestMegabytes32Bit;
            int targetMegabytes = CalculateMemoryTargetMegabytes(availableBytes, maximumMegabytes);
            if (targetMegabytes == 0)
                throw new InvalidOperationException("Для безопасной проверки памяти требуется не менее 256 МБ свободной ОЗУ. Закройте ненужные программы и повторите тест.");

            state.MemoryMegabytes = targetMegabytes;
            int totalBytes = checked(targetMegabytes * 1024 * 1024);
            int chunkCount = (totalBytes + MemoryChunkBytes - 1) / MemoryChunkBytes;
            byte[][] buffers = new byte[chunkCount][];
            for (int i = 0; i < buffers.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                buffers[i] = new byte[Math.Min(MemoryChunkBytes, totalBytes - i * MemoryChunkBytes)];
            }

            int pass = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                for (int blockIndex = 0; blockIndex < buffers.Length; blockIndex++)
                {
                    if (!WriteMemoryPattern(buffers[blockIndex], blockIndex * MemoryChunkBytes, pass, cancellationToken))
                        return;
                }

                for (int blockIndex = 0; blockIndex < buffers.Length; blockIndex++)
                {
                    if (!VerifyMemoryPattern(buffers[blockIndex], blockIndex * MemoryChunkBytes, pass, state, cancellationToken))
                        return;
                }

                Interlocked.Increment(ref state.MemoryPassesCompleted);
                pass++;
            }
        }

        private static bool WriteMemoryPattern(byte[] buffer, int baseOffset, int pass,
            CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0xfffff) == 0 && cancellationToken.IsCancellationRequested)
                    return false;
                buffer[offset] = ExpectedMemoryByte(baseOffset + offset, pass);
            }
            return true;
        }

        private static bool VerifyMemoryPattern(byte[] buffer, int baseOffset, int pass,
            RunState state, CancellationToken cancellationToken)
        {
            long mismatches = 0;
            int firstMismatchOffset = -1;
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0xfffff) == 0 && cancellationToken.IsCancellationRequested)
                    return false;
                if (buffer[offset] != ExpectedMemoryByte(baseOffset + offset, pass))
                {
                    mismatches++;
                    if (firstMismatchOffset < 0)
                        firstMismatchOffset = baseOffset + offset;
                }
            }

            if (mismatches > 0)
            {
                Interlocked.Add(ref state.MemoryErrors, mismatches);
                lock (state.SyncRoot)
                {
                    if (String.IsNullOrWhiteSpace(state.FirstMemoryError))
                        state.FirstMemoryError = "Смещение в тестовом буфере: 0x" + firstMismatchOffset.ToString("X");
                }
            }
            return true;
        }

        private static byte ExpectedMemoryByte(int absoluteOffset, int pass)
        {
            return MemoryPatterns[(absoluteOffset + pass) & 7];
        }

        private static void ReportProgress(RunState state, Stopwatch stopwatch,
            IProgress<StressTestProgress> progress, CancellationToken cancellationToken)
        {
            if (progress == null)
                return;

            while (!cancellationToken.IsCancellationRequested)
            {
                try { progress.Report(CreateProgress(state, stopwatch)); }
                catch { }
                Thread.Sleep(1000);
            }
            try { progress.Report(CreateProgress(state, stopwatch)); }
            catch { }
        }

        private static StressTestProgress CreateProgress(RunState state, Stopwatch stopwatch)
        {
            return new StressTestProgress
            {
                Elapsed = stopwatch.Elapsed,
                CpuBlocksCompleted = Interlocked.Read(ref state.CpuBlocksCompleted),
                CpuErrors = Interlocked.Read(ref state.CpuErrors),
                CpuWorkers = state.CpuWorkers,
                MemoryMegabytes = state.MemoryMegabytes,
                MemoryPassesCompleted = Interlocked.Read(ref state.MemoryPassesCompleted),
                MemoryErrors = Interlocked.Read(ref state.MemoryErrors)
            };
        }

        private static ulong GetAvailablePhysicalMemory()
        {
            MemoryStatusEx status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status))
                throw new InvalidOperationException("Windows не смогла определить объём свободной оперативной памяти.");
            return status.AvailablePhysical;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        private sealed class RunState
        {
            public readonly object SyncRoot = new object();
            public int CpuWorkers;
            public int MemoryMegabytes;
            public long CpuBlocksCompleted;
            public long CpuErrors;
            public long MemoryPassesCompleted;
            public long MemoryErrors;
            public string FirstMemoryError;
            public string ErrorMessage;
        }
    }
}
