using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.IO;
using UsageIsland.Core;

namespace UsageIsland.App;

public partial class SettingsWindow : Window
{
    private readonly IslandSettings original;
    private CloudCredentials? existing;
    private readonly Func<CloudVendor, LoadedConfiguration> loadProfile;
    private readonly Dictionary<CloudVendor, Draft> drafts = new();
    private CloudVendor selectedVendor;
    private bool ready;
    private sealed record VendorOption(CloudVendor Vendor, string Name);
    private sealed record Draft(string Instance, string Region, string Project, string Bandwidth, string Disk, string Key, string Secret, string Token, bool Detailed, bool Clear, CloudCredentials? Existing);
    private readonly Action<IslandSettings, CloudCredentials?> save;
    private readonly CancellationTokenSource lifetime = new();
    public SettingsWindow(IslandSettings settings, CloudCredentials? credentials, string path, string? warning, Action<IslandSettings, CloudCredentials?> save, Func<CloudVendor, LoadedConfiguration>? loadProfile = null)
    {
        InitializeComponent();
        original = settings; this.save = save;
        this.loadProfile = loadProfile ?? (vendor => new(CloudCatalog.Defaults(vendor), null, null));
        VendorBox.ItemsSource = Enum.GetValues<CloudVendor>().Select(v => new VendorOption(v, CloudCatalog.Name(v))).ToArray();
        selectedVendor = settings.Vendor;
        LoadProfile(settings, credentials);
        VendorBox.SelectedValue = settings.Vendor;
        YellowBox.Text = settings.YellowThreshold.ToString(CultureInfo.InvariantCulture); RedBox.Text = settings.RedThreshold.ToString(CultureInfo.InvariantCulture);
        LockBox.IsChecked = settings.PositionLocked;
        StartupBox.IsChecked = settings.StartWithWindows;
        ConfigPathText.Text = "配置位置：" + path;
        StatusText.Text = warning ?? "首次使用请填写只读凭证和实际带宽，再测试连接。";
        ready = true;
        Closed += (_, _) => lifetime.Cancel();
    }
    private void LoadProfile(IslandSettings profile, CloudCredentials? credentials)
    {
        existing = credentials?.Vendor == selectedVendor ? credentials : null;
        InstanceBox.Text = profile.InstanceId; RegionBox.Text = profile.RegionId;
        ProjectBox.Text = profile.ProjectId ?? ""; BandwidthBox.Text = profile.BandwidthMbps?.ToString(CultureInfo.InvariantCulture) ?? "";
        DiskBox.Text = profile.DiskDevice ?? ""; AccessKeyBox.Text = existing?.AccessKeyId ?? "";
        SecretBox.Clear(); TokenBox.Password = existing?.SessionToken ?? "";
        ClearCredentialsBox.IsChecked = false; AwsDetailedBox.IsChecked = profile.AwsDetailedMonitoring;
        UpdateLabels();
    }
    private void UpdateLabels()
    {
        InstanceLabel.Text = CloudCatalog.Name(selectedVendor) + " 实例 ID";
        AccessKeyLabel.Text = CloudCatalog.CredentialIdLabel(selectedVendor); SecretLabel.Text = CloudCatalog.CredentialSecretLabel(selectedVendor);
        ProjectPanel.Visibility = selectedVendor == CloudVendor.Huawei ? Visibility.Visible : Visibility.Collapsed;
        AwsDetailedBox.Visibility = selectedVendor == CloudVendor.Aws ? Visibility.Visible : Visibility.Collapsed;
        TokenPanel.Visibility = selectedVendor == CloudVendor.Aliyun ? Visibility.Collapsed : Visibility.Visible;
        BandwidthLabel.Text = CloudCatalog.NetworkName(selectedVendor) + "出站参考带宽（Mbps）";
        DiskBox.IsEnabled = selectedVendor != CloudVendor.Tencent;
        DiskLabel.Text = selectedVendor == CloudVendor.Tencent ? "腾讯云返回所有磁盘最高使用率，不支持固定设备" : selectedVendor == CloudVendor.Aws ? "固定磁盘 path 或 device（留空取最高；需要 CWAgent）" : selectedVendor == CloudVendor.Huawei ? "固定磁盘 mount_point（留空取最高；需要 CES Agent）" : "固定磁盘设备（留空显示使用率最高的磁盘）";
        CloudHint.Text = selectedVendor switch
        {
            CloudVendor.Tencent => "地域示例 ap-beijing，实例 ins-…；公网网络为1分钟最大速率。带宽按实际配置填写，例如5 Mbps填5。",
            CloudVendor.Huawei => "地域示例 cn-north-4；项目ID从“我的凭证 → API凭证”查看。CPU及网卡总流量按5分钟聚合，网络包含内网；内存、磁盘需要 CES Agent。带宽填用于比较的参考上限。",
            CloudVendor.Aws => "地域示例 us-east-1（中国区可用 cn-north-1）。默认基础监控5分钟；勾选仅声明已经开通详细监控，不会帮你开通。网卡包含内网，带宽填参考上限；内存、磁盘需要 CWAgent。",
            _ => "地域示例 cn-beijing；带宽按 ECS / EIP 实际公网出站配置填写，例如5 Mbps填5。内存、磁盘需要云监控插件。"
        };
        SecretHint.Text = existing is null ? "请在本机填写 Secret，不需要发送到聊天。" : "本厂商 Secret 已加密保存；留空保留，填写则替换。";
    }
    private void Vendor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || VendorBox.SelectedValue is not CloudVendor vendor || vendor == selectedVendor) return;
        drafts[selectedVendor] = new(InstanceBox.Text, RegionBox.Text, ProjectBox.Text, BandwidthBox.Text, DiskBox.Text, AccessKeyBox.Text, SecretBox.Password, TokenBox.Password, AwsDetailedBox.IsChecked == true, ClearCredentialsBox.IsChecked == true, existing);
        selectedVendor = vendor;
        if (drafts.TryGetValue(vendor, out var draft))
        {
            existing = draft.Existing;
            InstanceBox.Text = draft.Instance; RegionBox.Text = draft.Region; ProjectBox.Text = draft.Project; BandwidthBox.Text = draft.Bandwidth; DiskBox.Text = draft.Disk;
            AccessKeyBox.Text = draft.Key; SecretBox.Password = draft.Secret; TokenBox.Password = draft.Token; AwsDetailedBox.IsChecked = draft.Detailed; ClearCredentialsBox.IsChecked = draft.Clear;
            UpdateLabels(); SetStatus("已切换至" + CloudCatalog.Name(vendor) + "，保存后生效。", false);
        }
        else
        {
            var loaded = loadProfile(vendor); LoadProfile(loaded.Settings, loaded.Credentials);
            SetStatus(loaded.Warning ?? "已载入" + CloudCatalog.Name(vendor) + "配置，保存后生效。", loaded.Warning is not null);
        }
    }
    private (IslandSettings Settings, CloudCredentials? Credentials) ReadForm()
    {
        if (!double.TryParse(YellowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var yellow) || !double.TryParse(RedBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var red)) throw new ArgumentException("请填写有效的颜色阈值。");
        double? bandwidth = null;
        if (!string.IsNullOrWhiteSpace(BandwidthBox.Text))
        {
            if (!double.TryParse(BandwidthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) throw new ArgumentException("请填写有效的 Mbps 带宽数值。");
            bandwidth = n;
        }
        var settings = original with { Vendor = selectedVendor, ProjectId = string.IsNullOrWhiteSpace(ProjectBox.Text) ? null : ProjectBox.Text.Trim(), AwsDetailedMonitoring = AwsDetailedBox.IsChecked == true, InstanceId = InstanceBox.Text.Trim(), RegionId = RegionBox.Text.Trim(), BandwidthMbps = bandwidth, YellowThreshold = yellow, RedThreshold = red, DiskDevice = selectedVendor == CloudVendor.Tencent || string.IsNullOrWhiteSpace(DiskBox.Text) ? null : DiskBox.Text.Trim(), PositionLocked = LockBox.IsChecked == true, StartWithWindows = StartupBox.IsChecked == true };
        var errors = settings.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        var credentials = CredentialPolicy.Resolve(selectedVendor, AccessKeyBox.Text, SecretBox.Password, TokenBox.Password, existing, ClearCredentialsBox.IsChecked == true);
        if (credentials is not null && bandwidth is null) throw new ArgumentException("请填写实际出站或参考带宽（Mbps），用于计算网络条。");
        return (settings, credentials);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { var form = ReadForm(); save(form.Settings, form.Credentials); Close(); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); }
        catch { SetStatus("保存失败，请检查配置目录和当前用户启动项的写入权限。", true); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var form = ReadForm();
            if (form.Credentials is null) throw new ArgumentException("请先填写只读凭证，再测试连接。");
            TestButton.IsEnabled = false; SaveButton.IsEnabled = false; VendorBox.IsEnabled = false; SetStatus("正在读取" + CloudCatalog.Name(form.Settings.Vendor) + "监控数据…", false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var results = await new CloudMonitorService().PollAsync(form.Settings, form.Credentials, timeout.Token);
            string Label(MonitorMetric metric) => metric switch { MonitorMetric.Cpu => "CPU", MonitorMetric.Memory => "内存", MonitorMetric.Disk => "云盘", MonitorMetric.NetworkIn => CloudCatalog.NetworkName(form.Settings.Vendor) + "入站", _ => CloudCatalog.NetworkName(form.Settings.Vendor) + "出站" };
            bool invalid = false;
            var lines = results.Select(pair =>
            {
                bool network = pair.Key is MonitorMetric.NetworkIn or MonitorMetric.NetworkOut;
                var state = MetricRules.Evaluate(pair.Value.Sample, pair.Value.Error, DateTimeOffset.UtcNow, form.Settings, network);
                if (state.Error is not null) invalid = true;
                string value = state.Sample is null ? "—" : network ? MetricRules.FormatSpeed(state.Sample.Value) : state.Sample.Value.ToString("F1") + "%";
                return Label(pair.Key) + "：" + value + (state.Error is null ? "" : " · " + state.Error) + (state.Sample is null ? "" : " · 采样 " + state.Sample.SampledAt.ToLocalTime().ToString("HH:mm:ss") + " / " + state.Sample.PeriodSeconds + "秒");
            }).ToArray();
            SetStatus(string.Join("\n", lines), invalid);
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) SetStatus("读取超时，请检查网络后重试。", true); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); }
        catch { SetStatus("连接未成功，请检查网络、当前厂商凭证及监控读取权限。", true); }
        finally { TestButton.IsEnabled = true; SaveButton.IsEnabled = true; VendorBox.IsEnabled = true; }
    }
    private void SetStatus(string message, bool error) { StatusText.Text = message; StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 160, 153) : Color.FromRgb(175, 188, 208)); }
}
