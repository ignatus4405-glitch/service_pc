using System;

namespace PcCare.Diagnostics
{
    /// <summary>
    /// Calculates the visible time range for live and completed stability charts.
    /// </summary>
    public static class LiveChartWindow
    {
        public const int LiveWindowSeconds = 60;
        private const double MinimumLiveWindowSeconds = 2.0;

        public static void Calculate(DateTime firstSampleTime, DateTime lastSampleTime, bool isLive,
            out DateTime startTime, out double durationSeconds)
        {
            startTime = firstSampleTime;
            durationSeconds = (lastSampleTime - firstSampleTime).TotalSeconds;

            if (isLive)
            {
                durationSeconds = Math.Min(LiveWindowSeconds,
                    Math.Max(MinimumLiveWindowSeconds, durationSeconds));
                startTime = lastSampleTime.AddSeconds(-durationSeconds);
            }
            else if (durationSeconds <= 0.0)
            {
                durationSeconds = 1.0;
            }
        }
    }
}
