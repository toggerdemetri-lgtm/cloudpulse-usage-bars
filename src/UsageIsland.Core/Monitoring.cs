using System.Globalization;
using System.Text.Json;

namespace UsageIsland.Core;

public enum BarTone { Gray, Green, Yellow, Red }
public enum MonitorMetric { Cpu, Memory, Disk, NetworkIn, NetworkOut }
public enum WidgetMode { Desktop, Taskbar }
public enum CloudVendor { Aliyun, Tencent, Huawei, Aws }
public sealed record MetricValue(double Value, DateTimeOffset SampledAt, string? Device = null, int PeriodSeconds = 60);
public sealed record MetricResult(MetricValue? Sample, string? Error = null);
public sealed record BarState(double Fill, BarTone Tone, MetricValue? Sample, string? Error);

public sealed record IslandSettings
{
    public CloudVendor Vendor { get; init; }
    public string? ProjectId { get; init; }
    public bool AwsDetailedMonitoring { get; init; }
    public string InstanceId { get; init; } = "i-your-instance-id";
    public string RegionId { get; init; } = "cn-beijing";
    public double? BandwidthMbps { get; init; }
    public double YellowThreshold { get; init; } = 60;
    public double RedThreshold { get; init; } = 80;
    public string? DiskDevice { get; init; }
    public int? ScreenX { get; init; }
    public int? ScreenY { get; init; }
    public bool PositionLocked { get; init; }
    public bool StartWithWindows { get; init; }
    public WidgetMode DisplayMode { get; init; }
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(DisplayMode)) errors.Add("显示模式无效。");
        if (!Enum.IsDefined(Vendor)) errors.Add("云厂商无效。");
        if (string.IsNullOrWhiteSpace(InstanceId) || (Vendor is CloudVendor.Aliyun or CloudVendor.Aws && !InstanceId.StartsWith("i-", StringComparison.Ordinal)) || (Vendor == CloudVendor.Tencent && !InstanceId.StartsWith("ins-", StringComparison.Ordinal)) || (Vendor == CloudVendor.Huawei && !Guid.TryParse(InstanceId, out _))) errors.Add("请填写所选云厂商的正确实例 ID。");
        if (string.IsNullOrWhiteSpace(RegionId) || RegionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) errors.Add("请填写正确的地域 ID（字母、数字及短横线）。");
        if (Vendor == CloudVendor.Huawei && (string.IsNullOrWhiteSpace(ProjectId) || ProjectId.Length != 32 || ProjectId.Any(c => !Uri.IsHexDigit(c)))) errors.Add("华为云需要填写地域对应的32位项目 ID。");
        if (BandwidthMbps.HasValue && (!double.IsFinite(BandwidthMbps.Value) || BandwidthMbps.Value <= 0)) errors.Add("出站带宽必须是大于零的 Mbps 数值。");
        if (!double.IsFinite(YellowThreshold) || !double.IsFinite(RedThreshold) || YellowThreshold <= 0 || YellowThreshold >= RedThreshold || RedThreshold > 100) errors.Add("阈值须满足：0 < 黄色阈值 < 红色阈值 ≤ 100。");
        return errors;
    }
}

public static class MetricRules
{
    public static IReadOnlyList<MetricValue> Parse(string json, string instanceId)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("指标格式无效。");
        var results = new List<MetricValue>();
        foreach (var point in document.RootElement.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("instanceId", out var instance) || instance.GetString() != instanceId) continue;
            if (!point.TryGetProperty("Average", out var average) || !TryNumber(average, out var value) || !double.IsFinite(value) || value < 0) continue;
            if (!point.TryGetProperty("timestamp", out var timestamp) || !TryNumber(timestamp, out var time) || !double.IsFinite(time) || time < 0 || time > 253402300799999) continue;
            string? device = point.TryGetProperty("device", out var disk) && disk.ValueKind == JsonValueKind.String ? disk.GetString() : null;
            results.Add(new(value, DateTimeOffset.FromUnixTimeMilliseconds((long)time), device));
        }
        return results;
    }
    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out number) : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
    public static MetricValue? Latest(IEnumerable<MetricValue> values) => values.OrderByDescending(v => v.SampledAt).FirstOrDefault();
    public static MetricValue? SelectDisk(IEnumerable<MetricValue> values, string? device)
    {
        if (!string.IsNullOrWhiteSpace(device)) return Latest(values.Where(v => v.Device == device));
        return values.GroupBy(v => v.Device).Select(g => Latest(g)!).OrderByDescending(v => v.Value).FirstOrDefault();
    }
    public static BarState Evaluate(MetricValue? value, string? error, DateTimeOffset now, IslandSettings settings, bool network = false)
    {
        if (error is not null) return new(0, BarTone.Gray, value, error);
        if (value is null) return new(0, BarTone.Gray, null, "尚无数据");
        if (!double.IsFinite(value.Value) || value.Value < 0 || (!network && value.Value > 100)) return new(0, BarTone.Gray, value, "指标数值无效");
        if (value.PeriodSeconds is < 1 or > 3600) return new(0, BarTone.Gray, value, "采样周期无效");
        // Coarse EC2 samples timestamp the beginning of a completed bucket.
        int maxAge = value.PeriodSeconds <= 60 ? 180 : value.PeriodSeconds * 2 + 120;
        if ((now - value.SampledAt).TotalSeconds > maxAge) return new(0, BarTone.Gray, value, "数据已过期");
        if ((value.SampledAt - now).TotalSeconds > 60) return new(0, BarTone.Gray, value, "采样时间异常，请检查本机时间");
        if (network && (!settings.BandwidthMbps.HasValue || !double.IsFinite(settings.BandwidthMbps.Value) || settings.BandwidthMbps.Value <= 0)) return new(0, BarTone.Gray, value, "请在设置中填写实际出站带宽");
        var percent = network ? value.Value / (settings.BandwidthMbps!.Value * 1_000_000) * 100 : value.Value;
        var tone = percent >= settings.RedThreshold ? BarTone.Red : percent >= settings.YellowThreshold ? BarTone.Yellow : BarTone.Green;
        return new(Math.Clamp(percent / 100, 0, 1), tone, value, null);
    }
    public static string FormatSpeed(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => (bitsPerSecond / 1_000_000_000).ToString("F2", CultureInfo.InvariantCulture) + " Gbps",
        >= 1_000_000 => (bitsPerSecond / 1_000_000).ToString("F2", CultureInfo.InvariantCulture) + " Mbps",
        >= 1_000 => (bitsPerSecond / 1_000).ToString("F2", CultureInfo.InvariantCulture) + " Kbps",
        _ => bitsPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " bps"
    };
}

public sealed class RefreshGate
{
    private int running;
    public async Task<bool> TryRunAsync(Func<Task> refresh)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
        try { await refresh(); return true; }
        finally { Volatile.Write(ref running, 0); }
    }
}
