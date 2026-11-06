using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcCare.Diagnostics;
using PcCare.Models;
using PcCare.Updates;

namespace PcCare
{
    public sealed class MainForm : Form
    {
        private static readonly Color SidebarColor = Color.FromArgb(22, 32, 50);
        private static readonly Color PageColor = Color.FromArgb(244, 247, 251);
        private static readonly Color TextColor = Color.FromArgb(31, 43, 62);
        private static readonly Color MutedColor = Color.FromArgb(102, 116, 137);
        private static readonly Color AccentColor = Color.FromArgb(55, 111, 226);
        private static readonly Color GreenColor = Color.FromArgb(29, 133, 93);
        private static readonly Color OrangeColor = Color.FromArgb(194, 111, 23);
        private static readonly Color RedColor = Color.FromArgb(190, 62, 72);
        private static readonly Font NavigationRegularFont = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        private static readonly Font NavigationBoldFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        private const string SensorProviderReleaseUrl = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases";

        private readonly DiagnosticService _diagnosticService = new DiagnosticService();
        private readonly RecommendationEngine _recommendationEngine = new RecommendationEngine();
        private readonly CleanupService _cleanupService = new CleanupService();
        private readonly StressTestService _stressTestService = new StressTestService();
        private readonly LiveSensorService _liveSensorService = new LiveSensorService();
        private readonly DiskSurfaceTestService _diskSurfaceTestService = new DiskSurfaceTestService();
        private readonly UpdateService _updateService = new UpdateService();
        private readonly Dictionary<string, Button> _navigationButtons = new Dictionary<string, Button>();
        private readonly List<Button> _scanButtons = new List<Button>();

        private SystemSnapshot _snapshot;
        private IList<Recommendation> _recommendations = new List<Recommendation>();
        private CleanupResult _lastCleanupResult;
        private Panel _pageHost;
        private Panel _sidebar;
        private FlowLayoutPanel _pageFlow;
        private Label _pageTitle;
        private Label _pageSubtitle;
        private Label _updateStatusLabel;
        private Button _updateCheckButton;
        private CheckedListBox _stressModeSelector;
        private ComboBox _stressDurationSelector;
        private Label _stressStatusLabel;
        private Button _stressStartButton;
        private Button _stressStopButton;
        private Label _liveSensorStatusLabel;
        private ListView _liveSensorValuesList;
        private LiveSensorChartPanel _sensorChart;
        private readonly List<LiveSensorSnapshot> _sensorHistory = new List<LiveSensorSnapshot>();
        private ComboBox _diskSelector;
        private Label _diskScanStatusLabel;
        private Button _diskScanStartButton;
        private Button _diskScanStopButton;
        private CancellationTokenSource _stressCancellation;
        private CancellationTokenSource _diskScanCancellation;
        private StressTestResult _lastStressTestResult;
        private StressTestMode _selectedStressMode = StressTestMode.Cpu;
        private DiskScanResult _lastDiskScanResult;
        private LiveSensorSnapshot _latestSensorSnapshot;
        private string _stressProgressText = "Нагрузочный тест ещё не запускался.";
        private string _diskScanProgressText = "Проверка поверхности диска ещё не запускалась.";
        private string _currentPage = "overview";
        private bool _isScanning;
        private bool _isCleaning;
        private bool _isCheckingUpdate;
        private bool _isStressTestRunning;
        private bool _isDiskScanRunning;

        public MainForm()
        {
            Text = "PC Care — диагностика и обслуживание Windows";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 660);
            Size = new Size(1220, 820);
            BackColor = PageColor;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            BuildShell();
            ShowPage("overview");
            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;
        }

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            await RunDiagnosticsAsync();
            if (!IsDisposed && !Disposing)
                await CheckForUpdatesAsync(true);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isStressTestRunning && !_isDiskScanRunning)
                return;

            DialogResult answer = MessageBox.Show(this,
                "Проверка оборудования ещё выполняется. Остановить её и закрыть PC Care?",
                "Тест компьютера", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            if (_stressCancellation != null)
                _stressCancellation.Cancel();
            if (_diskScanCancellation != null)
                _diskScanCancellation.Cancel();
        }

        private void BuildShell()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = new Padding(0);
            layout.Padding = new Padding(0);
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(layout);

            _sidebar = BuildSidebar();
            layout.Controls.Add(_sidebar, 0, 0);

            Panel mainPanel = new Panel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.BackColor = PageColor;
            layout.Controls.Add(mainPanel, 1, 0);

            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 100;
            topBar.BackColor = Color.White;
            topBar.Padding = new Padding(30, 18, 24, 12);
            mainPanel.Controls.Add(topBar);

            _pageTitle = new Label();
            _pageTitle.AutoSize = false;
            _pageTitle.Location = new Point(30, 17);
            _pageTitle.Size = new Size(520, 33);
            _pageTitle.Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold);
            _pageTitle.ForeColor = TextColor;
            topBar.Controls.Add(_pageTitle);

            _pageSubtitle = new Label();
            _pageSubtitle.AutoSize = false;
            _pageSubtitle.Location = new Point(32, 54);
            _pageSubtitle.Size = new Size(690, 25);
            _pageSubtitle.Font = new Font("Segoe UI", 9F);
            _pageSubtitle.ForeColor = MutedColor;
            topBar.Controls.Add(_pageSubtitle);

            Label versionLabel = new Label();
            versionLabel.AutoSize = false;
            versionLabel.Text = "Версия " + UpdateService.FormatVersion(_updateService.CurrentVersion);
            versionLabel.TextAlign = ContentAlignment.MiddleRight;
            versionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            versionLabel.Location = new Point(0, 25);
            versionLabel.Size = new Size(170, 22);
            versionLabel.ForeColor = MutedColor;
            topBar.Controls.Add(versionLabel);
            topBar.Resize += delegate
            {
                versionLabel.Left = topBar.ClientSize.Width - versionLabel.Width - 26;
            };

            _pageHost = new Panel();
            _pageHost.Dock = DockStyle.Fill;
            _pageHost.BackColor = PageColor;
            mainPanel.Controls.Add(_pageHost);
            _pageHost.BringToFront();
            topBar.BringToFront();
            _pageHost.Resize += delegate { ResizePageCards(); };
        }

        private Panel BuildSidebar()
        {
            Panel sidebar = new Panel();
            sidebar.Dock = DockStyle.Fill;
            sidebar.BackColor = SidebarColor;
            sidebar.Padding = new Padding(0);

            Panel brand = new Panel();
            brand.Dock = DockStyle.Top;
            brand.Height = 112;
            brand.Padding = new Padding(20, 19, 12, 12);
            sidebar.Controls.Add(brand);

            Panel brandMark = new Panel();
            brandMark.BackColor = AccentColor;
            brandMark.Location = new Point(20, 22);
            brandMark.Size = new Size(44, 44);
            brand.Controls.Add(brandMark);

            Label markText = new Label();
            markText.Text = "PC";
            markText.Dock = DockStyle.Fill;
            markText.TextAlign = ContentAlignment.MiddleCenter;
            markText.ForeColor = Color.White;
            markText.Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold);
            brandMark.Controls.Add(markText);

            Label brandName = new Label();
            brandName.Text = "PC Care";
            brandName.Location = new Point(75, 21);
            brandName.Size = new Size(130, 28);
            brandName.ForeColor = Color.White;
            brandName.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold);
            brand.Controls.Add(brandName);

            Label brandTagline = new Label();
            brandTagline.Text = "Проверка и обслуживание";
            brandTagline.Location = new Point(76, 49);
            brandTagline.Size = new Size(140, 35);
            brandTagline.ForeColor = Color.FromArgb(173, 185, 203);
            brandTagline.Font = new Font("Segoe UI", 8F);
            brandTagline.AutoEllipsis = true;
            brand.Controls.Add(brandTagline);

            Label section = new Label();
            section.Text = "РАЗДЕЛЫ";
            section.Dock = DockStyle.Top;
            section.Height = 28;
            section.Padding = new Padding(22, 6, 0, 0);
            section.ForeColor = Color.FromArgb(130, 146, 169);
            section.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            sidebar.Controls.Add(section);

            FlowLayoutPanel navigation = new FlowLayoutPanel();
            navigation.Dock = DockStyle.Top;
            navigation.FlowDirection = FlowDirection.TopDown;
            navigation.WrapContents = false;
            navigation.AutoSize = true;
            navigation.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navigation.Padding = new Padding(10, 2, 8, 0);
            navigation.BackColor = SidebarColor;
            sidebar.Controls.Add(navigation);

            AddNavigationButton(navigation, "Обзор", "overview");
            AddNavigationButton(navigation, "Диагностика", "diagnostics");
            AddNavigationButton(navigation, "Тест стабильности", "stress");
            AddNavigationButton(navigation, "Диски и SMART", "drives");
            AddNavigationButton(navigation, "Предложения", "recommendations");
            AddNavigationButton(navigation, "Очистка кэша", "cleanup");
            AddNavigationButton(navigation, "Оптимизация", "optimization");
            AddNavigationButton(navigation, "Обновления", "updates");

            Label supported = new Label();
            supported.Text = "Windows 7 SP1 · 8 · 8.1 · 10 · 11";
            supported.Dock = DockStyle.Bottom;
            supported.Height = 48;
            supported.Padding = new Padding(16, 0, 10, 10);
            supported.ForeColor = Color.FromArgb(151, 165, 186);
            supported.Font = new Font("Segoe UI", 8F);
            supported.TextAlign = ContentAlignment.BottomLeft;
            sidebar.Controls.Add(supported);

            return sidebar;
        }

        private void AddNavigationButton(FlowLayoutPanel navigation, string text, string page)
        {
            Button button = new Button();
            button.Text = "   " + text;
            button.Tag = page;
            button.Size = new Size(204, 45);
            button.Margin = new Padding(0, 2, 0, 4);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 51, 74);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Font = NavigationRegularFont;
            button.ForeColor = Color.FromArgb(222, 229, 239);
            button.BackColor = SidebarColor;
            button.Cursor = Cursors.Hand;
            button.Click += delegate
            {
                bool leavingStressPage = _isStressTestRunning && !String.Equals(page, "stress", StringComparison.OrdinalIgnoreCase);
                bool leavingDiskScanPage = _isDiskScanRunning && !String.Equals(page, "drives", StringComparison.OrdinalIgnoreCase);
                if (leavingStressPage || leavingDiskScanPage)
                {
                    DialogResult stop = MessageBox.Show(this,
                        "Проверка оборудования ещё выполняется. Остановить её и перейти в другой раздел?",
                        "Тест компьютера", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (stop != DialogResult.Yes)
                        return;
                    if (leavingStressPage && _stressCancellation != null)
                        _stressCancellation.Cancel();
                    if (leavingDiskScanPage && _diskScanCancellation != null)
                        _diskScanCancellation.Cancel();
                }
                ShowPage(page);
            };
            navigation.Controls.Add(button);
            _navigationButtons[page] = button;
        }

        private void ShowPage(string page)
        {
            _currentPage = page;
            _pageFlow = null;
            foreach (Control oldPage in _pageHost.Controls)
                oldPage.Dispose();
            _pageHost.Controls.Clear();
            _scanButtons.Clear();
            _updateStatusLabel = null;
            _updateCheckButton = null;
            _stressModeSelector = null;
            _stressDurationSelector = null;
            _stressStatusLabel = null;
            _stressStartButton = null;
            _stressStopButton = null;
            _liveSensorStatusLabel = null;
            _liveSensorValuesList = null;
            _sensorChart = null;
            _diskSelector = null;
            _diskScanStatusLabel = null;
            _diskScanStartButton = null;
            _diskScanStopButton = null;

            foreach (KeyValuePair<string, Button> entry in _navigationButtons)
            {
                bool active = String.Equals(entry.Key, page, StringComparison.OrdinalIgnoreCase);
                entry.Value.BackColor = active ? Color.FromArgb(42, 63, 96) : SidebarColor;
                entry.Value.ForeColor = active ? Color.White : Color.FromArgb(222, 229, 239);
                entry.Value.Font = active ? NavigationBoldFont : NavigationRegularFont;
            }

            string title;
            string subtitle;
            GetPageHeader(page, out title, out subtitle);
            _pageTitle.Text = title;
            _pageSubtitle.Text = subtitle;

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.Padding = new Padding(24, 22, 24, 22);
            flow.BackColor = PageColor;
            flow.SizeChanged += delegate { ResizePageCards(); };
            _pageFlow = flow;
            _pageHost.Controls.Clear();
            _pageHost.Controls.Add(flow);

            switch (page)
            {
                case "diagnostics": BuildDiagnosticsPage(flow); break;
                case "stress": BuildStressPage(flow); break;
                case "drives": BuildDrivesPage(flow); break;
                case "recommendations": BuildRecommendationsPage(flow); break;
                case "cleanup": BuildCleanupPage(flow); break;
                case "optimization": BuildOptimizationPage(flow); break;
                case "updates": BuildUpdatesPage(flow); break;
                default: BuildOverviewPage(flow); break;
            }

            ResizePageCards();
        }

        private static void GetPageHeader(string page, out string title, out string subtitle)
        {
            switch (page)
            {
                case "diagnostics":
                    title = "Диагностика компьютера";
                    subtitle = "Сведения о процессоре, памяти, накопителях и состоянии SMART.";
                    break;
                case "stress":
                    title = "Тест стабильности компьютера";
                    subtitle = "Ручной ограниченный выбор нагрузок CPU, FPU, кэша и оперативной памяти.";
                    break;
                case "drives":
                    title = "Накопители, SMART и поверхность";
                    subtitle = "Сводка SMART и ручная последовательная проверка физических дисков только на чтение.";
                    break;
                case "recommendations":
                    title = "Предложения по оптимизации";
                    subtitle = "Персональные подсказки формируются по результатам последней проверки.";
                    break;
                case "cleanup":
                    title = "Безопасная очистка";
                    subtitle = "Удаляются только старые временные файлы текущего пользователя.";
                    break;
                case "optimization":
                    title = "Оптимизация Windows";
                    subtitle = "Используйте встроенные средства Windows — без скрытых твиков и риска для данных.";
                    break;
                case "updates":
                    title = "Обновления PC Care";
                    subtitle = "Проверка новых стабильных версий в GitHub Releases.";
                    break;
                default:
                    title = "Состояние компьютера";
                    subtitle = "Краткий обзор системы и следующий безопасный шаг.";
                    break;
            }
        }

        private void BuildOverviewPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, _snapshot == null ? "Начнём с проверки" : "Последняя проверка",
                _snapshot == null
                    ? "Диагностика запускается автоматически при старте. Можно выполнить её повторно в любой момент."
                    : "Проверено " + _snapshot.CollectedAt.ToString("dd.MM.yyyy HH:mm") + ". Данные собираются локально с помощью Windows WMI.");

            Panel status = CreateStatusBanner(GetOverallStatus(), GetOverallStatusDetails(), GetOverallStatusColor());
            flow.Controls.Add(status);

            TableLayoutPanel metrics = BuildMetricGrid();
            flow.Controls.Add(metrics);

            AddSectionHeading(flow, "Предложения по результатам");
            IList<Recommendation> preview = _recommendations;
            if (preview == null || preview.Count == 0)
            {
                flow.Controls.Add(CreateInfoCard("Проверка ещё не завершена",
                    "После диагностики здесь появятся рекомендации, если система обнаружит условия, на которые стоит обратить внимание."));
            }
            else
            {
                int count = Math.Min(3, preview.Count);
                for (int i = 0; i < count; i++)
                    flow.Controls.Add(CreateRecommendationCard(preview[i]));
                if (preview.Count > count)
                    flow.Controls.Add(CreateActionLink("Показать все предложения", "recommendations"));
            }

            AddSectionHeading(flow, "Быстрые действия");
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.Margin = new Padding(0, 0, 0, 8);
            actions.BackColor = PageColor;
            actions.Controls.Add(CreateActionButton("Очистить временные файлы", false, delegate { ShowPage("cleanup"); }));
            actions.Controls.Add(CreateActionButton("Оптимизировать диски", false, delegate { OpenWindowsTool(WindowsMaintenance.OpenOptimizeDrives); }));
            flow.Controls.Add(actions);

            Button scan = CreateButton(_isScanning ? "Проверка выполняется…" : "Запустить диагностику", true);
            scan.Enabled = !_isScanning;
            scan.Size = new Size(220, 44);
            scan.Click += async delegate { await RunDiagnosticsAsync(); };
            _scanButtons.Add(scan);
            flow.Controls.Add(scan);
        }

        private TableLayoutPanel BuildMetricGrid()
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 4;
            grid.RowCount = 1;
            grid.Height = 146;
            grid.Margin = new Padding(0, 0, 0, 16);
            grid.Padding = new Padding(0);
            grid.BackColor = PageColor;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            string cpuValue = _snapshot == null || !_snapshot.CpuLoadPercent.HasValue
                ? "—" : _snapshot.CpuLoadPercent.Value + "%";
            string cpuDetail = _snapshot == null ? "Нет данных" : _snapshot.CpuName;
            Color cpuColor = _snapshot != null && _snapshot.CpuLoadPercent.HasValue && _snapshot.CpuLoadPercent.Value >= 85
                ? RedColor : AccentColor;

            string memoryValue = "—";
            string memoryDetail = "Нет данных";
            Color memoryColor = AccentColor;
            if (_snapshot != null && _snapshot.MemoryDataKnown && _snapshot.TotalMemoryBytes > 0)
            {
                double used = Math.Max(0.0, 100.0 - (double)_snapshot.AvailableMemoryBytes * 100.0 / _snapshot.TotalMemoryBytes);
                memoryValue = used.ToString("N0") + "%";
                memoryDetail = RecommendationEngine.FormatBytes(_snapshot.AvailableMemoryBytes) + " свободно из " +
                    RecommendationEngine.FormatBytes(_snapshot.TotalMemoryBytes);
                if (used >= 90.0)
                    memoryColor = OrangeColor;
            }

            string diskValue = "—";
            string diskDetail = _snapshot == null ? "Нет данных" : "Разделы не обнаружены";
            Color diskColor = AccentColor;
            LogicalVolumeInfo lowestVolume = GetLowestFreeVolume();
            if (lowestVolume != null)
            {
                diskValue = lowestVolume.FreePercent.ToString("N0") + "% свободно";
                diskDetail = (lowestVolume.DeviceId ?? "Раздел") + " · " + RecommendationEngine.FormatBytes(lowestVolume.FreeBytes);
                if (lowestVolume.FreePercent <= 5.0)
                    diskColor = RedColor;
                else if (lowestVolume.FreePercent <= 15.0)
                    diskColor = OrangeColor;
            }

            string smartValue = GetSmartSummary();
            Color smartColor = GetSmartColor();
            string smartDetail = _snapshot == null ? "Не проверено" : GetSmartDetail();

            grid.Controls.Add(CreateMetricCard("ПРОЦЕССОР", cpuValue, cpuDetail, cpuColor), 0, 0);
            grid.Controls.Add(CreateMetricCard("ПАМЯТЬ", memoryValue, memoryDetail, memoryColor), 1, 0);
            grid.Controls.Add(CreateMetricCard("ДИСК", diskValue, diskDetail, diskColor), 2, 0);
            grid.Controls.Add(CreateMetricCard("SMART", smartValue, smartDetail, smartColor), 3, 0);
            return grid;
        }

        private void BuildDiagnosticsPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, _snapshot == null ? "Параметры компьютера" : "Снимок системы",
                _snapshot == null ? "Нажмите кнопку, чтобы прочитать доступные сведения Windows. Проверка не запускает стресс-тесты."
                    : "Время сбора: " + _snapshot.CollectedAt.ToString("dd.MM.yyyy HH:mm:ss") + ". Высокая загрузка — только краткий снимок.");

            Button scan = CreateButton(_isScanning ? "Проверка выполняется…" : "Повторить диагностику", true);
            scan.Size = new Size(210, 44);
            scan.Enabled = !_isScanning;
            scan.Click += async delegate { await RunDiagnosticsAsync(); };
            _scanButtons.Add(scan);
            flow.Controls.Add(scan);

            if (_snapshot == null)
            {
                flow.Controls.Add(CreateInfoCard("Ожидание диагностики",
                    "Поддерживаются Windows 7 SP1, 8, 8.1, 10 и 11. Запросы выполняются через стандартный Windows Management Instrumentation (WMI)."));
                return;
            }

            List<string> osLines = new List<string>();
            osLines.Add((_snapshot.OperatingSystem ?? "Windows") + " · версия " + (_snapshot.OsVersion ?? "—") +
                " · сборка " + (_snapshot.OsBuild ?? "—"));
            osLines.Add("Архитектура: " + (_snapshot.Architecture ?? "не определена"));
            flow.Controls.Add(CreateDetailCard("Операционная система", osLines));

            List<string> cpuLines = new List<string>();
            cpuLines.Add(_snapshot.CpuName ?? "Модель процессора не определена");
            cpuLines.Add("Загрузка в момент проверки: " + (_snapshot.CpuLoadPercent.HasValue ? _snapshot.CpuLoadPercent.Value + "%" : "нет данных"));
            cpuLines.Add("Ядра: " + NullableText(_snapshot.CpuCores) + " · логические процессоры: " + NullableText(_snapshot.CpuLogicalProcessors));
            cpuLines.Add("Максимальная частота по WMI: " + (_snapshot.CpuMaxClockMhz.HasValue ? _snapshot.CpuMaxClockMhz.Value + " МГц" : "нет данных"));
            flow.Controls.Add(CreateDetailCard("Процессор", cpuLines));

            List<string> memoryLines = new List<string>();
            memoryLines.Add("Всего доступно Windows: " + (_snapshot.MemoryDataKnown
                ? RecommendationEngine.FormatBytes(_snapshot.TotalMemoryBytes) : "нет данных"));
            memoryLines.Add("Свободно на момент проверки: " + (_snapshot.MemoryDataKnown
                ? RecommendationEngine.FormatBytes(_snapshot.AvailableMemoryBytes) : "нет данных"));
            if (_snapshot.MemoryDataKnown && _snapshot.TotalMemoryBytes > 0)
            {
                double used = 100.0 - (double)_snapshot.AvailableMemoryBytes * 100.0 / _snapshot.TotalMemoryBytes;
                memoryLines.Add("Используется примерно: " + used.ToString("N1") + "%");
            }
            flow.Controls.Add(CreateDetailCard("Оперативная память", memoryLines));

            List<string> volumeLines = new List<string>();
            if (_snapshot.Volumes.Count == 0)
                volumeLines.Add("Локальные разделы не обнаружены или WMI не вернул данные.");
            else
            {
                foreach (LogicalVolumeInfo volume in _snapshot.Volumes)
                {
                    string label = String.IsNullOrWhiteSpace(volume.Label) ? String.Empty : " (" + volume.Label + ")";
                    volumeLines.Add((volume.DeviceId ?? "Раздел") + label + ": свободно " +
                        RecommendationEngine.FormatBytes(volume.FreeBytes) + " из " +
                        RecommendationEngine.FormatBytes(volume.SizeBytes) + " · " + volume.FreePercent.ToString("N1") + "%");
                }
            }
            flow.Controls.Add(CreateDetailCard("Свободное место", volumeLines));

            List<string> physicalLines = new List<string>();
            if (_snapshot.PhysicalDisks.Count == 0)
                physicalLines.Add("Физические диски не обнаружены или WMI не вернул данные.");
            else
            {
                foreach (PhysicalDiskInfo disk in _snapshot.PhysicalDisks)
                {
                    string name = String.IsNullOrWhiteSpace(disk.Model) ? "Модель не указана" : disk.Model;
                    string status = String.IsNullOrWhiteSpace(disk.Status) ? "статус Windows не указан" : "статус Windows: " + disk.Status;
                    physicalLines.Add("Диск " + disk.Index + ": " + name + " · " +
                        (disk.SizeBytes > 0 ? RecommendationEngine.FormatBytes(disk.SizeBytes) : "размер не указан") + " · " + status);
                }
            }
            if (_snapshot.SmartDevices.Count == 0)
            {
                physicalLines.Add("SMART: нет данных через стандартный интерфейс WMI. Для NVMe, USB и RAID это возможно; проверьте утилитой производителя.");
            }
            else
            {
                foreach (SmartDeviceStatus smart in _snapshot.SmartDevices)
                {
                    string device = smart.PhysicalDriveIndex.HasValue ? "Диск " + smart.PhysicalDriveIndex.Value :
                        (String.IsNullOrWhiteSpace(smart.InstanceName) ? "Устройство" : smart.InstanceName);
                    string status = smart.PredictsFailure == true ? "возможен отказ" :
                        smart.PredictsFailure == false ? "флаг прогнозируемого отказа не установлен" : "статус не определён";
                    physicalLines.Add("SMART — " + device + ": " + status);
                }
            }
            flow.Controls.Add(CreateDetailCard("Накопители и SMART", physicalLines));

            if (_snapshot.Notes.Count > 0)
                flow.Controls.Add(CreateDetailCard("Примечания диагностики", _snapshot.Notes));
        }

        private void BuildStressPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "Проверка стабильности CPU, FPU, кэша и ОЗУ",
                "Самостоятельный набор нагрузок с выбором компонентов. Запускается только вручную; результат не гарантирует исправность оборудования.");

            flow.Controls.Add(CreateInfoCard("Важное предупреждение",
                "Нагрузки могут сильно нагреть процессор, усилить шум вентиляторов или выключить компьютер. Сохраните работу и подключите ноутбук к питанию. Каждый тест стартует только после подтверждения, ограничен 1–15 минутами и доступен для отмены. RAM-тест использует не более четверти свободной памяти и максимум 1 ГБ для 64-разрядного / 512 МБ для 32-разрядного процесса. Датчики показываются только при наличии реального источника; CPU-температурой считается только явно маркированный CPU-датчик. LibreHardwareMonitor/OpenHardwareMonitor должен быть запущен вами — PC Care не устанавливает монитор или драйверы. GPU и диски не нагружаются; напряжение, частота, мощность и вентиляторы отображаются лишь если источник их предоставляет."));

            Panel sensors = CreateCard(200);
            sensors.Margin = new Padding(0, 0, 0, 12);
            AddLabel(sensors, "Доступные датчики во время теста", 18, 10, 700, 24,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            _liveSensorStatusLabel = AddLabel(sensors, GetLiveSensorStatusText(), 18, 39, 800, 130,
                new Font("Segoe UI", 9F), MutedColor, false);
            LinkLabel sensorProviderLink = new LinkLabel();
            sensorProviderLink.Text = "Как включить датчики: запустите LibreHardwareMonitor (официальный GitHub)";
            sensorProviderLink.LinkArea = new LinkArea(0, sensorProviderLink.Text.Length);
            sensorProviderLink.Location = new Point(18, 172);
            sensorProviderLink.Size = new Size(800, 18);
            sensorProviderLink.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            sensorProviderLink.Font = new Font("Segoe UI", 8.5F);
            sensorProviderLink.LinkColor = AccentColor;
            sensorProviderLink.ActiveLinkColor = AccentColor;
            sensorProviderLink.VisitedLinkColor = AccentColor;
            sensorProviderLink.LinkClicked += delegate { OpenReleasePage(SensorProviderReleaseUrl); };
            sensors.Controls.Add(sensorProviderLink);
            flow.Controls.Add(sensors);

            Panel sensorValues = CreateCard(258);
            sensorValues.Margin = new Padding(0, 0, 0, 12);
            AddLabel(sensorValues, "Текущие показания и диапазон", 18, 9, 700, 22,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            AddLabel(sensorValues, "Текущее, min/max и среднее вычисляются только по фактически полученным значениям текущего/последнего теста.",
                18, 31, 800, 18, new Font("Segoe UI", 8F), MutedColor, false);
            _liveSensorValuesList = new ListView();
            _liveSensorValuesList.View = View.Details;
            _liveSensorValuesList.FullRowSelect = true;
            _liveSensorValuesList.GridLines = true;
            _liveSensorValuesList.HideSelection = false;
            _liveSensorValuesList.Location = new Point(18, 53);
            _liveSensorValuesList.Size = new Size(Math.Max(300, sensorValues.Width - 36), 190);
            _liveSensorValuesList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _liveSensorValuesList.Font = new Font("Segoe UI", 8.5F);
            _liveSensorValuesList.Columns.Add("Параметр", 180);
            _liveSensorValuesList.Columns.Add("Текущее", 80);
            _liveSensorValuesList.Columns.Add("Мин.", 70);
            _liveSensorValuesList.Columns.Add("Макс.", 70);
            _liveSensorValuesList.Columns.Add("Среднее", 80);
            _liveSensorValuesList.Columns.Add("Источник", 150);
            sensorValues.Controls.Add(_liveSensorValuesList);
            flow.Controls.Add(sensorValues);
            UpdateLiveSensorValuesList();

            AddSectionHeading(flow, "Графики текущего или последнего теста");
            _sensorChart = new LiveSensorChartPanel();
            _sensorChart.Width = CurrentCardWidth();
            _sensorChart.SetHistory(_sensorHistory);
            _sensorChart.SetLive(_isStressTestRunning);
            flow.Controls.Add(_sensorChart);

            Panel settings = CreateCard(208);
            settings.Margin = new Padding(0, 0, 0, 12);
            AddLabel(settings, "Компоненты нагрузки — можно выбрать несколько", 18, 9, 650, 22,
                new Font("Segoe UI Semibold", 9F, FontStyle.Bold), TextColor, false);
            _stressModeSelector = new CheckedListBox();
            _stressModeSelector.CheckOnClick = true;
            _stressModeSelector.IntegralHeight = false;
            _stressModeSelector.ItemHeight = 21;
            _stressModeSelector.Font = new Font("Segoe UI", 8.5F);
            _stressModeSelector.Location = new Point(18, 33);
            _stressModeSelector.Size = new Size(Math.Max(250, settings.Width - 40), 94);
            _stressModeSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Cpu,
                "CPU — целочисленные вычисления по логическим потокам (до 64)"),
                (_selectedStressMode & StressTestMode.Cpu) != 0);
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Fpu,
                "FPU — вычисления с плавающей точкой"),
                (_selectedStressMode & StressTestMode.Fpu) != 0);
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Cache,
                "Кэш — чтение/проверка рабочего набора 32 КиБ на поток"),
                (_selectedStressMode & StressTestMode.Cache) != 0);
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Memory,
                "ОЗУ — запись и проверка ограниченного буфера"),
                (_selectedStressMode & StressTestMode.Memory) != 0);
            _stressModeSelector.Enabled = !_isStressTestRunning;
            settings.Controls.Add(_stressModeSelector);

            AddLabel(settings, "Длительность", 18, 133, 500, 22,
                new Font("Segoe UI Semibold", 9F, FontStyle.Bold), TextColor, false);
            _stressDurationSelector = new ComboBox();
            _stressDurationSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _stressDurationSelector.Font = new Font("Segoe UI", 9F);
            _stressDurationSelector.Location = new Point(18, 157);
            _stressDurationSelector.Size = new Size(220, 27);
            _stressDurationSelector.Items.Add(new StressDurationChoice(1, "1 минута — быстрый тест"));
            _stressDurationSelector.Items.Add(new StressDurationChoice(5, "5 минут — стандартный тест"));
            _stressDurationSelector.Items.Add(new StressDurationChoice(15, "15 минут — расширенный тест"));
            _stressDurationSelector.SelectedIndex = 0;
            _stressDurationSelector.Enabled = !_isStressTestRunning;
            settings.Controls.Add(_stressDurationSelector);
            flow.Controls.Add(settings);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.BackColor = PageColor;
            actions.Margin = new Padding(0, 0, 0, 10);
            _stressStartButton = CreateButton(_isStressTestRunning ? "Тест выполняется…" : "Начать тест", true);
            _stressStartButton.Size = new Size(190, 44);
            _stressStartButton.Enabled = !_isStressTestRunning && !_isDiskScanRunning && !_isScanning;
            _stressStartButton.Click += async delegate { await RunStressTestAsync(); };
            actions.Controls.Add(_stressStartButton);
            _stressStopButton = CreateButton("Остановить тест", false);
            _stressStopButton.Size = new Size(170, 44);
            _stressStopButton.Enabled = _isStressTestRunning;
            _stressStopButton.Click += delegate { StopStressTest(); };
            actions.Controls.Add(_stressStopButton);
            flow.Controls.Add(actions);

            Panel progress = CreateCard(150);
            progress.Margin = new Padding(0, 0, 0, 12);
            AddLabel(progress, "Состояние", 18, 10, 600, 24,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            _stressStatusLabel = AddLabel(progress, _stressProgressText, 18, 38, 800, 100,
                new Font("Segoe UI", 9F), MutedColor, false);
            flow.Controls.Add(progress);

            if (_lastStressTestResult != null)
                flow.Controls.Add(CreateDetailCard("Результат последнего теста", GetStressResultLines(_lastStressTestResult)));

            flow.Controls.Add(CreateInfoCard("Как трактовать результат",
                "Ноль ошибок означает только, что выбранные проверки не обнаружили ошибок в ограниченном тестовом наборе. Это не полноценная сертификация стабильности, не проверка всех физических модулей ОЗУ и не диагноз температур, GPU, дисков или блока питания."));
        }

        private void BuildDrivesPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "SMART и проверка дисков",
                "Сведения зависят от контроллера и драйвера. Проверка поверхности выполняет только последовательное чтение физического диска.");

            flow.Controls.Add(CreateInfoCard("Ограничения SMART",
                "Доступны только данные, которые предоставляет Windows WMI. ATA-атрибуты и их raw-значения зависят от производителя; типовые названия — подсказки, а не универсальная расшифровка. Отсутствие SMART не означает исправность. Температура ATA — приблизительная; NVMe/RAID/USB-мосты могут не передавать атрибуты."));

            Button refresh = CreateButton(_isScanning ? "Обновление данных…" : "Обновить диагностику и SMART", true);
            refresh.Size = new Size(250, 44);
            refresh.Enabled = !_isScanning && !_isDiskScanRunning && !_isStressTestRunning;
            refresh.Click += async delegate { await RunDiagnosticsAsync(); };
            _scanButtons.Add(refresh);
            flow.Controls.Add(refresh);

            AddSectionHeading(flow, "Состояние и SMART-атрибуты");
            List<SmartDeviceStatus> displayedSmartDevices = new List<SmartDeviceStatus>();
            if (_snapshot == null || _snapshot.PhysicalDisks.Count == 0)
            {
                flow.Controls.Add(CreateInfoCard("Физические диски не обнаружены",
                    "Запустите или повторите диагностику. WMI SMART-данные, которые нельзя связать с номером физического диска, будут показаны отдельно."));
            }
            else
            {
                foreach (PhysicalDiskInfo disk in _snapshot.PhysicalDisks)
                {
                    SmartDeviceStatus smart = FindSmartDeviceForDisk(disk.Index);
                    if (smart != null && !displayedSmartDevices.Contains(smart))
                        displayedSmartDevices.Add(smart);
                    string diskName = String.IsNullOrWhiteSpace(disk.Model) ? "Модель не указана" : disk.Model;
                    string smartStatus = GetSmartStatusText(smart);
                    string temperature = smart != null && smart.TemperatureCelsius.HasValue
                        ? smart.TemperatureCelsius.Value.ToString("N0") + " °C (приблизительно, ATA SMART)"
                        : "нет данных от доступного SMART-атрибута";
                    List<string> summary = new List<string>();
                    summary.Add("Номер физического диска: " + disk.Index + " · " +
                        (disk.SizeBytes > 0 ? RecommendationEngine.FormatBytes(disk.SizeBytes) : "размер не указан"));
                    summary.Add("Статус Windows: " + (String.IsNullOrWhiteSpace(disk.Status) ? "не указан" : disk.Status));
                    summary.Add("SMART: " + smartStatus);
                    if (smart != null)
                        summary.Add("Источник SMART WMI: " + (String.IsNullOrWhiteSpace(smart.InstanceName) ? "имя не указано" : smart.InstanceName));
                    summary.Add("Температура: " + temperature);
                    flow.Controls.Add(CreateDetailCard("Диск " + disk.Index + " — " + diskName, summary));

                    if (smart != null && smart.Attributes.Count > 0)
                    {
                        flow.Controls.Add(CreateDetailCard("SMART-атрибуты диска " + disk.Index +
                            " (значения производителя)", GetSmartAttributeLines(smart.Attributes)));
                    }
                    else if (smart == null && _snapshot.SmartDevices.Count > 0)
                    {
                        flow.Controls.Add(CreateInfoCard("SMART для диска " + disk.Index + " не сопоставлен",
                            "WMI вернул SMART-строки, но их связь с этим номером диска не подтверждена. Непривязанные сведения показаны отдельно; отсутствие строки не означает исправность."));
                    }
                    else
                    {
                        flow.Controls.Add(CreateInfoCard("Подробные SMART-атрибуты не получены",
                            "Контроллер или драйвер не передал ATA VendorSpecific данные. Это не является подтверждением исправности диска."));
                    }
                }
            }

            if (_snapshot != null)
            {
                foreach (SmartDeviceStatus smart in _snapshot.SmartDevices)
                {
                    if (displayedSmartDevices.Contains(smart))
                        continue;

                    List<string> deviceLines = new List<string>();
                    deviceLines.Add("WMI instance: " + (String.IsNullOrWhiteSpace(smart.InstanceName) ? "имя не указано" : smart.InstanceName));
                    deviceLines.Add("Номер физического диска из WMI: " +
                        (smart.PhysicalDriveIndex.HasValue ? smart.PhysicalDriveIndex.Value.ToString() : "не определён"));
                    deviceLines.Add("SMART: " + GetSmartStatusText(smart));
                    deviceLines.Add("Температура: " + (smart.TemperatureCelsius.HasValue
                        ? smart.TemperatureCelsius.Value.ToString("N0") + " °C (приблизительно, ATA SMART)"
                        : "нет данных"));
                    string deviceTitle = smart.PhysicalDriveIndex.HasValue
                        ? "Дополнительная SMART-строка WMI (индекс " + smart.PhysicalDriveIndex.Value + ")"
                        : "SMART-устройство без сопоставленного физического диска";
                    flow.Controls.Add(CreateDetailCard(deviceTitle, deviceLines));
                    if (smart.Attributes.Count > 0)
                        flow.Controls.Add(CreateDetailCard("ATA SMART-атрибуты — WMI instance", GetSmartAttributeLines(smart.Attributes)));
                }
            }

            AddSectionHeading(flow, "Чтение поверхности диска");
            flow.Controls.Add(CreateInfoCard("Перед запуском",
                "Полное последовательное чтение всего физического диска может занять часы и создать длительную нагрузку на накопитель. Проверка не записывает, не исправляет и не переназначает сектора. Время измеряется для блоков 1 МБ: 200–999 мс отмечаются как медленные, от 1 с — как очень медленные; это эвристика, а не диагноз отдельного сектора. Остановка действует между блоками; при повторных попытках самого диска одно чтение может задержаться. Если Windows SMART уже предупреждает об отказе, сначала сохраните данные. Для raw-доступа могут потребоваться права администратора; PC Care не запрашивает UAC автоматически."));

            Panel selectorCard = CreateCard(82);
            selectorCard.Margin = new Padding(0, 0, 0, 12);
            AddLabel(selectorCard, "Физический диск для полного сканирования", 18, 9, 700, 22,
                new Font("Segoe UI Semibold", 9F, FontStyle.Bold), TextColor, false);
            _diskSelector = new ComboBox();
            _diskSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _diskSelector.Font = new Font("Segoe UI", 9F);
            _diskSelector.Location = new Point(18, 35);
            _diskSelector.Size = new Size(Math.Max(260, selectorCard.Width - 40), 27);
            _diskSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            if (_snapshot != null)
            {
                foreach (PhysicalDiskInfo disk in _snapshot.PhysicalDisks)
                {
                    if (disk.Index >= 0 && disk.SizeBytes > 0)
                        _diskSelector.Items.Add(new DiskChoice(disk));
                }
            }
            if (_diskSelector.Items.Count > 0)
                _diskSelector.SelectedIndex = 0;
            _diskSelector.Enabled = !_isDiskScanRunning;
            selectorCard.Controls.Add(_diskSelector);
            flow.Controls.Add(selectorCard);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.BackColor = PageColor;
            actions.Margin = new Padding(0, 0, 0, 10);
            _diskScanStartButton = CreateButton(_isDiskScanRunning ? "Сканирование…" : "Начать чтение диска", true);
            _diskScanStartButton.Size = new Size(210, 44);
            _diskScanStartButton.Enabled = !_isDiskScanRunning && !_isStressTestRunning && !_isScanning && _diskSelector.Items.Count > 0;
            _diskScanStartButton.Click += async delegate { await RunDiskScanAsync(); };
            actions.Controls.Add(_diskScanStartButton);
            _diskScanStopButton = CreateButton("Остановить чтение", false);
            _diskScanStopButton.Size = new Size(180, 44);
            _diskScanStopButton.Enabled = _isDiskScanRunning;
            _diskScanStopButton.Click += delegate { StopDiskScan(); };
            actions.Controls.Add(_diskScanStopButton);
            flow.Controls.Add(actions);

            Panel status = CreateCard(124);
            status.Margin = new Padding(0, 0, 0, 12);
            AddLabel(status, "Ход проверки", 18, 10, 600, 24,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            _diskScanStatusLabel = AddLabel(status, _diskScanProgressText, 18, 38, 800, 76,
                new Font("Segoe UI", 9F), MutedColor, false);
            flow.Controls.Add(status);

            if (_lastDiskScanResult != null)
                flow.Controls.Add(CreateDetailCard("Итог последней проверки", GetDiskScanResultLines(_lastDiskScanResult)));
        }

        private void BuildRecommendationsPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "Рекомендации после проверки",
                _snapshot == null ? "Сначала выполните диагностику, чтобы советы учитывали параметры вашего компьютера."
                    : "Найденные условия объясняются простым языком. Приложение не меняет системные настройки автоматически.");

            if (_snapshot == null)
            {
                flow.Controls.Add(CreateInfoCard("Нет результата проверки",
                    "Запустите диагностику на странице «Обзор» или «Диагностика». После завершения список обновится автоматически."));
                Button scan = CreateButton(_isScanning ? "Проверка выполняется…" : "Запустить диагностику", true);
                scan.Size = new Size(220, 44);
                scan.Enabled = !_isScanning;
                scan.Click += async delegate { await RunDiagnosticsAsync(); };
                _scanButtons.Add(scan);
                flow.Controls.Add(scan);
                return;
            }

            foreach (Recommendation recommendation in _recommendations)
                flow.Controls.Add(CreateRecommendationCard(recommendation));

            flow.Controls.Add(CreateInfoCard("Как трактовать результаты",
                "Значения получены из доступных WMI-датчиков Windows; SMART поддерживается не каждым контроллером. Неизвестный статус не считается подтверждением исправности. Для важных файлов сохраняйте резервные копии."));
        }

        private void BuildCleanupPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "Очистка временных файлов",
                "Удаляются только старые временные файлы в папке TEMP текущего пользователя.");

            string tempPath = _cleanupService.GetUserTempPath();
            flow.Controls.Add(CreateInfoCard("Что будет очищено",
                "Папка: " + tempPath + "\r\nБудут удалены файлы, не изменявшиеся последние 24 часа. Недавние, занятые или недоступные файлы пропускаются. Пароли, cookies, история браузера, документы, корзина и системные папки не затрагиваются."));

            Panel resultCard = CreateCard(112);
            AddLabel(resultCard, "Последний результат", 18, 14, 500, 24,
                new Font("Segoe UI Semibold", 11F, FontStyle.Bold), TextColor, false);
            string resultText = _isCleaning ? "Очистка выполняется…" :
                _lastCleanupResult == null ? "Очистка ещё не запускалась." :
                "Удалено файлов: " + _lastCleanupResult.DeletedFiles + " · освобождено: " +
                RecommendationEngine.FormatBytes(_lastCleanupResult.BytesFreed) + " · пропущено: " + _lastCleanupResult.SkippedFiles;
            AddLabel(resultCard, resultText, 18, 46, 780, 42,
                new Font("Segoe UI", 9F), MutedColor, false);
            flow.Controls.Add(resultCard);
            if (_lastCleanupResult != null && _lastCleanupResult.Warnings.Count > 0)
                flow.Controls.Add(CreateDetailCard("Предупреждения очистки", _lastCleanupResult.Warnings));

            Button clean = CreateButton(_isCleaning ? "Очистка выполняется…" : "Очистить старые временные файлы", true);
            clean.Size = new Size(275, 46);
            clean.Enabled = !_isCleaning;
            clean.Click += async delegate { await RunCleanupAsync(); };
            flow.Controls.Add(clean);

            flow.Controls.Add(CreateInfoCard("Безопасность",
                "PC Care не запускается от имени администратора и не очищает папки Windows, Prefetch или данные браузера. Некоторые файлы могут быть нужны работающим программам — они останутся на месте."));
        }

        private void BuildOptimizationPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "Оптимизация без рискованных твиков",
                "Встроенные инструменты Windows определяют подходящий способ обслуживания HDD и SSD.");

            flow.Controls.Add(CreateInfoCard("Рекомендация",
                _snapshot == null
                    ? "После диагностики здесь появятся персональные предложения. Для дисков используйте окно «Оптимизация дисков» Windows: система сама выбирает дефрагментацию HDD или TRIM для SSD."
                    : GetOptimizationAdvice()));

            flow.Controls.Add(CreateInfoCard("Почему действия запускаются вручную",
                "Изменения автозагрузки, очистка системных файлов и обслуживание дисков могут влиять на вашу работу. PC Care открывает штатные окна Windows, но не отключает службы, не меняет реестр и не выполняет дефрагментацию в фоне."));

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.BackColor = PageColor;
            actions.Margin = new Padding(0, 0, 0, 10);
            actions.Controls.Add(CreateActionButton("Оптимизация дисков Windows", true, delegate { OpenWindowsTool(WindowsMaintenance.OpenOptimizeDrives); }));
            actions.Controls.Add(CreateActionButton("Проверить автозагрузку", false, delegate { OpenWindowsTool(WindowsMaintenance.OpenStartupManager); }));
            actions.Controls.Add(CreateActionButton("Очистка диска Windows", false, delegate { OpenWindowsTool(WindowsMaintenance.OpenDiskCleanup); }));
            actions.Controls.Add(CreateActionButton("Диспетчер задач", false, delegate { OpenWindowsTool(WindowsMaintenance.OpenTaskManager); }));
            flow.Controls.Add(actions);
        }

        private void BuildUpdatesPage(FlowLayoutPanel flow)
        {
            AddPageIntro(flow, "Обновление программы",
                "PC Care проверяет страницу GitHub Releases при запуске и сообщает, если доступна версия новее.");

            flow.Controls.Add(CreateInfoCard("Установленная версия",
                "PC Care " + UpdateService.FormatVersion(_updateService.CurrentVersion) + "\r\nОбновления публикуются как подписанные по версии GitHub Releases (тег vX.Y.Z). Установка начинается только после вашего подтверждения; при размещении в Program Files Windows может запросить разрешение UAC."));

            _updateStatusLabel = new Label();
            _updateStatusLabel.AutoSize = false;
            _updateStatusLabel.Size = new Size(700, 42);
            _updateStatusLabel.Margin = new Padding(0, 0, 0, 14);
            _updateStatusLabel.ForeColor = MutedColor;
            _updateStatusLabel.Text = _isCheckingUpdate ? "Проверяем GitHub Releases…" : "Последняя проверка выполняется при запуске. Проверку можно запустить вручную.";
            flow.Controls.Add(_updateStatusLabel);

            _updateCheckButton = CreateButton(_isCheckingUpdate ? "Проверка…" : "Проверить обновления", true);
            _updateCheckButton.Size = new Size(210, 44);
            _updateCheckButton.Enabled = !_isCheckingUpdate;
            _updateCheckButton.Click += async delegate { await CheckForUpdatesAsync(false); };
            flow.Controls.Add(_updateCheckButton);

            flow.Controls.Add(CreateInfoCard("Как работает обновление",
                "Приложение получает сведения и ZIP-пакет напрямую из GitHub Releases по HTTPS. Новая версия не устанавливается скрытно: сначала появится уведомление, затем вы решаете, скачивать ли её. Если автоустановка невозможна, откроется страница релиза."));
        }

        private void AddPageIntro(FlowLayoutPanel flow, string title, string description)
        {
            Panel intro = new Panel();
            intro.Height = 60;
            intro.Margin = new Padding(0, 0, 0, 10);
            intro.BackColor = PageColor;
            AddLabel(intro, title, 0, 0, 800, 27,
                new Font("Segoe UI Semibold", 13F, FontStyle.Bold), TextColor, false);
            AddLabel(intro, description, 0, 30, 900, 27,
                new Font("Segoe UI", 9F), MutedColor, false);
            flow.Controls.Add(intro);
        }

        private void AddSectionHeading(FlowLayoutPanel flow, string text)
        {
            Panel heading = new Panel();
            heading.Height = 32;
            heading.Margin = new Padding(0, 4, 0, 2);
            heading.BackColor = PageColor;
            AddLabel(heading, text, 0, 0, 800, 27,
                new Font("Segoe UI Semibold", 11F, FontStyle.Bold), TextColor, false);
            flow.Controls.Add(heading);
        }

        private Panel CreateStatusBanner(string title, string details, Color accent)
        {
            Panel card = CreateCard(88);
            Panel stripe = new Panel();
            stripe.BackColor = accent;
            stripe.Location = new Point(0, 0);
            stripe.Size = new Size(5, card.Height);
            card.Controls.Add(stripe);
            AddLabel(card, title, 20, 12, 780, 28,
                new Font("Segoe UI Semibold", 12F, FontStyle.Bold), accent, false);
            AddLabel(card, details, 20, 44, 840, 34,
                new Font("Segoe UI", 9F), MutedColor, false);
            card.Margin = new Padding(0, 0, 0, 15);
            return card;
        }

        private Control CreateMetricCard(string title, string value, string details, Color valueColor)
        {
            Panel card = CreateCard(130);
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 10, 0);
            AddLabel(card, title, 13, 12, 190, 20,
                new Font("Segoe UI Semibold", 8F, FontStyle.Bold), MutedColor, true);
            Label valueLabel = AddLabel(card, value, 13, 38, 190, 35,
                new Font("Segoe UI Semibold", 18F, FontStyle.Bold), valueColor, true);
            valueLabel.AutoEllipsis = true;
            Label detailsLabel = AddLabel(card, details, 13, 78, 190, 42,
                new Font("Segoe UI", 8F), MutedColor, false);
            detailsLabel.AutoEllipsis = true;
            return card;
        }

        private Panel CreateInfoCard(string title, string body)
        {
            int width = CurrentCardWidth();
            Font bodyFont = new Font("Segoe UI", 9F);
            int bodyHeight = MeasureTextHeight(body, bodyFont, width - 44);
            int height = 50 + bodyHeight + 22;
            Panel card = CreateCard(height);
            card.Margin = new Padding(0, 0, 0, 14);
            AddLabel(card, title, 18, 13, width - 40, 25,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            AddLabel(card, body, 18, 43, width - 40, bodyHeight + 4, bodyFont, MutedColor, false);
            return card;
        }

        private Panel CreateDetailCard(string title, IList<string> lines)
        {
            int width = CurrentCardWidth();
            Font lineFont = new Font("Segoe UI", 9F);
            int totalHeight = 48;
            List<int> lineHeights = new List<int>();
            foreach (string line in lines)
            {
                int lineHeight = MeasureTextHeight(line, lineFont, width - 48);
                lineHeights.Add(lineHeight);
                totalHeight += lineHeight + 8;
            }

            Panel card = CreateCard(totalHeight + 10);
            card.Margin = new Padding(0, 0, 0, 14);
            AddLabel(card, title, 18, 13, width - 42, 24,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            int y = 43;
            for (int i = 0; i < lines.Count; i++)
            {
                AddLabel(card, lines[i], 18, y, width - 42, lineHeights[i] + 2,
                    lineFont, MutedColor, false);
                y += lineHeights[i] + 8;
            }
            return card;
        }

        private Panel CreateRecommendationCard(Recommendation recommendation)
        {
            int width = CurrentCardWidth();
            Color accent = SeverityColor(recommendation.Severity);
            Font detailFont = new Font("Segoe UI", 9F);
            int detailHeight = MeasureTextHeight(recommendation.Details, detailFont, width - 66);
            int height = Math.Max(82, 48 + detailHeight + 12);
            Panel card = CreateCard(height);
            card.Margin = new Padding(0, 0, 0, 12);

            Panel marker = new Panel();
            marker.BackColor = accent;
            marker.Location = new Point(0, 0);
            marker.Size = new Size(5, height);
            card.Controls.Add(marker);
            AddLabel(card, recommendation.Title, 18, 11, width - 42, 25,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), accent, false);
            AddLabel(card, recommendation.Details, 18, 39, width - 42, detailHeight + 3,
                detailFont, MutedColor, false);
            return card;
        }

        private Control CreateActionLink(string text, string page)
        {
            Button link = CreateButton(text, false);
            link.Size = new Size(220, 40);
            link.Click += delegate { ShowPage(page); };
            return link;
        }

        private Button CreateActionButton(string text, bool primary, Action action)
        {
            Button button = CreateButton(text, primary);
            button.Size = new Size(222, 45);
            button.Margin = new Padding(0, 0, 10, 9);
            button.Click += delegate { action(); };
            return button;
        }

        private Button CreateButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 219, 231);
            button.BackColor = primary ? AccentColor : Color.White;
            button.ForeColor = primary ? Color.White : TextColor;
            button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0, 0, 0, 12);
            return button;
        }

        private Panel CreateCard(int height)
        {
            Panel card = new Panel();
            card.Height = height;
            card.Width = CurrentCardWidth();
            card.BackColor = Color.White;
            card.Margin = new Padding(0, 0, 0, 12);
            card.BorderStyle = BorderStyle.FixedSingle;
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            return card;
        }

        private static Label AddLabel(Control parent, string text, int x, int y, int width, int height,
            Font font, Color color, bool boldEllipsis)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(x, y);
            label.Size = new Size(Math.Max(20, width), Math.Max(18, height));
            label.Font = font;
            label.ForeColor = color;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = boldEllipsis;
            label.UseMnemonic = false;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(label);
            return label;
        }

        private void ResizePageCards()
        {
            if (_pageFlow == null || _pageFlow.IsDisposed)
                return;

            int width = Math.Max(320, _pageFlow.ClientSize.Width - _pageFlow.Padding.Left -
                _pageFlow.Padding.Right - SystemInformation.VerticalScrollBarWidth);
            foreach (Control control in _pageFlow.Controls)
            {
                if (control is TableLayoutPanel)
                    control.Width = width;
                else if (control is Panel && control.Dock != DockStyle.Fill)
                    control.Width = width;
                else if (control is Label)
                    control.Width = Math.Min(width, 900);

                ResizeAnchoredLabels(control);
            }
        }

        private static void ResizeAnchoredLabels(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                Label label = child as Label;
                if (label != null && (label.Anchor & AnchorStyles.Left) == AnchorStyles.Left &&
                    (label.Anchor & AnchorStyles.Right) == AnchorStyles.Right)
                    label.Width = Math.Max(24, parent.ClientSize.Width - label.Left - 18);

                ComboBox combo = child as ComboBox;
                if (combo != null && (combo.Anchor & AnchorStyles.Left) == AnchorStyles.Left &&
                    (combo.Anchor & AnchorStyles.Right) == AnchorStyles.Right)
                    combo.Width = Math.Max(160, parent.ClientSize.Width - combo.Left - 20);

                if (child.HasChildren)
                    ResizeAnchoredLabels(child);
            }
        }

        private int CurrentCardWidth()
        {
            if (_pageFlow == null || _pageFlow.IsDisposed)
                return Math.Max(600, ClientSize.Width - 300);
            return Math.Max(320, _pageFlow.ClientSize.Width - _pageFlow.Padding.Left -
                _pageFlow.Padding.Right - SystemInformation.VerticalScrollBarWidth);
        }

        private static int MeasureTextHeight(string text, Font font, int width)
        {
            if (String.IsNullOrEmpty(text))
                return font.Height + 4;
            Size measured = TextRenderer.MeasureText(text, font,
                new Size(Math.Max(80, width), 1000), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return Math.Max(font.Height + 4, measured.Height + 2);
        }

        private async Task RunDiagnosticsAsync()
        {
            if (_isScanning)
                return;

            _isScanning = true;
            ShowPage(_currentPage);
            try
            {
                SystemSnapshot result = await Task.Run(delegate { return _diagnosticService.Run(); });
                if (IsDisposed || Disposing)
                    return;
                _snapshot = result;
                _recommendations = _recommendationEngine.Create(_snapshot);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    MessageBox.Show(this, "Диагностика не завершилась: " + ex.Message,
                        "PC Care", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isScanning = false;
                if (!IsDisposed && !Disposing)
                    ShowPage(_currentPage);
            }
        }

        private StressTestMode GetSelectedStressMode()
        {
            StressTestMode mode = StressTestMode.None;
            if (_stressModeSelector == null)
                return mode;

            foreach (object item in _stressModeSelector.CheckedItems)
            {
                StressModeChoice choice = item as StressModeChoice;
                if (choice != null)
                    mode |= choice.Mode;
            }
            return mode;
        }

        private async Task RunStressTestAsync()
        {
            if (_isStressTestRunning || _isDiskScanRunning || _isScanning)
                return;

            StressTestMode selectedMode = GetSelectedStressMode();
            StressDurationChoice durationChoice = _stressDurationSelector == null
                ? null : _stressDurationSelector.SelectedItem as StressDurationChoice;
            if (selectedMode == StressTestMode.None)
            {
                MessageBox.Show(this, "Отметьте хотя бы один компонент теста: CPU, FPU, кэш или ОЗУ.",
                    "Не выбраны компоненты", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (durationChoice == null)
                return;

            string selectedModeName = GetStressModeName(selectedMode);
            DialogResult answer = MessageBox.Show(this,
                "Запустить нагрузку «" + selectedModeName + "» на " + durationChoice.Minutes + " мин.?\r\n\r\n" +
                "Тест создаст высокую нагрузку на выбранные компоненты. Сохраните документы и подключите ноутбук к питанию. Датчики показывают только доступные источники и могут отсутствовать; температура помечается как CPU только при явной маркировке. GPU и диски не нагружаются. Тест можно остановить в любой момент.",
                "Подтверждение нагрузочного теста", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            _selectedStressMode = selectedMode;
            _isStressTestRunning = true;
            _latestSensorSnapshot = null;
            _sensorHistory.Clear();
            _stressCancellation = new CancellationTokenSource();
            _stressProgressText = "Подготовка нагрузочного теста…";
            ShowPage("stress");
            CancellationToken cancellationToken = _stressCancellation.Token;
            CancellationTokenSource sensorCancellation = new CancellationTokenSource();
            ObserveBackgroundTask(MonitorLiveSensorsAsync(sensorCancellation.Token));
            Progress<StressTestProgress> progress = new Progress<StressTestProgress>(delegate(StressTestProgress update)
            {
                if (!_isStressTestRunning || IsDisposed || Disposing)
                    return;
                _stressProgressText = GetStressProgressText(update);
                if (_stressStatusLabel != null && !_stressStatusLabel.IsDisposed)
                    _stressStatusLabel.Text = _stressProgressText;
            });

            try
            {
                StressTestResult result = await _stressTestService.RunAsync(
                    new StressTestOptions
                    {
                        Mode = selectedMode,
                        Duration = TimeSpan.FromMinutes(durationChoice.Minutes)
                    }, progress, cancellationToken);
                _lastStressTestResult = result;
                _stressProgressText = GetStressSummary(result);
            }
            catch (Exception ex)
            {
                _stressProgressText = "Не удалось выполнить тест: " + ex.Message;
            }
            finally
            {
                sensorCancellation.Cancel();
                sensorCancellation.Dispose();
                _isStressTestRunning = false;
                if (_stressCancellation != null)
                {
                    _stressCancellation.Dispose();
                    _stressCancellation = null;
                }
                if (!IsDisposed && !Disposing)
                    ShowPage(_currentPage);
            }
        }

        private void StopStressTest()
        {
            if (_stressCancellation == null || !_isStressTestRunning)
                return;
            _stressProgressText = "Остановка теста… ожидается завершение текущего блока.";
            if (_stressStatusLabel != null && !_stressStatusLabel.IsDisposed)
                _stressStatusLabel.Text = _stressProgressText;
            _stressCancellation.Cancel();
        }

        private static void ObserveBackgroundTask(Task task)
        {
            task.ContinueWith(delegate(Task completed)
            {
                if (completed.Exception != null)
                    Debug.WriteLine(completed.Exception);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private async Task MonitorLiveSensorsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    LiveSensorSnapshot snapshot = await Task.Run(delegate { return _liveSensorService.Read(); });
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    _latestSensorSnapshot = snapshot;
                    _sensorHistory.Add(snapshot);
                    if (_sensorHistory.Count > 900)
                        _sensorHistory.RemoveAt(0);
                    if (_sensorChart != null && !_sensorChart.IsDisposed)
                        _sensorChart.AddSample(snapshot);
                    UpdateLiveSensorValuesList();
                    if (_liveSensorStatusLabel != null && !_liveSensorStatusLabel.IsDisposed &&
                        String.Equals(_currentPage, "stress", StringComparison.OrdinalIgnoreCase))
                        _liveSensorStatusLabel.Text = GetLiveSensorStatusText();

                    await Task.Delay(2000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // User stopped the stress test; stop sensor polling as well.
            }
            catch (Exception ex)
            {
                if (_liveSensorStatusLabel != null && !_liveSensorStatusLabel.IsDisposed)
                    _liveSensorStatusLabel.Text = "Не удалось обновить датчики: " + ex.Message;
            }
        }

        private void UpdateLiveSensorValuesList()
        {
            if (_liveSensorValuesList == null || _liveSensorValuesList.IsDisposed)
                return;

            _liveSensorValuesList.BeginUpdate();
            try
            {
                _liveSensorValuesList.Items.Clear();
                if (_latestSensorSnapshot == null || _latestSensorSnapshot.Readings.Count == 0)
                {
                    ListViewItem unavailable = new ListViewItem(_latestSensorSnapshot == null
                        ? (_isStressTestRunning ? "Ожидание первого опроса датчиков" : "Запустите тест, чтобы получить показания датчиков")
                        : "Доступные показания не получены");
                    unavailable.SubItems.Add("—");
                    unavailable.SubItems.Add("—");
                    unavailable.SubItems.Add("—");
                    unavailable.SubItems.Add("—");
                    unavailable.SubItems.Add("Нет источника данных");
                    _liveSensorValuesList.Items.Add(unavailable);
                    return;
                }

                Dictionary<string, SensorStatistics> statistics = GetSensorStatistics();
                foreach (LiveSensorValue reading in _latestSensorSnapshot.Readings)
                {
                    if (reading == null)
                        continue;

                    string name = String.IsNullOrWhiteSpace(reading.Name) ? "датчик" : reading.Name;
                    string parameter = GetSensorTypeName(reading.SensorType) + " · " + name;
                    ListViewItem item = new ListViewItem(parameter);
                    item.SubItems.Add(FormatLiveSensorValue(reading));

                    SensorStatistics sensorStatistics;
                    if (statistics.TryGetValue(GetSensorKey(reading), out sensorStatistics))
                    {
                        item.SubItems.Add(FormatLiveSensorValue(sensorStatistics.Minimum, reading.Unit));
                        item.SubItems.Add(FormatLiveSensorValue(sensorStatistics.Maximum, reading.Unit));
                        item.SubItems.Add(FormatLiveSensorValue(sensorStatistics.Average, reading.Unit));
                    }
                    else
                    {
                        item.SubItems.Add("—");
                        item.SubItems.Add("—");
                        item.SubItems.Add("—");
                    }

                    item.SubItems.Add(String.IsNullOrWhiteSpace(reading.Source)
                        ? "Источник не указан" : reading.Source);
                    _liveSensorValuesList.Items.Add(item);
                }
            }
            finally
            {
                _liveSensorValuesList.EndUpdate();
            }
        }

        private Dictionary<string, SensorStatistics> GetSensorStatistics()
        {
            Dictionary<string, SensorStatistics> statistics =
                new Dictionary<string, SensorStatistics>(StringComparer.OrdinalIgnoreCase);
            foreach (LiveSensorSnapshot snapshot in _sensorHistory)
            {
                if (snapshot == null)
                    continue;
                foreach (LiveSensorValue reading in snapshot.Readings)
                {
                    if (reading == null)
                        continue;
                    string key = GetSensorKey(reading);
                    SensorStatistics current;
                    if (!statistics.TryGetValue(key, out current))
                    {
                        current = new SensorStatistics(reading.Value);
                        statistics.Add(key, current);
                    }
                    else
                    {
                        current.Add(reading.Value);
                    }
                }
            }
            return statistics;
        }

        private static string GetSensorKey(LiveSensorValue reading)
        {
            return (reading.Source ?? String.Empty) + "|" + (reading.SensorType ?? String.Empty) + "|" +
                (reading.Unit ?? String.Empty) + "|" + (reading.Identifier ?? String.Empty) + "|" +
                (reading.Name ?? String.Empty);
        }

        private static string FormatLiveSensorValue(LiveSensorValue reading)
        {
            return FormatLiveSensorValue(reading.Value, reading.Unit);
        }

        private static string FormatLiveSensorValue(double value, string unit)
        {
            return value.ToString("N1") + (String.IsNullOrWhiteSpace(unit) ? String.Empty : " " + unit);
        }

        private static string GetSensorTypeName(string sensorType)
        {
            if (String.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase)) return "Нагрузка";
            if (String.Equals(sensorType, "Memory", StringComparison.OrdinalIgnoreCase)) return "Память";
            if (String.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase)) return "Температура";
            if (String.Equals(sensorType, "Voltage", StringComparison.OrdinalIgnoreCase)) return "Напряжение";
            if (String.Equals(sensorType, "Clock", StringComparison.OrdinalIgnoreCase)) return "Частота";
            if (String.Equals(sensorType, "Fan", StringComparison.OrdinalIgnoreCase)) return "Вентилятор";
            if (String.Equals(sensorType, "Power", StringComparison.OrdinalIgnoreCase)) return "Мощность";
            return String.IsNullOrWhiteSpace(sensorType) ? "Показание" : sensorType;
        }

        private string GetLiveSensorStatusText()
        {
            if (_latestSensorSnapshot == null)
                return _isStressTestRunning
                    ? "Запрашиваем доступные датчики Windows…"
                    : "Датчики будут опрашиваться во время нагрузочного теста.";

            List<string> lines = new List<string>();
            lines.Add("CPU (WMI PerfOS): " + (_latestSensorSnapshot.CpuLoadPercent.HasValue
                ? _latestSensorSnapshot.CpuLoadPercent.Value + "% загрузки" : "загрузка недоступна"));
            if (_latestSensorSnapshot.TotalMemoryBytes.HasValue && _latestSensorSnapshot.AvailableMemoryBytes.HasValue)
                lines.Add("ОЗУ (WMI Windows), свободно: " + RecommendationEngine.FormatBytes(_latestSensorSnapshot.AvailableMemoryBytes.Value) +
                    " из " + RecommendationEngine.FormatBytes(_latestSensorSnapshot.TotalMemoryBytes.Value));
            else
                lines.Add("ОЗУ (WMI Windows): данные недоступны");

            int cpuTemperatureCount = 0;
            int otherTemperatureCount = 0;
            bool displayedCpuTemperature = false;
            bool displayedOtherTemperature = false;
            foreach (TemperatureSensorReading reading in _latestSensorSnapshot.TemperatureSensors)
            {
                string source = String.IsNullOrWhiteSpace(reading.Source) ? "WMI" : reading.Source;
                string name = String.IsNullOrWhiteSpace(reading.Name) ? "датчик" : reading.Name;
                if (reading.IsCpuSensor)
                {
                    cpuTemperatureCount++;
                    if (!displayedCpuTemperature)
                    {
                        lines.Add("Температура CPU (" + source + ", " + name + "): " +
                            reading.TemperatureCelsius.ToString("N1") + " °C");
                        displayedCpuTemperature = true;
                    }
                }
                else
                {
                    otherTemperatureCount++;
                    if (!displayedOtherTemperature)
                    {
                        lines.Add("Другой датчик (" + source + ", " + name + "): " +
                            reading.TemperatureCelsius.ToString("N1") + " °C — не подтверждён как CPU");
                        displayedOtherTemperature = true;
                    }
                }
            }
            if (cpuTemperatureCount == 0)
                lines.Add("CPU-датчик не найден. Если Windows не передаёт температуру, запустите и оставьте открытым совместимый монитор, например LibreHardwareMonitor.");

            bool hasSmartTemperature = false;
            if (_snapshot != null)
            {
                foreach (SmartDeviceStatus device in _snapshot.SmartDevices)
                {
                    if (!device.TemperatureCelsius.HasValue)
                        continue;
                    string name = device.PhysicalDriveIndex.HasValue
                        ? "Диск " + device.PhysicalDriveIndex.Value : "Накопитель";
                    lines.Add(name + " (ATA SMART): " + device.TemperatureCelsius.Value.ToString("N0") + " °C*");
                    hasSmartTemperature = true;
                    break;
                }
            }
            if (cpuTemperatureCount == 0 && otherTemperatureCount == 0 && !hasSmartTemperature)
                lines.Add("WMI/ACPI/ATA сейчас не передают температуру; отсутствие показаний не означает 0 °C.");
            lines.Add("Обновлено " + _latestSensorSnapshot.CollectedAt.ToString("HH:mm:ss") +
                "; общая ACPI-зона не подменяет CPU, *SMART — снимок последней диагностики.");
            return String.Join("\r\n", lines.ToArray());
        }

        private static string GetStressProgressText(StressTestProgress progress)
        {
            List<string> details = new List<string>();
            details.Add("Выполняется · прошло " + FormatStressElapsed(progress.Elapsed) +
                (progress.ComputeWorkers > 0 ? " · вычислительных потоков: " + progress.ComputeWorkers : String.Empty));

            List<string> computeStats = new List<string>();
            if ((progress.Mode & StressTestMode.Cpu) != 0)
                computeStats.Add("CPU-блоки " + progress.CpuBlocksCompleted + " · ошибок " + progress.CpuErrors);
            if ((progress.Mode & StressTestMode.Fpu) != 0)
                computeStats.Add("FPU-блоки " + progress.FpuBlocksCompleted + " · ошибок " + progress.FpuErrors);
            if (computeStats.Count > 0)
                details.Add(String.Join("  |  ", computeStats.ToArray()));
            if ((progress.Mode & StressTestMode.Cache) != 0)
                details.Add("Кэш: проходов " + progress.CachePassesCompleted + " · ошибок " + progress.CacheErrors);
            if ((progress.Mode & StressTestMode.Memory) != 0)
            {
                details.Add(progress.MemoryMegabytes > 0
                    ? "ОЗУ: " + progress.MemoryMegabytes + " МБ · проходов " + progress.MemoryPassesCompleted + " · несовпадений " + progress.MemoryErrors
                    : "Подготовка безопасного буфера ОЗУ…");
            }
            return String.Join("\r\n", details.ToArray());
        }

        private static string GetStressSummary(StressTestResult result)
        {
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                return "Тест завершился с ошибкой: " + result.ErrorMessage;
            if (result.HasErrors)
                return "Обнаружены ошибки во время теста. Прекратите нагрузку и проверьте компоненты отдельными средствами.";
            if (result.Cancelled)
                return "Тест остановлен пользователем через " + FormatStressElapsed(result.Duration) + ".";
            return "Тест завершён за " + FormatStressElapsed(result.Duration) + ". Ошибок в выбранных проверках не обнаружено; это не гарантия исправности компьютера.";
        }

        private static IList<string> GetStressResultLines(StressTestResult result)
        {
            List<string> lines = new List<string>();
            lines.Add("Выбрано: " + GetStressModeName(result.Mode));
            lines.Add("Время выполнения: " + FormatStressElapsed(result.Duration));
            if (result.ComputeWorkers > 0)
                lines.Add("Вычислительных потоков: " + result.ComputeWorkers);
            if ((result.Mode & StressTestMode.Cpu) != 0)
                lines.Add("CPU: проверено блоков " + result.CpuBlocksCompleted + " · ошибок вычислений " + result.CpuErrors);
            if ((result.Mode & StressTestMode.Fpu) != 0)
                lines.Add("FPU: проверено блоков " + result.FpuBlocksCompleted + " · ошибок вычислений " + result.FpuErrors);
            if ((result.Mode & StressTestMode.Cache) != 0)
                lines.Add("Кэш: проходов " + result.CachePassesCompleted + " · несовпадений " + result.CacheErrors);
            if ((result.Mode & StressTestMode.Memory) != 0)
                lines.Add("ОЗУ: выделено " + result.MemoryMegabytes + " МБ · полных проходов " + result.MemoryPassesCompleted +
                    " · несовпадений " + result.MemoryErrors);
            if (!String.IsNullOrWhiteSpace(result.FirstMemoryError))
                lines.Add("Первое несовпадение ОЗУ: " + result.FirstMemoryError);
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                lines.Add("Ошибка запуска/выполнения: " + result.ErrorMessage);
            lines.Add(result.Cancelled ? "Статус: остановлен пользователем." :
                result.HasErrors ? "Статус: обнаружены ошибки или тест не удалось завершить." : "Статус: завершён без обнаруженных ошибок.");
            return lines;
        }

        private static string GetStressModeName(StressTestMode mode)
        {
            List<string> names = new List<string>();
            if ((mode & StressTestMode.Cpu) != 0) names.Add("CPU");
            if ((mode & StressTestMode.Fpu) != 0) names.Add("FPU");
            if ((mode & StressTestMode.Cache) != 0) names.Add("кэш");
            if ((mode & StressTestMode.Memory) != 0) names.Add("ОЗУ");
            return names.Count == 0 ? "компоненты не выбраны" : String.Join(", ", names.ToArray());
        }

        private static string FormatStressElapsed(TimeSpan elapsed)
        {
            return String.Format("{0:00}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);
        }

        private async Task RunDiskScanAsync()
        {
            if (_isDiskScanRunning || _isStressTestRunning || _isScanning || _diskSelector == null)
                return;

            DiskChoice choice = _diskSelector.SelectedItem as DiskChoice;
            if (choice == null || choice.Disk == null)
                return;

            SmartDeviceStatus smart = FindSmartDeviceForDisk(choice.Disk.Index);
            string warning = smart != null && smart.PredictsFailure == true
                ? "\r\n\r\nВНИМАНИЕ: Windows SMART уже сообщает о возможном отказе. Сначала сделайте резервную копию; безопаснее не нагружать такой диск."
                : String.Empty;
            DialogResult answer = MessageBox.Show(this,
                "Полностью прочитать физический диск " + choice.Disk.Index + " — " + choice.Disk.Model + " (" +
                RecommendationEngine.FormatBytes(choice.Disk.SizeBytes) + ")?\r\n\r\n" +
                "Тест не записывает, не исправляет и не переназначает сектора. Чтение может занять много времени; отмена применяется после текущего блока." +
                warning + "\r\n\r\nПродолжить?",
                "Подтверждение проверки диска", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            _isDiskScanRunning = true;
            _lastDiskScanResult = null;
            _diskScanCancellation = new CancellationTokenSource();
            _diskScanProgressText = "Открываем физический диск только для чтения…";
            ShowPage("drives");
            CancellationToken cancellationToken = _diskScanCancellation.Token;
            Progress<DiskScanProgress> progress = new Progress<DiskScanProgress>(delegate(DiskScanProgress update)
            {
                if (!_isDiskScanRunning || IsDisposed || Disposing)
                    return;
                _diskScanProgressText = GetDiskScanProgressText(update);
                if (_diskScanStatusLabel != null && !_diskScanStatusLabel.IsDisposed)
                    _diskScanStatusLabel.Text = _diskScanProgressText;
            });

            try
            {
                _lastDiskScanResult = await _diskSurfaceTestService.RunAsync(
                    choice.Disk.Index, progress, cancellationToken);
                _diskScanProgressText = GetDiskScanSummary(_lastDiskScanResult);
            }
            catch (Exception ex)
            {
                _diskScanProgressText = "Не удалось выполнить чтение диска: " + ex.Message;
            }
            finally
            {
                _isDiskScanRunning = false;
                if (_diskScanCancellation != null)
                {
                    _diskScanCancellation.Dispose();
                    _diskScanCancellation = null;
                }
                if (!IsDisposed && !Disposing)
                    ShowPage(_currentPage);
            }
        }

        private void StopDiskScan()
        {
            if (_diskScanCancellation == null || !_isDiskScanRunning)
                return;
            _diskScanProgressText = "Остановка… ожидается завершение текущего чтения.";
            if (_diskScanStatusLabel != null && !_diskScanStatusLabel.IsDisposed)
                _diskScanStatusLabel.Text = _diskScanProgressText;
            _diskScanCancellation.Cancel();
        }

        private static string GetDiskScanProgressText(DiskScanProgress progress)
        {
            List<string> lines = new List<string>();
            lines.Add("Полное чтение: " + progress.PercentComplete.ToString("N1") + "% · обработано " +
                RecommendationEngine.FormatBytes((ulong)Math.Max(0L, progress.BytesProcessed)) + " из " +
                RecommendationEngine.FormatBytes((ulong)Math.Max(0L, progress.TotalBytes)));
            lines.Add("Блоков: " + progress.BlocksProcessed + " · ошибок чтения: " + progress.ErrorBlocks +
                " · медленных (200–999 мс): " + progress.SlowBlocks + " · очень медленных (≥1 с): " + progress.VerySlowBlocks);
            lines.Add("Последний блок: " + progress.LastBlockMilliseconds.ToString("N1") + " мс · максимум: " +
                progress.MaximumBlockMilliseconds.ToString("N1") + " мс");
            return String.Join("\r\n", lines.ToArray());
        }

        private static string GetDiskScanSummary(DiskScanResult result)
        {
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                return "Не удалось полностью прочитать диск: " + result.ErrorMessage;
            if (result.Cancelled)
                return "Чтение остановлено пользователем после обработки " +
                    RecommendationEngine.FormatBytes((ulong)Math.Max(0L, result.BytesProcessed)) + ".";
            if (result.ErrorBlocks > 0)
                return "Сканирование завершено с ошибками чтения. Сохраните данные и проверьте диск/кабель/контроллер.";
            return result.Completed
                ? "Полное последовательное чтение завершено; ошибок чтения не обнаружено. Это не гарантия исправности накопителя."
                : "Сканирование не завершено.";
        }

        private static IList<string> GetDiskScanResultLines(DiskScanResult result)
        {
            List<string> lines = new List<string>();
            lines.Add("Статус: " + (result.Completed ? "полное чтение завершено" :
                result.Cancelled ? "остановлено пользователем" : "не завершено"));
            lines.Add("Обработано: " + RecommendationEngine.FormatBytes((ulong)Math.Max(0L, result.BytesProcessed)) +
                " из " + RecommendationEngine.FormatBytes((ulong)Math.Max(0L, result.TotalBytes)) +
                " · успешно прочитано: " + RecommendationEngine.FormatBytes((ulong)Math.Max(0L, result.BytesRead)));
            lines.Add("Блоков: " + result.BlocksProcessed + " · ошибок чтения: " + result.ErrorBlocks +
                " · медленных (200–999 мс): " + result.SlowBlocks + " · очень медленных (≥1 с): " + result.VerySlowBlocks);
            lines.Add("Максимальное время чтения блока 1 МБ: " + result.MaximumBlockMilliseconds.ToString("N1") + " мс");
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                lines.Add("Причина остановки: " + result.ErrorMessage);
            foreach (string sample in result.ErrorSamples)
                lines.Add(sample);
            return lines;
        }

        private static IList<string> GetSmartAttributeLines(IList<SmartAttribute> attributes)
        {
            List<string> lines = new List<string>();
            foreach (SmartAttribute attribute in attributes)
            {
                string threshold = attribute.Threshold.HasValue ? attribute.Threshold.Value.ToString() : "н/д";
                lines.Add(String.Format(System.Globalization.CultureInfo.CurrentCulture,
                    "ID {0:X2} · {1} · Current {2} · Worst {3} · порог {4} · raw 0x{5}",
                    attribute.Id, attribute.Name, attribute.Current, attribute.Worst, threshold,
                    attribute.RawHex ?? attribute.RawValue.ToString("X12")));
            }
            return lines;
        }

        private SmartDeviceStatus FindSmartDeviceForDisk(int diskIndex)
        {
            if (_snapshot == null)
                return null;
            foreach (SmartDeviceStatus status in _snapshot.SmartDevices)
            {
                if (status.PhysicalDriveIndex == diskIndex)
                    return status;
            }
            return null;
        }

        private static string GetSmartStatusText(SmartDeviceStatus status)
        {
            if (status == null)
                return "нет данных через Windows SMART/WMI";
            if (status.PredictsFailure == true)
                return "Windows прогнозирует отказ — срочно сохраните данные";
            if (status.PredictsFailure == false)
                return "флаг прогнозируемого отказа не установлен (это не гарантия здоровья)";
            return "статус не определён";
        }

        private async Task RunCleanupAsync()
        {
            if (_isCleaning)
                return;

            DialogResult answer = MessageBox.Show(this,
                "Удалить файлы старше 24 часов из папки TEMP текущего пользователя?\r\n\r\n" +
                "Недавние, занятые и недоступные файлы будут пропущены. Данные браузера и документы не затрагиваются.",
                "Подтверждение очистки", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            _isCleaning = true;
            ShowPage("cleanup");
            try
            {
                CleanupResult result = await Task.Run(delegate { return _cleanupService.CleanOldTemporaryFiles(); });
                if (IsDisposed || Disposing)
                    return;
                _lastCleanupResult = result;
                ShowPage("cleanup");
                string message = "Удалено файлов: " + _lastCleanupResult.DeletedFiles + "\r\nОсвобождено: " +
                    RecommendationEngine.FormatBytes(_lastCleanupResult.BytesFreed) + "\r\nПропущено: " + _lastCleanupResult.SkippedFiles;
                if (_lastCleanupResult.Warnings.Count > 0)
                    message += "\r\n\r\nНекоторые папки обработать не удалось. Проверьте подробности на странице очистки.";
                MessageBox.Show(this, message, "Очистка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    MessageBox.Show(this, "Не удалось завершить очистку: " + ex.Message,
                        "PC Care", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isCleaning = false;
                if (!IsDisposed && !Disposing)
                    ShowPage("cleanup");
            }
        }

        private async Task CheckForUpdatesAsync(bool silent)
        {
            if (_isCheckingUpdate)
                return;

            _isCheckingUpdate = true;
            if (_currentPage == "updates")
            {
                if (_updateStatusLabel != null)
                    _updateStatusLabel.Text = "Проверяем GitHub Releases…";
                if (_updateCheckButton != null)
                {
                    _updateCheckButton.Enabled = false;
                    _updateCheckButton.Text = "Проверка…";
                }
            }

            UpdateCheckResult result = null;
            try
            {
                result = await _updateService.CheckAsync();
            }
            catch (Exception ex)
            {
                result = new UpdateCheckResult { ErrorMessage = ex.Message };
            }
            finally
            {
                _isCheckingUpdate = false;
                if (_updateCheckButton != null && !_updateCheckButton.IsDisposed)
                {
                    _updateCheckButton.Enabled = true;
                    _updateCheckButton.Text = "Проверить обновления";
                }
            }

            if (IsDisposed || Disposing)
                return;
            if (result == null)
                result = new UpdateCheckResult { ErrorMessage = "Не удалось получить ответ от GitHub." };

            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                SetUpdateStatus("Не удалось проверить обновления. Проверьте подключение к интернету.");
                if (!silent)
                    MessageBox.Show(this, result.ErrorMessage, "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (result.NoPublishedReleases)
            {
                SetUpdateStatus("Пока нет опубликованных версий в GitHub Releases.");
                if (!silent)
                    MessageBox.Show(this, "В репозитории ещё нет опубликованных релизов.", "PC Care",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (result.Release == null)
            {
                SetUpdateStatus("GitHub не вернул сведения о версии.");
                return;
            }

            if (result.IsUpToDate)
            {
                string upToDate = "У вас актуальная версия PC Care " + UpdateService.FormatVersion(_updateService.CurrentVersion) + ".";
                SetUpdateStatus(upToDate);
                if (!silent)
                    MessageBox.Show(this, upToDate, "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string availableVersion = UpdateService.FormatVersion(result.Release.Version);
            SetUpdateStatus("Доступна новая версия " + availableVersion + ".");
            string notes = String.IsNullOrWhiteSpace(result.Release.ReleaseNotes)
                ? String.Empty
                : "\r\n\r\nЧто нового:\r\n" + Truncate(result.Release.ReleaseNotes, 700);
            string prompt = "Доступна новая версия PC Care " + availableVersion + "." + notes + "\r\n\r\n" +
                (result.Release.HasInstallPackage
                    ? "Скачать и установить её сейчас? Приложение будет перезапущено."
                    : "Для этого релиза нет ZIP-пакета. Открыть страницу релиза?");
            DialogResult choice = MessageBox.Show(this, prompt, "Доступно обновление PC Care",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (choice != DialogResult.Yes)
                return;

            if (!result.Release.HasInstallPackage)
            {
                OpenReleasePage(result.Release.ReleasePageUrl);
                return;
            }

            await InstallUpdateAsync(result.Release);
        }

        private async Task InstallUpdateAsync(ReleaseInfo release)
        {
            try
            {
                SetUpdateStatus("Скачиваем пакет " + release.TagName + "…");
                await _updateService.DownloadAndStartUpdateAsync(release, Process.GetCurrentProcess().Id,
                    Application.ExecutablePath);
                Application.Exit();
            }
            catch (Exception ex)
            {
                SetUpdateStatus("Не удалось установить обновление. Текущая версия оставлена без изменений.");
                DialogResult open = MessageBox.Show(this,
                    "Не удалось подготовить обновление. Текущая версия не была намеренно удалена.\r\n\r\n" + ex.Message +
                    "\r\n\r\nОткрыть страницу релиза для ручной загрузки?",
                    "Обновление PC Care", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (open == DialogResult.Yes)
                    OpenReleasePage(release.ReleasePageUrl);
            }
        }

        private void SetUpdateStatus(string text)
        {
            if (_updateStatusLabel != null && !_updateStatusLabel.IsDisposed)
                _updateStatusLabel.Text = text;
        }

        private static string Truncate(string text, int maximumLength)
        {
            string value = (text ?? String.Empty).Trim();
            if (value.Length <= maximumLength)
                return value;
            return value.Substring(0, maximumLength) + "…";
        }

        private static void OpenReleasePage(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
                return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void OpenWindowsTool(Action open)
        {
            try
            {
                open();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть встроенный инструмент Windows: " + ex.Message,
                    "PC Care", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private string GetOverallStatus()
        {
            if (_snapshot == null)
                return _isScanning ? "Выполняется диагностика" : "Ожидание диагностики";

            foreach (Recommendation recommendation in _recommendations)
            {
                if (recommendation.Severity == RecommendationSeverity.Critical)
                    return "Нужно обратить внимание";
            }
            foreach (Recommendation recommendation in _recommendations)
            {
                if (recommendation.Severity == RecommendationSeverity.Warning)
                    return "Есть рекомендации";
            }
            return _snapshot.Notes.Count > 0 || !HasKnownSmartStatus()
                ? "Проверка завершена"
                : "Срочных проблем не найдено";
        }

        private string GetOverallStatusDetails()
        {
            if (_snapshot == null)
                return "После завершения проверки появятся показатели CPU, памяти, дисков и рекомендации.";
            if (_recommendations.Count == 0)
                return "Проверка завершена, но результатов для оценки недостаточно.";
            if (_snapshot.Notes.Count > 0)
                return "Часть данных недоступна. Оценка основана только на ответивших компонентах Windows.";
            if (!HasKnownSmartStatus())
                return "SMART не передал определённый статус. Неизвестные значения не трактуются как исправность.";
            return "Оценка основана на доступных данных Windows и не заменяет резервное копирование.";
        }

        private Color GetOverallStatusColor()
        {
            if (_snapshot == null)
                return AccentColor;
            foreach (Recommendation recommendation in _recommendations)
            {
                if (recommendation.Severity == RecommendationSeverity.Critical)
                    return RedColor;
            }
            foreach (Recommendation recommendation in _recommendations)
            {
                if (recommendation.Severity == RecommendationSeverity.Warning)
                    return OrangeColor;
            }
            return _snapshot.Notes.Count > 0 || !HasKnownSmartStatus() ? AccentColor : GreenColor;
        }

        private bool HasKnownSmartStatus()
        {
            if (_snapshot == null)
                return false;
            foreach (SmartDeviceStatus status in _snapshot.SmartDevices)
            {
                if (status.PredictsFailure.HasValue)
                    return true;
            }
            return false;
        }

        private string GetSmartSummary()
        {
            if (_snapshot == null || _snapshot.SmartDevices.Count == 0)
                return "Нет данных";

            bool known = false;
            foreach (SmartDeviceStatus status in _snapshot.SmartDevices)
            {
                if (status.PredictsFailure == true)
                    return "Проверить";
                if (status.PredictsFailure == false)
                    known = true;
            }
            return known ? "Нет сигнала" : "Нет данных";
        }

        private string GetSmartDetail()
        {
            if (_snapshot == null)
                return "Не проверено";
            if (_snapshot.SmartDevices.Count == 0)
                return "Контроллер не передал SMART";

            foreach (SmartDeviceStatus status in _snapshot.SmartDevices)
            {
                if (status.PredictsFailure.HasValue)
                    return _snapshot.SmartDevices.Count + " устройство(а); это не гарантия исправности";
            }
            return "Статус SMART не определён";
        }

        private Color GetSmartColor()
        {
            if (_snapshot == null || _snapshot.SmartDevices.Count == 0)
                return MutedColor;

            bool known = false;
            foreach (SmartDeviceStatus status in _snapshot.SmartDevices)
            {
                if (status.PredictsFailure == true)
                    return RedColor;
                if (status.PredictsFailure == false)
                    known = true;
            }
            return known ? GreenColor : MutedColor;
        }

        private LogicalVolumeInfo GetLowestFreeVolume()
        {
            if (_snapshot == null || _snapshot.Volumes.Count == 0)
                return null;

            LogicalVolumeInfo lowest = null;
            foreach (LogicalVolumeInfo volume in _snapshot.Volumes)
            {
                if (volume.SizeBytes == 0)
                    continue;
                if (lowest == null || volume.FreePercent < lowest.FreePercent)
                    lowest = volume;
            }
            return lowest;
        }

        private string GetOptimizationAdvice()
        {
            foreach (Recommendation recommendation in _recommendations)
            {
                if (recommendation.Severity == RecommendationSeverity.Critical ||
                    recommendation.Severity == RecommendationSeverity.Warning)
                    return recommendation.Title + ". " + recommendation.Details +
                        "\r\n\r\nДля общего обслуживания используйте «Оптимизация дисков Windows» и выберите действия в самом Windows.";
            }
            return "По доступным данным срочных проблем не найдено. Для обслуживания накопителя откройте «Оптимизация дисков Windows»: Windows сама выберет подходящий режим для типа накопителя.";
        }

        private static string NullableText(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "нет данных";
        }

        private sealed class DiskChoice
        {
            public DiskChoice(PhysicalDiskInfo disk)
            {
                Disk = disk;
            }

            public PhysicalDiskInfo Disk { get; private set; }

            public override string ToString()
            {
                string model = String.IsNullOrWhiteSpace(Disk.Model) ? "Модель не указана" : Disk.Model;
                return "Диск " + Disk.Index + " — " + model + " (" + RecommendationEngine.FormatBytes(Disk.SizeBytes) + ")";
            }
        }

        private sealed class SensorStatistics
        {
            public SensorStatistics(double value)
            {
                Minimum = value;
                Maximum = value;
                Total = value;
                Count = 1;
            }

            public double Minimum { get; private set; }
            public double Maximum { get; private set; }
            public double Total { get; private set; }
            public long Count { get; private set; }

            public double Average
            {
                get { return Total / Count; }
            }

            public void Add(double value)
            {
                Minimum = Math.Min(Minimum, value);
                Maximum = Math.Max(Maximum, value);
                Total += value;
                Count++;
            }
        }

        private sealed class StressModeChoice
        {
            public StressModeChoice(StressTestMode mode, string title)
            {
                Mode = mode;
                Title = title;
            }

            public StressTestMode Mode { get; private set; }
            public string Title { get; private set; }

            public override string ToString()
            {
                return Title;
            }
        }

        private sealed class StressDurationChoice
        {
            public StressDurationChoice(int minutes, string title)
            {
                Minutes = minutes;
                Title = title;
            }

            public int Minutes { get; private set; }
            public string Title { get; private set; }

            public override string ToString()
            {
                return Title;
            }
        }

        private static Color SeverityColor(RecommendationSeverity severity)
        {
            switch (severity)
            {
                case RecommendationSeverity.Critical: return RedColor;
                case RecommendationSeverity.Warning: return OrangeColor;
                default: return AccentColor;
            }
        }
    }
}
