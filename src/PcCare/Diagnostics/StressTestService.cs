using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PcCare.Diagnostics
{
    [Flags]
    public enum StressTestMode
    {
        None = 0,
        Cpu = 1,
        Fpu = 2,
        Cache = 4,
        Memory = 8
    }

    public sealed class StressTestOptions
    {
        public StressTestMode Mode { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public sealed class StressTestProgress
    {
        public StressTestMode Mode { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public int ComputeWorkers { get; internal set; }
        public long CpuBlocksCompleted { get; internal set; }
        public long CpuErrors { get; internal set; }
        public long FpuBlocksCompleted { get; internal set; }
        public long FpuErrors { get; internal set; }
        public long CachePassesCompleted { get; internal set; }
        public long CacheErrors { get; internal set; }
        public int MemoryMegabytes { get; internal set; }
        public long MemoryPassesCompleted { get; internal set; }
        public long MemoryErrors { get; internal set; }
    }

    public sealed class StressTestResult
    {
        public StressTestMode Mode { get; internal set; }
        public TimeSpan Duration { get; internal set; }
        public bool Cancelled { get; internal set; }
        public int ComputeWorkers { get; internal set; }
        public long CpuBlocksCompleted { get; internal set; }
        public long CpuErrors { get; internal set; }
        public long FpuBlocksCompleted { get; internal set; }
        public long FpuErrors { get; internal set; }
        public long CachePassesCompleted { get; internal set; }
        public long CacheErrors { get; internal set; }
        public int MemoryMegabytes { get; internal set; }
        public long MemoryPassesCompleted { get; internal set; }
        public long MemoryErrors { get; internal set; }
        public string FirstMemoryError { get; internal set; }
        public string ErrorMessage { get; internal set; }

        public bool HasErrors
        {
            get
            {
                return CpuErrors > 0 || FpuErrors > 0 || CacheErrors > 0 || MemoryErrors > 0 ||
                    !String.IsNullOrWhiteSpace(ErrorMessage);
            }
        }
    }

    /// <summary>
    /// User-initiated, bounded CPU integer/FPU/cache and RAM stability checks. This does
    /// not test GPU/VRAM, disks, power delivery or physical memory modules in full, and
    /// never runs automatically.
    /// </summary>
    public sealed class StressTestService
    {
        private const int CpuBlockIterations = 65536;
        private const int FpuBlockIterations = 8192;
        private const uint CpuCheckSeed = 0xA5C39E17U;
        private const uint ExpectedCpuCheckValue = 0xD8572465U;
        private const double ExpectedFpuCheckValue = 2758.280819831906;
        private const int CacheWorkingSetBytes = 32 * 1024;
        private const int MemoryChunkBytes = 16 * 1024 * 1024;
        private const int MinimumMemoryTestMegabytes = 64;
        private const int MaximumMemoryTestMegabytes64Bit = 1024;
        private const int MaximumMemoryTestMegabytes32Bit = 512;
        private const int MaximumComputeWorkers = 64;

        private static readonly byte[] MemoryPatterns = { 0x00, 0xff, 0xaa, 0x55, 0xcc, 0x33, 0xf0, 0x0f };

        public async Task<StressTestResult> RunAsync(StressTestOptions options,
            IProgress<StressTestProgress> progress, CancellationToken cancellationToken)
        {
            ValidateOptions(options);

            int computeWorkers = IncludesCompute(options.Mode)
                ? Math.Max(1, Math.Min(Environment.ProcessorCount, MaximumComputeWorkers))
                : 0;
            RunState state = new RunState();
            state.Mode = options.Mode;
            state.ComputeWorkers = computeWorkers;
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> workers = new List<Task>();

            using (CancellationTokenSource runCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                CancellationToken token = runCancellation.Token;
                if (IncludesCompute(options.Mode))
                {
                    for (int workerIndex = 0; workerIndex < computeWorkers; workerIndex++)
                    {
                        workers.Add(StartWorker(delegate
                        {
                            RunComputeWorker(state, options.Mode, token);
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
                ComputeWorkers = state.ComputeWorkers,
                CpuBlocksCompleted = Interlocked.Read(ref state.CpuBlocksCompleted),
                CpuErrors = Interlocked.Read(ref state.CpuErrors),
                FpuBlocksCompleted = Interlocked.Read(ref state.FpuBlocksCompleted),
                FpuErrors = Interlocked.Read(ref state.FpuErrors),
                CachePassesCompleted = Interlocked.Read(ref state.CachePassesCompleted),
                CacheErrors = Interlocked.Read(ref state.CacheErrors),
                MemoryMegabytes = state.MemoryMegabytes,
                MemoryPassesCompleted = Interlocked.Read(ref state.MemoryPassesCompleted),
                MemoryErrors = Interlocked.Read(ref state.MemoryErrors),
                FirstMemoryError = firstMemoryError,
                ErrorMessage = errorMessage
            };
        }

        /// <summary>Deterministic CPU integer workload block, exposed for verification by tests.</summary>
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

        /// <summary>Deterministic floating-point workload block, exposed for verification by tests.</summary>
        public static double ComputeFpuCheckValue(int iterations)
        {
            if (iterations < 0)
                throw new ArgumentOutOfRangeException("iterations");

            double value = 0.125;
            for (int i = 0; i < iterations; i++)
            {
                double operand = (i % 97 + 1) / 97.0;
                value += operand * operand;
                value -= operand / (i + 2.0);
            }
            return value;
        }

        /// <summary>Writes a deterministic working-set pattern for cache-check tests.</summary>
        public static void FillCacheTestPattern(byte[] buffer, int pass)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = ExpectedCacheByte(i, pass);
        }

        /// <summary>Counts changed bytes in a cache-test working set.</summary>
        public static long CountCacheTestMismatches(byte[] buffer, int pass)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            long mismatches = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != ExpectedCacheByte(i, pass))
                    mismatches++;
            }
            return mismatches;
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

        public static bool IsValidModeSelection(StressTestMode mode)
        {
            const StressTestMode supportedModes = StressTestMode.Cpu | StressTestMode.Fpu |
                StressTestMode.Cache | StressTestMode.Memory;
            int unknownFlags = (int)mode & ~(int)supportedModes;
            return mode != StressTestMode.None && unknownFlags == 0;
        }

        private static void ValidateOptions(StressTestOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            if (!IsValidModeSelection(options.Mode))
                throw new ArgumentOutOfRangeException("options.Mode", "Выберите хотя бы один поддерживаемый компонент теста.");
            if (options.Duration < TimeSpan.FromMinutes(1) || options.Duration > TimeSpan.FromMinutes(15))
                throw new ArgumentOutOfRangeException("options.Duration", "Продолжительность теста должна быть от 1 до 15 минут.");
        }

        private static bool IncludesCompute(StressTestMode mode)
        {
            return (mode & (StressTestMode.Cpu | StressTestMode.Fpu | StressTestMode.Cache)) != 0;
        }

        private static bool IncludesMemory(StressTestMode mode)
        {
            return (mode & StressTestMode.Memory) != 0;
        }

        private static bool Includes(StressTestMode mode, StressTestMode workload)
        {
            return (mode & workload) != 0;
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

        private static void RunComputeWorker(RunState state, StressTestMode mode,
            CancellationToken cancellationToken)
        {
            byte[] cacheBuffer = Includes(mode, StressTestMode.Cache)
                ? new byte[CacheWorkingSetBytes] : null;
            int cachePass = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (Includes(mode, StressTestMode.Cpu))
                {
                    uint value = ComputeCpuCheckValue(CpuCheckSeed, CpuBlockIterations);
                    if (value != ExpectedCpuCheckValue)
                        Interlocked.Increment(ref state.CpuErrors);
                    Interlocked.Increment(ref state.CpuBlocksCompleted);
                }

                if (Includes(mode, StressTestMode.Fpu))
                {
                    double value = ComputeFpuCheckValue(FpuBlockIterations);
                    if (Double.IsNaN(value) || Double.IsInfinity(value) ||
                        Math.Abs(value - ExpectedFpuCheckValue) > 0.00000001)
                        Interlocked.Increment(ref state.FpuErrors);
                    Interlocked.Increment(ref state.FpuBlocksCompleted);
                }

                if (cacheBuffer != null)
                {
                    if (!RunCachePass(cacheBuffer, cachePass, state, cancellationToken))
                        return;
                    cachePass++;
                }
            }
        }

        private static bool RunCachePass(byte[] buffer, int pass, RunState state,
            CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0x3ff) == 0 && cancellationToken.IsCancellationRequested)
                    return false;
                buffer[offset] = ExpectedCacheByte(offset, pass);
            }

            long mismatches = 0;
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0x3ff) == 0 && cancellationToken.IsCancellationRequested)
                    return false;
                if (buffer[offset] != ExpectedCacheByte(offset, pass))
                    mismatches++;
            }

            if (mismatches > 0)
                Interlocked.Add(ref state.CacheErrors, mismatches);
            Interlocked.Increment(ref state.CachePassesCompleted);
            return true;
        }

        private static byte ExpectedCacheByte(int offset, int pass)
        {
            unchecked
            {
                return (byte)(offset * 31 + pass * 17 + (offset >> 8));
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
                Mode = state.Mode,
                Elapsed = stopwatch.Elapsed,
                ComputeWorkers = state.ComputeWorkers,
                CpuBlocksCompleted = Interlocked.Read(ref state.CpuBlocksCompleted),
                CpuErrors = Interlocked.Read(ref state.CpuErrors),
                FpuBlocksCompleted = Interlocked.Read(ref state.FpuBlocksCompleted),
                FpuErrors = Interlocked.Read(ref state.FpuErrors),
                CachePassesCompleted = Interlocked.Read(ref state.CachePassesCompleted),
                CacheErrors = Interlocked.Read(ref state.CacheErrors),
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
            public StressTestMode Mode;
            public int ComputeWorkers;
            public int MemoryMegabytes;
            public long CpuBlocksCompleted;
            public long CpuErrors;
            public long FpuBlocksCompleted;
            public long FpuErrors;
            public long CachePassesCompleted;
            public long CacheErrors;
            public long MemoryPassesCompleted;
            public long MemoryErrors;
            public string FirstMemoryError;
            public string ErrorMessage;
        }
    }
}
