using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Text;
using System.Globalization;
using UsageIsland.Core;
using System.IO;
using System.Windows.Media.Imaging;
using UsageIsland.App.Native;

namespace UsageIsland.App;

public partial class IslandWindow : Window
{
    private DesktopHost? host;
    private readonly Border[] tracks = new Border[4];
    private readonly Border[] fills = new Border[4];
    private readonly Border[] targets = new Border[4];
    private IslandSettings settings;
    private bool dragging;
    private WidgetLayout layout = DockGeometry.DesktopLayout;
    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;
    public event Action<int, int, WidgetMode>? PositionChanged;
    public event Action<bool>? LockChanged;
    public event Action<string?>? HostStatusChanged;
    public event Action? PresentationResetRequested;
    public IslandWindow(IslandSettings? initial = null)
    {
        InitializeComponent();
        settings = initial ?? new();
        var names = new[] { "CPU", "内存", "云盘", "网络" };
        for (int i = 0; i < 4; i++)
        {
            fills[i] = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            tracks[i] = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brush("#687282"), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, Child = fills[i] };
            targets[i] = new Border { Background = Brushes.Transparent, Child = tracks[i], ToolTip = names[i] + "\n未配置，尚无数据" };
            System.Windows.Automation.AutomationProperties.SetName(targets[i], names[i]);
            ToolTipService.SetInitialShowDelay(targets[i], 250); ToolTipService.SetShowDuration(targets[i], 60000);
            Bars.Children.Add(targets[i]);
            int index = i; tracks[i].SizeChanged += (_, _) => { if (fills[index].Tag is double fill) SetFill(index, fill); };
        }
        ContextMenu = MakeMenu();
        MouseLeftButtonDown += StartDrag;
        MouseMove += MoveDrag;
        MouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) => { if (!DesktopHost.LeftButtonDown) FinishDrag(); };
        ApplyLayout(settings.DisplayMode == WidgetMode.Taskbar ? new(WidgetMode.Taskbar, 42, 36) : DockGeometry.DesktopLayout);
        SourceInitialized += (_, _) => { host = new DesktopHost(this, settings.ScreenX, settings.ScreenY, settings.DisplayMode); host.LayoutChanged += ApplyLayout; host.DragReleased += FinishDrag; host.DragStateChanged += active => Background = active ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent; host.AttachmentChanged += () => HostStatusChanged?.Invoke(host.Error); host.Attach(); };
        Closed += (_, _) => host?.Dispose();
    }
    public object DesktopProbe() => new { attached = host?.IsAttached, parentClass = host?.ParentClass, error = host?.Error, width = ActualWidth, height = ActualHeight, screenX = host?.Position.X, screenY = host?.Position.Y, mode = host?.Mode.ToString() };
    public void SavePreview(string path)
    {
        var image = new RenderTargetBitmap((int)Math.Round(layout.Width * 2), (int)Math.Round(layout.Height * 2), 192, 192, PixelFormats.Pbgra32); image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public void ApplySettings(IslandSettings value) { settings = value; ContextMenu = MakeMenu(); }
    private void ApplyLayout(WidgetLayout value)
    {
        layout = value; Width = value.Width; Height = value.Height;
        bool vertical = value.Mode == WidgetMode.Taskbar;
        ContentFrame.Padding = vertical ? new Thickness(4) : new Thickness(0);
        Bars.ColumnDefinitions.Clear();
        for (int i = 0; i < 4; i++)
        {
            if (!vertical && i > 0) Bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            Bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int i = 0; i < 4; i++)
        {
            Grid.SetColumn(targets[i], vertical ? i : i * 2);
            tracks[i].Width = vertical ? 4 : double.NaN;
            tracks[i].Height = vertical ? Math.Max(4, value.Height - 8) : 6;
            tracks[i].Margin = new Thickness(0);
            tracks[i].HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            fills[i].HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            fills[i].VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            SetFill(i, fills[i].Tag is double ratio ? ratio : 0);
        }
    }
    private void SetFill(int index, double ratio)
    {
        if (layout.Mode == WidgetMode.Taskbar) { fills[index].Width = 4; fills[index].Height = Math.Max(0, tracks[index].ActualHeight * ratio); }
        else { fills[index].Width = Math.Max(0, tracks[index].ActualWidth * ratio); fills[index].Height = 6; }
    }
    public void ReturnToDesktop()
    {
        if (dragging) FinishDrag();
        host?.ReturnToDesktop(); SavePosition(); PresentationResetRequested?.Invoke();
    }
    private void SavePosition() { if (host is not null) PositionChanged?.Invoke(host.Position.X, host.Position.Y, host.Mode); }
    private ContextMenu MakeMenu()
    {
        var menu = new ContextMenu();
        void Item(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("设置…", () => SettingsRequested?.Invoke()); Item("立即刷新", () => RefreshRequested?.Invoke());
        Item("移回桌面（横向）", ReturnToDesktop);
        var locked = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = settings.PositionLocked };
        locked.Click += (_, _) => LockChanged?.Invoke(locked.IsChecked); menu.Items.Add(locked);
        menu.Items.Add(new Separator()); Item("退出", () => ExitRequested?.Invoke()); return menu;
    }
    public void Render(MetricCache cache, IslandSettings current, string? unconfigured)
    {
        settings = current;
        var kinds = new[] { MonitorMetric.Cpu, MonitorMetric.Memory, MonitorMetric.Disk, MonitorMetric.NetworkOut };
        var networkName = CloudCatalog.NetworkName(current.Vendor);
        var names = new[] { "CPU 使用率", "内存使用率", "云盘使用率", networkName + "出站" };
        for (int i = 0; i < 4; i++)
        {
            var result = cache.Get(kinds[i]);
            var state = MetricRules.Evaluate(result.Sample, unconfigured ?? result.Error, DateTimeOffset.UtcNow, current, i == 3);
            var color = state.Tone switch { BarTone.Green => "#62DBA5", BarTone.Yellow => "#EBC561", BarTone.Red => "#F47D7B", _ => "#687282" };
            tracks[i].Background = Brush(state.Tone == BarTone.Gray ? color : "#343D4B");
            fills[i].Background = Brush(color); fills[i].Tag = state.Fill; SetFill(i, state.Fill);
            var tip = new StringBuilder(names[i]);
            tip.Append("\n").Append(CloudCatalog.Name(current.Vendor));
            if (state.Sample is null) tip.Append("\n—");
            else
            {
                tip.Append('\n').Append(i == 3 ? MetricRules.FormatSpeed(state.Sample.Value) : state.Sample.Value.ToString("F1", CultureInfo.InvariantCulture) + "%");
                if (i == 3 && current.BandwidthMbps is > 0) tip.Append(" · ").Append((state.Sample.Value / (current.BandwidthMbps.Value * 10000)).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
                if (i == 2) tip.Append("\n设备：").Append(state.Sample.Device ?? "未标识");
                tip.Append("\n采样：").Append(state.Sample.SampledAt.ToLocalTime().ToString("MM-dd HH:mm:ss"));
                tip.Append("\n周期：").Append(state.Sample.PeriodSeconds).Append("秒");
                if (current.Vendor == CloudVendor.Tencent && i is 2 or 3) tip.Append("（周期内最大值）");
                else tip.Append("（周期平均值）");
            }
            if (state.Error is not null) tip.Append("\n").Append(state.Error).Append(state.Sample is null ? "" : "（以上为上次数据）");
            if (i == 3)
            {
                var incoming = cache.Get(MonitorMetric.NetworkIn);
                var inState = MetricRules.Evaluate(incoming.Sample, unconfigured ?? incoming.Error, DateTimeOffset.UtcNow, current, true);
                tip.Append('\n').Append(networkName).Append("入站：").Append(incoming.Sample is null ? "—" : MetricRules.FormatSpeed(incoming.Sample.Value));
                if (incoming.Sample is not null) tip.Append("\n入站采样：").Append(incoming.Sample.SampledAt.ToLocalTime().ToString("MM-dd HH:mm:ss"));
                if (inState.Error is not null) tip.Append("\n入站：").Append(inState.Error).Append(incoming.Sample is null ? "" : "（以上入站为上次数据）");
                tip.Append("\n参考带宽：").Append(current.BandwidthMbps?.ToString(CultureInfo.InvariantCulture) ?? "未配置").Append(" Mbps");
                if (current.Vendor is CloudVendor.Aws or CloudVendor.Huawei) tip.Append("\n网络包含内网流量，占比按参考上限计算。");
            }
            targets[i].ToolTip = tip.ToString();
            System.Windows.Automation.AutomationProperties.SetHelpText(targets[i], tip.ToString());
        }
    }
    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (settings.PositionLocked || host is null || !host.IsAttached) return;
        if (!host.BeginDrag(DesktopHost.CursorPosition())) return;
        dragging = true; CaptureMouse(); e.Handled = true;
    }
    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (!dragging || host is null) return;
        host.UpdateDrag(DesktopHost.CursorPosition());
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) => FinishDrag();
    private void FinishDrag()
    {
        if (!dragging) return;
        dragging = false; ReleaseMouseCapture();
        host?.UpdateDrag(DesktopHost.CursorPosition()); host?.EndDrag(); SavePosition();
        PresentationResetRequested?.Invoke();
    }
}
