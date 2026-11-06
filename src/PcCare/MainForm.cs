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

        private readonly DiagnosticService _diagnosticService = new DiagnosticService();
        private readonly RecommendationEngine _recommendationEngine = new RecommendationEngine();
        private readonly CleanupService _cleanupService = new CleanupService();
        private readonly StressTestService _stressTestService = new StressTestService();
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
        private ComboBox _stressModeSelector;
        private ComboBox _stressDurationSelector;
        private Label _stressStatusLabel;
        private Button _stressStartButton;
        private Button _stressStopButton;
        private CancellationTokenSource _stressCancellation;
        private StressTestResult _lastStressTestResult;
        private string _stressProgressText = "Нагрузочный тест ещё не запускался.";
        private string _currentPage = "overview";
        private bool _isScanning;
        private bool _isCleaning;
        private bool _isCheckingUpdate;
        private bool _isStressTestRunning;

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
            if (!_isStressTestRunning)
                return;

            DialogResult answer = MessageBox.Show(this,
                "Нагрузочный тест ещё выполняется. Остановить его и закрыть PC Care?",
                "Тест компьютера", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            if (_stressCancellation != null)
                _stressCancellation.Cancel();
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
                if (_isStressTestRunning && !String.Equals(page, "stress", StringComparison.OrdinalIgnoreCase))
                {
                    DialogResult stop = MessageBox.Show(this,
                        "Нагрузочный тест ещё выполняется. Остановить его и перейти в другой раздел?",
                        "Тест компьютера", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (stop != DialogResult.Yes)
                        return;
                    if (_stressCancellation != null)
                        _stressCancellation.Cancel();
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
                    subtitle = "Ручной ограниченный нагрузочный тест процессора и оперативной памяти.";
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
            AddPageIntro(flow, "Проверка стабильности CPU и RAM",
                "Запускается только вручную. Результат показывает ошибки в ходе теста, но не гарантирует исправность компонентов.");

            flow.Controls.Add(CreateInfoCard("Важное предупреждение",
                "Нагрузочный тест может сильно нагреть процессор и вызвать шум вентиляторов или выключение компьютера. Сохраните работу, подключите ноутбук к питанию и следите за температурой отдельной утилитой: PC Care её не измеряет. Остановите тест при перегреве, зависаниях или необычном шуме. Проверка ОЗУ выделяет не более 25% свободной памяти (до 1 ГБ в 64-разрядном процессе / 512 МБ в 32-разрядном). GPU, видеопамять, блок питания и накопители здесь не тестируются."));

            Panel settings = CreateCard(164);
            settings.Margin = new Padding(0, 0, 0, 12);
            AddLabel(settings, "Компоненты", 18, 9, 500, 22,
                new Font("Segoe UI Semibold", 9F, FontStyle.Bold), TextColor, false);
            _stressModeSelector = new ComboBox();
            _stressModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _stressModeSelector.Font = new Font("Segoe UI", 9F);
            _stressModeSelector.Location = new Point(18, 33);
            _stressModeSelector.Size = new Size(Math.Max(250, settings.Width - 40), 27);
            _stressModeSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Cpu, "Процессор — нагрузка на логические ядра (до 64 потоков)"));
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.Memory, "Оперативная память — запись и проверка шаблонов"));
            _stressModeSelector.Items.Add(new StressModeChoice(StressTestMode.CpuAndMemory, "Комплексный тест — процессор и память одновременно"));
            _stressModeSelector.SelectedIndex = 0;
            settings.Controls.Add(_stressModeSelector);

            AddLabel(settings, "Длительность", 18, 69, 500, 22,
                new Font("Segoe UI Semibold", 9F, FontStyle.Bold), TextColor, false);
            _stressDurationSelector = new ComboBox();
            _stressDurationSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _stressDurationSelector.Font = new Font("Segoe UI", 9F);
            _stressDurationSelector.Location = new Point(18, 93);
            _stressDurationSelector.Size = new Size(180, 27);
            _stressDurationSelector.Items.Add(new StressDurationChoice(1, "1 минута — быстрый тест"));
            _stressDurationSelector.Items.Add(new StressDurationChoice(5, "5 минут — стандартный тест"));
            _stressDurationSelector.Items.Add(new StressDurationChoice(15, "15 минут — расширенный тест"));
            _stressDurationSelector.SelectedIndex = 0;
            settings.Controls.Add(_stressDurationSelector);
            flow.Controls.Add(settings);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.BackColor = PageColor;
            actions.Margin = new Padding(0, 0, 0, 10);
            _stressStartButton = CreateButton(_isStressTestRunning ? "Тест выполняется…" : "Начать тест", true);
            _stressStartButton.Size = new Size(190, 44);
            _stressStartButton.Enabled = !_isStressTestRunning;
            _stressStartButton.Click += async delegate { await RunStressTestAsync(); };
            actions.Controls.Add(_stressStartButton);
            _stressStopButton = CreateButton("Остановить тест", false);
            _stressStopButton.Size = new Size(170, 44);
            _stressStopButton.Enabled = _isStressTestRunning;
            _stressStopButton.Click += delegate { StopStressTest(); };
            actions.Controls.Add(_stressStopButton);
            flow.Controls.Add(actions);

            Panel progress = CreateCard(100);
            progress.Margin = new Padding(0, 0, 0, 12);
            AddLabel(progress, "Состояние", 18, 10, 600, 24,
                new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextColor, false);
            _stressStatusLabel = AddLabel(progress, _stressProgressText, 18, 38, 800, 52,
                new Font("Segoe UI", 9F), MutedColor, false);
            flow.Controls.Add(progress);

            if (_lastStressTestResult != null)
                flow.Controls.Add(CreateDetailCard("Результат последнего теста", GetStressResultLines(_lastStressTestResult)));

            flow.Controls.Add(CreateInfoCard("Как трактовать результат",
                "Ноль ошибок означает только, что выбранный короткий тест не обнаружил ошибок в выделенной памяти и вычислениях. Это не полноценная сертификация стабильности, не проверка всей физической ОЗУ и не диагноз температуры, GPU или блока питания."));
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

        private async Task RunStressTestAsync()
        {
            if (_isStressTestRunning)
                return;

            StressModeChoice modeChoice = _stressModeSelector == null
                ? null : _stressModeSelector.SelectedItem as StressModeChoice;
            StressDurationChoice durationChoice = _stressDurationSelector == null
                ? null : _stressDurationSelector.SelectedItem as StressDurationChoice;
            if (modeChoice == null || durationChoice == null)
                return;

            string selectedMode = GetStressModeName(modeChoice.Mode);
            DialogResult answer = MessageBox.Show(this,
                "Запустить тест «" + selectedMode + "» на " + durationChoice.Minutes + " мин.?\r\n\r\n" +
                "Тест создаст высокую нагрузку. Сохраните открытые документы, подключите ноутбук к питанию и следите за температурой. " +
                "PC Care не измеряет температуру и GPU. Тест можно остановить в любой момент.",
                "Подтверждение нагрузочного теста", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            _isStressTestRunning = true;
            _stressCancellation = new CancellationTokenSource();
            _stressProgressText = "Подготовка нагрузочного теста…";
            ShowPage("stress");
            CancellationToken cancellationToken = _stressCancellation.Token;
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
                        Mode = modeChoice.Mode,
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

        private static string GetStressProgressText(StressTestProgress progress)
        {
            List<string> details = new List<string>();
            details.Add("Выполняется, прошло " + FormatStressElapsed(progress.Elapsed));
            if (progress.CpuWorkers > 0)
                details.Add("CPU: " + progress.CpuWorkers + " потоков, проверено блоков: " + progress.CpuBlocksCompleted +
                    ", ошибок: " + progress.CpuErrors);
            if (progress.MemoryMegabytes > 0)
                details.Add("ОЗУ: " + progress.MemoryMegabytes + " МБ, проходов: " + progress.MemoryPassesCompleted +
                    ", ошибок: " + progress.MemoryErrors);
            else if (progress.MemoryPassesCompleted == 0 && progress.CpuWorkers == 0)
                details.Add("Подготовка теста ОЗУ…");
            return String.Join("\r\n", details.ToArray());
        }

        private static string GetStressSummary(StressTestResult result)
        {
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                return "Тест завершился с ошибкой: " + result.ErrorMessage;
            if (result.CpuErrors > 0 || result.MemoryErrors > 0)
                return "Обнаружены ошибки во время теста. Прекратите нагрузку и проверьте компоненты отдельными средствами.";
            if (result.Cancelled)
                return "Тест остановлен пользователем через " + FormatStressElapsed(result.Duration) + ".";
            return "Тест завершён за " + FormatStressElapsed(result.Duration) + ". Ошибок в проверенном объёме не обнаружено; это не гарантия исправности компьютера.";
        }

        private static IList<string> GetStressResultLines(StressTestResult result)
        {
            List<string> lines = new List<string>();
            lines.Add("Режим: " + GetStressModeName(result.Mode));
            lines.Add("Время выполнения: " + FormatStressElapsed(result.Duration));
            if (result.CpuWorkers > 0)
                lines.Add("CPU: потоков " + result.CpuWorkers + " · проверено блоков " + result.CpuBlocksCompleted +
                    " · ошибок вычислений " + result.CpuErrors);
            if (result.MemoryMegabytes > 0)
                lines.Add("ОЗУ: выделено " + result.MemoryMegabytes + " МБ · полных проходов " + result.MemoryPassesCompleted +
                    " · несовпадений " + result.MemoryErrors);
            if (!String.IsNullOrWhiteSpace(result.FirstMemoryError))
                lines.Add("Первое несовпадение: " + result.FirstMemoryError);
            if (!String.IsNullOrWhiteSpace(result.ErrorMessage))
                lines.Add("Ошибка запуска/выполнения: " + result.ErrorMessage);
            lines.Add(result.Cancelled ? "Статус: остановлен пользователем." :
                result.HasErrors ? "Статус: обнаружены ошибки или тест не удалось завершить." : "Статус: завершён без обнаруженных ошибок.");
            return lines;
        }

        private static string GetStressModeName(StressTestMode mode)
        {
            switch (mode)
            {
                case StressTestMode.Memory: return "оперативная память";
                case StressTestMode.CpuAndMemory: return "процессор и оперативная память";
                default: return "процессор";
            }
        }

        private static string FormatStressElapsed(TimeSpan elapsed)
        {
            return String.Format("{0:00}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);
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
