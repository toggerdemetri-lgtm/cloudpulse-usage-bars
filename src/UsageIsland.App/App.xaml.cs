using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using UsageIsland.Core;
using Forms = System.Windows.Forms;

namespace UsageIsland.App;

public partial class App : Application
{
    private readonly MetricCache cache = new();
    private readonly RefreshGate refreshGate = new();
    private readonly CloudMonitorService provider = new();
    private readonly StartupRegistration startup = new();
    private IslandSettings settings = new();
    private CloudCredentials? credentials;
    private ConfigurationStore store = null!;
    private IslandWindow widget = null!;
    private SettingsWindow? settingsWindow;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private DispatcherTimer? pollTimer, renderTimer;
    private CancellationTokenSource? activePoll;
    private Mutex? instanceMutex;
    private bool ownsMutex, stopping, pendingRefresh, canPersistPosition;
    private int version;
    private string? warning;
    private string? hostWarning;
    private string? startupWarning;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && (e.Args[0] == "--desktop-probe" || e.Args[0] == "--edge-probe" || e.Args[0] == "--taskbar-probe"))
        {
            using var iconProbe = CreateIcon();
            var initial = e.Args[0] switch { "--edge-probe" => new IslandSettings { ScreenX = -10000, ScreenY = -10000 }, "--taskbar-probe" => new IslandSettings { ScreenX = 400, ScreenY = 100000, DisplayMode = WidgetMode.Taskbar }, _ => null };
            var probe = new IslandWindow(initial); MainWindow = probe; probe.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(probe.DesktopProbe(), new JsonSerializerOptions { WriteIndented = true }));
                probe.SavePreview(Path.ChangeExtension(e.Args[1], ".png")); Shutdown();
            };
            timer.Start(); return;
        }
        instanceMutex = new Mutex(true, "Local\\UsageIsland.Desktop.v1", out ownsMutex);
        bool fromAutostart = e.Args.Contains("--autostart");
        if (!ownsMutex) { if (!fromAutostart) MessageBox.Show("云脉已经在运行。请从桌面颜色条的右键菜单，或系统托盘打开设置。", "云脉·用量颜色条"); Shutdown(); return; }
        string configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageIsland");
        int configArg = Array.IndexOf(e.Args, "--config-dir");
        if (configArg >= 0 && configArg + 1 < e.Args.Length) configDirectory = Path.GetFullPath(e.Args[configArg + 1]);
        store = new ConfigurationStore(configDirectory);
        var loaded = store.Load(); settings = loaded.Settings; credentials = loaded.Credentials; warning = loaded.Warning; canPersistPosition = warning is null;
        // Repair a moved/upgraded portable executable on a manual launch, not during Run execution.
        if (settings.StartWithWindows && !fromAutostart && warning is null)
        {
            try { startup.SetEnabled(true, Environment.ProcessPath!); }
            catch { startupWarning = "开机启动路径更新失败，请打开设置重新保存。"; }
        }
        CreateTray(); CreateWidget();
        if (startupWarning is not null) tray?.ShowBalloonTip(5000, "云脉·用量颜色条", startupWarning, Forms.ToolTipIcon.Warning);
        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        pollTimer.Tick += async (_, _) => await RefreshAsync(); pollTimer.Start();
        renderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        renderTimer.Tick += (_, _) => Render(); renderTimer.Start();
        _ = RefreshAsync();
        if (e.Args.Contains("--settings") || (!fromAutostart && (credentials is null || warning is not null))) Dispatcher.BeginInvoke(OpenSettings);
    }
    private void CreateWidget()
    {
        if (stopping) return;
        widget = new IslandWindow(settings); MainWindow = widget;
        var createdWidget = widget;
        widget.SettingsRequested += OpenSettings;
        widget.RefreshRequested += async () => await RefreshAsync();
        widget.ExitRequested += ExitApplication;
        widget.PositionChanged += (x, y, mode) => { settings = settings with { ScreenX = x, ScreenY = y, DisplayMode = mode }; PersistPosition(); };
        widget.LockChanged += value => { settings = settings with { PositionLocked = value }; widget.ApplySettings(settings); PersistPosition(); };
        widget.HostStatusChanged += message =>
        {
            if (hostWarning != message && message is not null) tray?.ShowBalloonTip(5000, "云脉·用量颜色条", message, Forms.ToolTipIcon.Warning);
            hostWarning = message;
        };
        widget.PresentationResetRequested += () => Dispatcher.BeginInvoke(() => ReplacePresentation(createdWidget), DispatcherPriority.Background);
        widget.Closed += (_, _) =>
        {
            if (!stopping && ReferenceEquals(widget, createdWidget))
                Dispatcher.BeginInvoke(() => { if (!stopping && ReferenceEquals(widget, createdWidget)) CreateWidget(); }, DispatcherPriority.Background);
        };
        widget.Show(); Render();
    }
    private void ReplacePresentation(IslandWindow previous)
    {
        if (stopping || !ReferenceEquals(widget, previous)) return;
        // A layered WPF surface can retain invalid presentation state after moving
        // child -> popup -> child. Start a new surface through the initial attach
        // path; keep monitoring, configuration and cached samples in this process.
        CreateWidget();
        previous.Close();
    }
    private void CreateTray()
    {
        trayIcon = CreateIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "云脉·用量颜色条 · 右键设置", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        menu.Items.Add("立即刷新", null, (_, _) => Dispatcher.BeginInvoke(async () => await RefreshAsync()));
        menu.Items.Add("移回桌面（横向）", null, (_, _) => Dispatcher.BeginInvoke(() => widget.ReturnToDesktop()));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(OpenSettings);
    }
    private static System.Drawing.Icon CreateIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/App.ico")) ?? throw new InvalidOperationException("图标资源缺失。");
        using (resource.Stream)
        using (var icon = new System.Drawing.Icon(resource.Stream, new System.Drawing.Size(32, 32))) return (System.Drawing.Icon)icon.Clone();
    }
    private void OpenSettings()
    {
        if (settingsWindow is not null) { settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, credentials, store.FilePath, warning ?? hostWarning ?? startupWarning, ApplyConfiguration, store.LoadProfile);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show(); settingsWindow.Activate();
    }
    private void ApplyConfiguration(IslandSettings changed, CloudCredentials? newCredentials)
    {
        changed = changed with { ScreenX = settings.ScreenX, ScreenY = settings.ScreenY, DisplayMode = settings.DisplayMode };
        startup.Apply(changed.StartWithWindows, Environment.ProcessPath!, () => store.Save(changed, newCredentials));
        startupWarning = null;
        activePoll?.Cancel(); version++;
        settings = changed; credentials = newCredentials; warning = null; canPersistPosition = true;
        cache.Clear(); widget.ApplySettings(settings); Render(); _ = RefreshAsync();
    }
    private void PersistPosition()
    {
        if (!canPersistPosition) return;
        try { store.Save(settings, credentials); }
        catch { tray?.ShowBalloonTip(4000, "位置未保存", "无法写入配置目录，本次位置仅在运行期间保留。", Forms.ToolTipIcon.Warning); }
    }
    private void Render() => widget?.Render(cache, settings, warning ?? (credentials is null ? "未配置只读凭证，请右键打开设置" : null));
    private async Task RefreshAsync()
    {
        if (stopping) return;
        bool ran = await refreshGate.TryRunAsync(async () =>
        {
            var currentCredentials = credentials; var currentSettings = settings; var currentVersion = version;
            if (currentCredentials is null) { Render(); return; }
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45)); activePoll = cancel;
            try
            {
                var results = await provider.PollAsync(currentSettings, currentCredentials, cancel.Token);
                if (!stopping && currentVersion == version) cache.Apply(results);
            }
            catch (OperationCanceledException)
            {
                if (!stopping && currentVersion == version) cache.Apply(Enum.GetValues<MonitorMetric>().ToDictionary(metric => metric, _ => new MetricResult(null, "请求超时，下一周期重试")));
            }
            catch
            {
                if (!stopping && currentVersion == version) cache.Apply(Enum.GetValues<MonitorMetric>().ToDictionary(metric => metric, _ => new MetricResult(null, "读取失败，请检查网络和配置")));
            }
            finally { if (ReferenceEquals(activePoll, cancel)) activePoll = null; if (!stopping) Render(); }
        });
        if (!ran) pendingRefresh = true;
        else if (pendingRefresh && !stopping) { pendingRefresh = false; await RefreshAsync(); }
    }
    private void ExitApplication() { stopping = true; activePoll?.Cancel(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        stopping = true; activePoll?.Cancel(); pollTimer?.Stop(); renderTimer?.Stop();
        tray?.Dispose(); trayIcon?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
