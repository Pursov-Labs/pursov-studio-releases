using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PursovStudio;

public partial class MainWindow : Window
{
    private readonly CoreController core = new();
    private readonly Queue<string> logLines = new();
    private readonly object logGate = new();
    private readonly bool preview = Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase);
    private readonly bool startHidden;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem trayAction;
    private readonly UiPalette palette;
    private readonly Dictionary<string, System.Drawing.Icon> trayIcons = new();
    private ProbeResult[] displayedResults = Array.Empty<ProbeResult>();
    private StrategyResult[] displayedStrategies = Array.Empty<StrategyResult>();
    private ConflictInfo[] displayedConflicts = Array.Empty<ConflictInfo>();
    private string trayState = "";
    private string? uiError;
    private bool allowClose;
    private bool exiting;
    private bool initialized;
    private bool initializing = true;
    private bool logDirty;
    private volatile bool logVisible;
    private int refreshQueued;

    public MainWindow(bool startHidden = false)
    {
        this.startHidden = startHidden;
        InitializeComponent();
        if (startHidden && !preview)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
        palette = new UiPalette();
        palette.Changed += ThemeChanged;
        if (preview) Title += " — предпросмотр";
        Rect area = SystemParameters.WorkArea;
        Width = Math.Min(620, area.Width - 24);
        Height = Math.Min(650, area.Height - 24);
        MinWidth = Math.Min(360, area.Width - 24);
        MinHeight = Math.Min(320, area.Height - 24);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0";
        FooterVersion.Text = version;
        VersionText.Text = "Установлена версия " + version;
        foreach (string state in new[] { "off", "on", "working", "attention" })
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PursovStudio;component/Assets/Tray-" + state + ".ico"))!.Stream;
            using var icon = new System.Drawing.Icon(stream);
            trayIcons[state] = (System.Drawing.Icon)icon.Clone();
        }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть PURSOV STUDIO", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        trayAction = new Forms.ToolStripMenuItem("Подключить", null, (_, _) => Dispatcher.BeginInvoke(RunMainActionAsync));
        menu.Items.Add(trayAction);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(ExitAsync));
        tray = new Forms.NotifyIcon { Icon = trayIcons["off"], Text = "PURSOV STUDIO", ContextMenuStrip = menu, Visible = !preview };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        core.Changed += QueueRefresh;
        core.InstallationCompleted += InstallationCompleted;
        Log.Written += LogWritten;
        RefreshView();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        if (startHidden && !preview) Hide();
        try { await RunUiAsync(core.InitializeAsync); }
        finally { initializing = false; if (!exiting) RefreshView(); }
    }

    private void QueueRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref refreshQueued, 0); RefreshView(); }, DispatcherPriority.Background);
    }

    private void InstallationCompleted()
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ExitAsync);
    }

    private void ThemeChanged()
    {
        displayedResults = Array.Empty<ProbeResult>();
        QueueRefresh();
    }

    private Brush Tone(bool? success) => (Brush)FindResource(success switch { true => "Accent", false => "Failure", null => "Muted" });

    private void RefreshView()
    {
        if (allowClose || Dispatcher.HasShutdownStarted) return;
        string state = uiError != null || core.NeedsSettingsRecovery ? "attention" : core.IsBusy ? "working" : core.IsConnected ? "on" : "off";
        if (trayState != state) { tray.Icon = trayIcons[state]; trayState = state; }
        string action = initializing && !core.IsBusy ? "Подготовка" : core.MainAction;
        trayAction.Text = action;
        trayAction.Enabled = !exiting && (!initializing || core.IsBusy);
        string tooltip = "PURSOV STUDIO — " + core.Status;
        tray.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        string status = uiError == null ? core.Status : "Действие не завершено";
        if (StatusText.Text != status)
        {
            StatusText.Text = status;
            UIElementAutomationPeer.CreatePeerForElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            if (initialized && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                StatusPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        StatusText.Foreground = (Brush)FindResource(uiError != null ? "Failure" : "Ink");
        DetailText.Text = uiError ?? core.Detail;
        ProgressText.Text = core.ProgressText;
        ProgressText.Visibility = string.IsNullOrWhiteSpace(core.ProgressText) ? Visibility.Collapsed : Visibility.Visible;
        MainButton.Content = action;
        MainButton.IsEnabled = trayAction.Enabled;
        bool canAct = !core.IsBusy && !exiting && !initializing;
        CheckButton.IsEnabled = canAct;
        FindStrategyButton.IsEnabled = canAct && core.IsConnected;
        TelegramButton.IsEnabled = canAct;
        MainTelegramButton.IsEnabled = canAct;
        InstallButton.IsEnabled = canAct;
        InstallButton.Visibility = core.IsInstalled || preview ? Visibility.Collapsed : Visibility.Visible;
        RecoveryButton.IsEnabled = canAct;
        RecoveryButton.Visibility = core.NeedsSettingsRecovery ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckButton.IsEnabled = canAct;
        UpdateApplyButton.IsEnabled = canAct && core.UpdateReady;
        UpdateNotice.Visibility = core.UpdateReady ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateText.Text != core.UpdateStatus)
        {
            UpdateText.Text = core.UpdateStatus;
            if (UpdatesSection.IsVisible) UIElementAutomationPeer.CreatePeerForElement(UpdateText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        AutoStartBox.IsChecked = core.AutoStart;
        AutoConnectBox.IsChecked = core.AutoConnect;
        AutoStartBox.IsEnabled = canAct;
        AutoConnectBox.IsEnabled = canAct;
        CheckedText.Text = core.LastChecked is { } time ? "Проверено " + time.ToLocalTime().ToString("dd.MM в HH:mm") : "Проверка ещё не запускалась";

        if (!displayedResults.SequenceEqual(core.Results))
        {
            displayedResults = core.Results.ToArray();
            SummaryItems.ItemsSource = UiPresentation.Summarize(displayedResults).Select(x => new SummaryRow(x, Tone(x.Success))).ToArray();
            ServiceItems.ItemsSource = displayedResults.Select(x => new ServiceRow(x, Tone(x.Success))).ToArray();
        }
        if (!displayedStrategies.SequenceEqual(core.Strategies))
        {
            displayedStrategies = core.Strategies.ToArray();
            StrategiesGrid.ItemsSource = displayedStrategies.Select(x => new StrategyRow(x)).ToArray();
        }
        StrategiesEmpty.Visibility = displayedStrategies.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!displayedConflicts.SequenceEqual(core.Conflicts))
        {
            displayedConflicts = core.Conflicts.ToArray();
            ConflictItems.ItemsSource = displayedConflicts;
        }
        ConflictPanel.Visibility = displayedConflicts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshLog();
    }

    private void LogWritten(string line)
    {
        lock (logGate)
        {
            logLines.Enqueue(line);
            while (logLines.Count > 600) logLines.Dequeue();
            logDirty = true;
        }
        if (logVisible) QueueRefresh();
    }

    private void RefreshLog()
    {
        if (!LogSection.IsVisible) return;
        lock (logGate)
        {
            if (!logDirty) return;
            bool follow = LogText.VerticalOffset >= LogText.ExtentHeight - LogText.ViewportHeight - 24;
            LogText.Text = string.Join(Environment.NewLine, logLines);
            LogEmpty.Visibility = logLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (follow) LogText.ScrollToEnd();
            logDirty = false;
        }
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        if (exiting) return;
        uiError = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Действие не выполнено: " + ex.Message);
            uiError = "Попробуйте ещё раз. Подробности ошибки доступны в журнале.";
        }
        RefreshView();
    }

    private Task RunUserActionAsync(Func<Task> action) => initializing || exiting ? Task.CompletedTask : RunUiAsync(action);
    private async void RunMainActionAsync()
    {
        if (exiting) return;
        if (core.IsBusy) { core.Cancel(); return; }
        if (!initializing) await RunUiAsync(core.ToggleAsync);
    }
    private void MainButton_Click(object sender, RoutedEventArgs e) => RunMainActionAsync();
    private async void CheckButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.CheckAsync);
    private async void FindStrategyButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.FindStrategyAsync);
    private async void TelegramButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.ConfigureTelegramAsync);
    private async void InstallButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.InstallAsync);
    private async void RecoveryButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.ResetSettingsAsync);
    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(() => core.CheckUpdatesAsync());
    private async void UpdateApplyButton_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(core.ApplyUpdatesAsync);
    private async void Preferences_Click(object sender, RoutedEventArgs e) => await RunUserActionAsync(() => { core.SavePreferences(AutoStartBox.IsChecked == true, AutoConnectBox.IsChecked == true); return Task.CompletedTask; });

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        bool open = DetailsPanel.Visibility != Visibility.Visible;
        DetailsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        logVisible = open && SectionPicker.SelectedIndex == 2;
        DetailsButton.Content = open ? "Скрыть подробности" : "Подробнее";
        if (open)
        {
            Height = Math.Min(860, SystemParameters.WorkArea.Height - 24);
            SectionPicker.Focus();
            DetailsPanel.BringIntoView();
            RefreshLog();
        }
        else Height = Math.Min(650, SystemParameters.WorkArea.Height - 24);
    }

    private void UpdateNotice_Click(object sender, RoutedEventArgs e)
    {
        if (DetailsPanel.Visibility != Visibility.Visible) DetailsButton_Click(sender, e);
        SectionPicker.SelectedIndex = 3;
        DetailsPanel.BringIntoView();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        DetailsButton_Click(sender, e);
        ShellScroll.ScrollToTop();
        DetailsButton.Focus();
    }

    private void SectionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiagnosticsSection is null) return;
        var sections = new FrameworkElement[] { DiagnosticsSection, StrategiesSection, LogSection, UpdatesSection, SettingsSection };
        for (int i = 0; i < sections.Length; i++) sections[i].Visibility = i == SectionPicker.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        logVisible = IsVisible && LogSection.IsVisible;
        RefreshLog();
    }

    private void SourceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme != "https" || e.Uri.Host != "github.com") return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { uiError = "Не удалось открыть браузер. Адрес проекта указан в инструкции."; Log.Write(ex.Message); RefreshView(); }
        e.Handled = true;
    }

    private void LicensesButton_Click(object sender, RoutedEventArgs e)
    {
        string file = Path.Combine(AppPaths.AppRoot, "THIRD_PARTY_LICENSES.md");
        try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { file } }); }
        catch (Win32Exception ex) { Log.Write(ex.Message); uiError = "Лицензии находятся в файле THIRD_PARTY_LICENSES.md в папке программы."; RefreshView(); }
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (tray is null) return;
        logVisible = IsVisible && LogSection.IsVisible;
        if (IsVisible) QueueRefresh();
        else StatusPanel.BeginAnimation(OpacityProperty, null);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (preview) ExitAsync();
        else Hide();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        QueueRefresh();
    }

    private async void ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        RefreshView();
        try { await core.DisposeAsync(); }
        catch (Exception ex) { Log.Write("Ошибка завершения: " + ex.Message); }
        finally
        {
            core.Changed -= QueueRefresh;
            core.InstallationCompleted -= InstallationCompleted;
            Log.Written -= LogWritten;
            palette.Changed -= ThemeChanged;
            palette.Dispose();
            tray.Visible = false;
            tray.Dispose();
            foreach (var icon in trayIcons.Values) icon.Dispose();
            allowClose = true;
            Close();
            Application.Current.Shutdown();
        }
    }

    private sealed record SummaryRow(string Name, string Text, Brush Brush, Geometry Glyph)
    {
        public override string ToString() => Name + ". " + Text;
        public SummaryRow(ServiceSummary row, Brush brush) : this(row.Name, row.Text, brush,
            Geometry.Parse(row.Success switch { true => "M1,7 L5,11 L13,2", false => "M2,2 L12,12 M12,2 L2,12", null => "M2,7 L12,7" })) { Glyph.Freeze(); }
    }
    private sealed record ServiceRow(string Service, string Status, string Detail, string Duration, Brush Brush)
    {
        public override string ToString() => Service + ". " + Status;
        public ServiceRow(ProbeResult result, Brush brush) : this(result.Service, result.Status, result.Detail,
            result.Milliseconds > 0 ? $"{result.Milliseconds:N0} мс" : "", brush) { }
    }
    private sealed record StrategyRow(string Name, string Coverage, string Latency)
    {
        public override string ToString() => Name + ". " + Coverage + ". " + Latency;
        public StrategyRow(StrategyResult result) : this(result.Id, $"{result.Passed} из {result.Checked}", result.Milliseconds > 0 ? $"{result.Milliseconds:N0} мс" : "—") { }
    }
}
