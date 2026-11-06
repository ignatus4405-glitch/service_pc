using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using PcCare.Diagnostics;

namespace PcCare
{
    /// <summary>
    /// Lightweight WinForms time-series view for the live CPU, memory and temperature
    /// readings collected during a user-started stability test.
    /// </summary>
    public sealed class LiveSensorChartPanel : Panel
    {
        private const int MaximumSamples = 900;
        private const int MaximumTemperatureSeries = 5;
        private static readonly Color TextColor = Color.FromArgb(31, 43, 62);
        private static readonly Color MutedColor = Color.FromArgb(102, 116, 137);
        private static readonly Color GridColor = Color.FromArgb(226, 232, 240);
        private static readonly Color CpuColor = Color.FromArgb(55, 111, 226);
        private static readonly Color MemoryColor = Color.FromArgb(29, 133, 93);
        private static readonly Color[] TemperatureColors =
        {
            Color.FromArgb(190, 62, 72),
            Color.FromArgb(194, 111, 23),
            Color.FromArgb(112, 82, 173),
            Color.FromArgb(0, 137, 145),
            Color.FromArgb(86, 101, 115)
        };

        private readonly List<ChartSample> _samples = new List<ChartSample>();
        private bool _isLive;

        public LiveSensorChartPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Height = 370;
            MinimumSize = new Size(320, 330);
            Margin = new Padding(0, 0, 0, 12);
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        }

        public void ClearSamples()
        {
            _samples.Clear();
            Invalidate();
        }

        public void SetHistory(IList<LiveSensorSnapshot> history)
        {
            _samples.Clear();
            if (history != null)
            {
                int first = Math.Max(0, history.Count - MaximumSamples);
                for (int i = first; i < history.Count; i++)
                    AddSampleCore(history[i]);
            }
            Invalidate();
        }

        public void AddSample(LiveSensorSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            AddSampleCore(snapshot);
            Invalidate();
        }

        public void SetLive(bool isLive)
        {
            if (_isLive == isLive)
                return;
            _isLive = isLive;
            Invalidate();
        }

        private void AddSampleCore(LiveSensorSnapshot snapshot)
        {
            ChartSample sample = new ChartSample();
            sample.Timestamp = snapshot.CollectedAt;
            if (snapshot.CpuLoadPercent.HasValue)
                sample.CpuLoadPercent = Math.Max(0, Math.Min(100, snapshot.CpuLoadPercent.Value));
            if (snapshot.TotalMemoryBytes.HasValue && snapshot.TotalMemoryBytes.Value > 0 &&
                snapshot.AvailableMemoryBytes.HasValue)
            {
                double available = (double)snapshot.AvailableMemoryBytes.Value * 100.0 /
                    snapshot.TotalMemoryBytes.Value;
                sample.AvailableMemoryPercent = Math.Max(0.0, Math.Min(100.0, available));
            }

            foreach (TemperatureSensorReading reading in snapshot.TemperatureSensors)
            {
                if (reading == null || Double.IsNaN(reading.TemperatureCelsius) ||
                    Double.IsInfinity(reading.TemperatureCelsius))
                    continue;
                string source = String.IsNullOrWhiteSpace(reading.Source) ? "Источник не указан" : reading.Source;
                string name = String.IsNullOrWhiteSpace(reading.Name) ? "датчик" : reading.Name;
                string seriesName = (reading.IsCpuSensor ? "CPU · " : "ACPI/прочее · ") + source + " · " + name;
                sample.Temperatures[seriesName] = reading.TemperatureCelsius;
            }

            _samples.Add(sample);
            if (_samples.Count > MaximumSamples)
                _samples.RemoveAt(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(BackColor);

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int top = 8;
            int rowHeight = Math.Max(88, (height - 18) / 3);
            DrawMetric(graphics, new Rectangle(0, top, width, rowHeight), "Загрузка процессора", Metric.Cpu);
            top += rowHeight;
            DrawMetric(graphics, new Rectangle(0, top, width, rowHeight), "Свободная оперативная память", Metric.Memory);
            top += rowHeight;
            DrawMetric(graphics, new Rectangle(0, top, width, Math.Max(88, height - top)), "Температура датчиков", Metric.Temperature);
        }

        private void DrawMetric(Graphics graphics, Rectangle row, string title, Metric metric)
        {
            using (Font titleFont = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold))
            using (Font axisFont = new Font("Segoe UI", 7.5F, FontStyle.Regular))
            using (Pen gridPen = new Pen(GridColor))
            using (Pen axisPen = new Pen(Color.FromArgb(185, 195, 208)))
            {
                string displayTitle = _isLive
                    ? title + " · прокрутка окна до " + LiveChartWindow.LiveWindowSeconds + " с"
                    : title;
                Rectangle titleBounds = new Rectangle(12, row.Top + 1, Math.Max(80, ClientSize.Width - 24), 18);
                TextRenderer.DrawText(graphics, displayTitle, titleFont, titleBounds, TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                int legendY = row.Top + 19;
                int plotTop = row.Top + 40;
                int plotBottom = row.Bottom - 16;
                int plotLeft = 58;
                int plotRight = ClientSize.Width - 14;
                if (plotRight <= plotLeft + 10 || plotBottom <= plotTop + 10)
                    return;

                double minValue = metric == Metric.Temperature ? -10.0 : 0.0;
                double maxValue = metric == Metric.Temperature ? 130.0 : 100.0;
                DrawYAxisLabel(graphics, axisFont, metric, maxValue, plotLeft - 54, plotTop - 8);
                DrawYAxisLabel(graphics, axisFont, metric, (minValue + maxValue) / 2.0, plotLeft - 54,
                    (plotTop + plotBottom) / 2 - 7);
                DrawYAxisLabel(graphics, axisFont, metric, minValue, plotLeft - 54, plotBottom - 8);

                for (int grid = 0; grid <= 2; grid++)
                {
                    int y = plotTop + (plotBottom - plotTop) * grid / 2;
                    graphics.DrawLine(grid == 2 ? axisPen : gridPen, plotLeft, y, plotRight, y);
                }
                graphics.DrawLine(axisPen, plotLeft, plotTop, plotLeft, plotBottom);

                if (_samples.Count == 0)
                {
                    string empty = metric == Metric.Temperature
                        ? "Нет доступных показаний температуры. Это неизвестное значение, не 0 °C."
                        : "График появится после первого опроса во время теста.";
                    Rectangle emptyBounds = new Rectangle(plotLeft + 8, plotTop + 4,
                        Math.Max(40, plotRight - plotLeft - 16), Math.Max(18, plotBottom - plotTop - 8));
                    TextRenderer.DrawText(graphics, empty, axisFont, emptyBounds, MutedColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }

                List<string> temperatureSeries = metric == Metric.Temperature ? GetTemperatureSeriesNames() : null;
                if (metric == Metric.Cpu)
                    DrawLegendItem(graphics, axisFont, CpuColor, "WMI PerfOS", 12, legendY, ClientSize.Width - 24);
                else if (metric == Metric.Memory)
                    DrawLegendItem(graphics, axisFont, MemoryColor, "WMI Windows · % доступно", 12, legendY, ClientSize.Width - 24);
                else
                    DrawTemperatureLegend(graphics, axisFont, temperatureSeries, legendY);

                DateTime firstSampleTime = _samples[0].Timestamp;
                DateTime lastTime = _samples[_samples.Count - 1].Timestamp;
                DateTime plotStart;
                double totalSeconds;
                LiveChartWindow.Calculate(firstSampleTime, lastTime, _isLive,
                    out plotStart, out totalSeconds);

                if (metric == Metric.Cpu)
                    DrawSingleSeries(graphics, CpuColor, plotLeft, plotRight, plotTop, plotBottom,
                        minValue, maxValue, plotStart, totalSeconds, delegate(ChartSample sample) { return sample.CpuLoadPercent; });
                else if (metric == Metric.Memory)
                    DrawSingleSeries(graphics, MemoryColor, plotLeft, plotRight, plotTop, plotBottom,
                        minValue, maxValue, plotStart, totalSeconds, delegate(ChartSample sample) { return sample.AvailableMemoryPercent; });
                else
                {
                    int count = Math.Min(MaximumTemperatureSeries, temperatureSeries.Count);
                    for (int i = 0; i < count; i++)
                    {
                        string seriesName = temperatureSeries[i];
                        Color color = TemperatureColors[i % TemperatureColors.Length];
                        DrawTemperatureSeries(graphics, color, seriesName, plotLeft, plotRight, plotTop, plotBottom,
                            minValue, maxValue, plotStart, totalSeconds);
                    }
                    if (temperatureSeries.Count == 0)
                    {
                        Rectangle noTempBounds = new Rectangle(plotLeft + 6, plotTop + 3,
                            Math.Max(40, plotRight - plotLeft - 12), Math.Max(18, plotBottom - plotTop - 6));
                        TextRenderer.DrawText(graphics,
                            "Температурные показания не передаются. Запустите совместимый монитор датчиков (например, LibreHardwareMonitor); PC Care не устанавливает и не запускает его.",
                            axisFont, noTempBounds, MutedColor,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    }
                }

                string firstLabel = plotStart.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                string lastLabel = lastTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                TextRenderer.DrawText(graphics, firstLabel, axisFont,
                    new Rectangle(plotLeft, row.Bottom - 15, 70, 14), MutedColor, TextFormatFlags.Left);
                TextRenderer.DrawText(graphics, lastLabel, axisFont,
                    new Rectangle(Math.Max(plotLeft, plotRight - 70), row.Bottom - 15, 70, 14), MutedColor, TextFormatFlags.Right);
            }
        }

        private void DrawTemperatureLegend(Graphics graphics, Font font, IList<string> names, int y)
        {
            if (names.Count == 0)
            {
                TextRenderer.DrawText(graphics, "CPU-датчик не найден; доступны только явно подписанные источники",
                    font, new Rectangle(12, y, Math.Max(80, ClientSize.Width - 24), 18), MutedColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            int x = 12;
            int maxX = ClientSize.Width - 12;
            int count = Math.Min(MaximumTemperatureSeries, names.Count);
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                string label = names[i];
                int remaining = maxX - x - 22;
                int labelWidth = Math.Min(180, remaining);
                if (labelWidth < 45)
                    break;
                using (Pen colorPen = new Pen(TemperatureColors[i % TemperatureColors.Length], 2F))
                    graphics.DrawLine(colorPen, x, y + 9, x + 12, y + 9);
                TextRenderer.DrawText(graphics, label, font,
                    new Rectangle(x + 16, y, labelWidth, 18), MutedColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += labelWidth + 28;
                shown++;
            }
            if (names.Count > shown)
            {
                int moreX = Math.Min(x, Math.Max(12, maxX - 42));
                TextRenderer.DrawText(graphics, "+" + (names.Count - shown), font,
                    new Rectangle(moreX, y, 42, 18), MutedColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        private static void DrawLegendItem(Graphics graphics, Font font, Color color, string label, int x, int y, int width)
        {
            using (Pen pen = new Pen(color, 2F))
                graphics.DrawLine(pen, x, y + 9, x + 12, y + 9);
            TextRenderer.DrawText(graphics, label, font,
                new Rectangle(x + 16, y, Math.Max(40, width - 18), 18), MutedColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static void DrawYAxisLabel(Graphics graphics, Font font, Metric metric, double value, int x, int y)
        {
            string suffix = metric == Metric.Temperature ? " °C" : "%";
            string label = value.ToString("N0", CultureInfo.CurrentCulture) + suffix;
            TextRenderer.DrawText(graphics, label, font, new Rectangle(x, y, 54, 16), MutedColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private void DrawSingleSeries(Graphics graphics, Color color, int left, int right, int top, int bottom,
            double min, double max, DateTime first, double totalSeconds, Func<ChartSample, double?> valueSelector)
        {
            PointF? previous = null;
            DateTime visibleEnd = first.AddSeconds(totalSeconds);
            using (Pen pen = new Pen(color, 2F))
            using (SolidBrush brush = new SolidBrush(color))
            {
                foreach (ChartSample sample in _samples)
                {
                    if (sample.Timestamp < first || sample.Timestamp > visibleEnd)
                    {
                        previous = null;
                        continue;
                    }
                    double? value = valueSelector(sample);
                    if (!value.HasValue)
                    {
                        previous = null;
                        continue;
                    }
                    PointF point = GetPoint(sample.Timestamp, value.Value, left, right, top, bottom,
                        min, max, first, totalSeconds);
                    if (previous.HasValue)
                        graphics.DrawLine(pen, previous.Value, point);
                    graphics.FillEllipse(brush, point.X - 2.0F, point.Y - 2.0F, 4F, 4F);
                    previous = point;
                }
            }
        }

        private void DrawTemperatureSeries(Graphics graphics, Color color, string seriesName, int left, int right,
            int top, int bottom, double min, double max, DateTime first, double totalSeconds)
        {
            PointF? previous = null;
            DateTime visibleEnd = first.AddSeconds(totalSeconds);
            using (Pen pen = new Pen(color, 2F))
            using (SolidBrush brush = new SolidBrush(color))
            {
                foreach (ChartSample sample in _samples)
                {
                    if (sample.Timestamp < first || sample.Timestamp > visibleEnd)
                    {
                        previous = null;
                        continue;
                    }
                    double value;
                    if (!sample.Temperatures.TryGetValue(seriesName, out value))
                    {
                        previous = null;
                        continue;
                    }
                    PointF point = GetPoint(sample.Timestamp, value, left, right, top, bottom,
                        min, max, first, totalSeconds);
                    if (previous.HasValue)
                        graphics.DrawLine(pen, previous.Value, point);
                    graphics.FillEllipse(brush, point.X - 2.0F, point.Y - 2.0F, 4F, 4F);
                    previous = point;
                }
            }
        }

        private static PointF GetPoint(DateTime timestamp, double value, int left, int right, int top,
            int bottom, double min, double max, DateTime first, double totalSeconds)
        {
            double elapsed = (timestamp - first).TotalSeconds;
            double xRatio = Math.Max(0.0, Math.Min(1.0, elapsed / totalSeconds));
            double yRatio = Math.Max(0.0, Math.Min(1.0, (value - min) / (max - min)));
            float x = left + (float)(right - left) * (float)xRatio;
            float y = bottom - (float)(bottom - top) * (float)yRatio;
            return new PointF(x, y);
        }

        private List<string> GetTemperatureSeriesNames()
        {
            List<string> names = new List<string>();
            foreach (ChartSample sample in _samples)
            {
                foreach (string name in sample.Temperatures.Keys)
                {
                    if (!names.Contains(name))
                        names.Add(name);
                }
            }

            names.Sort(delegate(string left, string right)
            {
                bool leftCpu = left.StartsWith("CPU ·", StringComparison.Ordinal);
                bool rightCpu = right.StartsWith("CPU ·", StringComparison.Ordinal);
                if (leftCpu != rightCpu)
                    return leftCpu ? -1 : 1;
                return String.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
            });
            return names;
        }

        private enum Metric
        {
            Cpu,
            Memory,
            Temperature
        }

        private sealed class ChartSample
        {
            public ChartSample()
            {
                Temperatures = new Dictionary<string, double>();
            }

            public DateTime Timestamp { get; set; }
            public double? CpuLoadPercent { get; set; }
            public double? AvailableMemoryPercent { get; set; }
            public Dictionary<string, double> Temperatures { get; private set; }
        }
    }
}
