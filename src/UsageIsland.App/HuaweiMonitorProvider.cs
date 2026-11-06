using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HuaweiCloud.SDK.Ces.V1;
using HuaweiCloud.SDK.Ces.V1.Model;
using HuaweiCloud.SDK.Core.Auth;
using HuaweiCloud.SDK.Core;
using UsageIsland.Core;

namespace UsageIsland.App;

public sealed class HuaweiMonitorProvider : ICloudMonitorProvider
{
    private const string Failure = "华为云监控读取失败，请检查凭证、项目、地域、只读权限及网络。";
    private readonly Func<ShowMetricDataRequest, CancellationToken, Task<ShowMetricDataResponse>>? query;
    private readonly Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>>? list;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeSpan queryTimeout = TimeSpan.FromSeconds(10);

    public HuaweiMonitorProvider() { now = () => DateTimeOffset.UtcNow; }
    public HuaweiMonitorProvider(Func<ShowMetricDataRequest, CancellationToken, Task<ShowMetricDataResponse>> query, Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>> list, Func<DateTimeOffset>? now = null, TimeSpan? queryTimeout = null)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        if (queryTimeout.HasValue)
        {
            if (queryTimeout.Value <= TimeSpan.Zero || queryTimeout.Value > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(queryTimeout));
            this.queryTimeout = queryTimeout.Value;
        }
    }

    public static IReadOnlyList<MetricValue> ParsePoints(ShowMetricDataResponse response, bool network, string? device, int periodSeconds)
    {
        var values = new List<MetricValue>();
        foreach (var point in response.Datapoints ?? new())
        {
            if (point?.Average is not double value || !double.IsFinite(value) || value < 0 || (!network && value > 100) || point.Timestamp is not long time || time < 0 || time > 253402300799999) continue;
            var normalized = network ? value * 8 : value;
            if (!double.IsFinite(normalized)) continue;
            values.Add(new(normalized, DateTimeOffset.FromUnixTimeMilliseconds(time), device, periodSeconds));
        }
        return values;
    }

    public async Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token)
    {
        if (settings.Vendor != CloudVendor.Huawei || credentials.Vendor != CloudVendor.Huawei)
            throw new ArgumentException("华为云监控需要对应厂商的配置及凭证。");
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) || string.IsNullOrWhiteSpace(credentials.AccessKeySecret) ||
            settings.ProjectId is not { Length: 32 } || settings.ProjectId.Any(c => !Uri.IsHexDigit(c)) || !Guid.TryParse(settings.InstanceId, out _) ||
            string.IsNullOrWhiteSpace(settings.RegionId) || settings.RegionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("华为云需要有效的凭证、实例、地域及32位项目 ID。");
        if (token.IsCancellationRequested) return Enum.GetValues<MonitorMetric>().ToDictionary(m => m, _ => new MetricResult(null, "本项读取已取消，下一周期重试。"));

        var end = now();
        var read = query;
        var discover = list;
        if (read is null || discover is null)
        {
            try
            {
                // Supplying both project and endpoint avoids automatic IAM project lookup and credential providers.
                var auth = new BasicCredentials(credentials.AccessKeyId, credentials.AccessKeySecret, settings.ProjectId);
                if (!string.IsNullOrWhiteSpace(credentials.SessionToken)) auth.WithSecurityToken(credentials.SessionToken);
                var http = HttpConfig.GetDefaultConfig();
                http.Timeout = 10;
                var client = CesAsyncClient.NewBuilder().WithCredential(auth)
                    .WithEndPoint("https://ces." + settings.RegionId + ".myhuaweicloud.com")
                    .WithHttpConfig(http).Build();
                read = (request, cancellation) => client.ShowMetricDataAsync(request).WaitAsync(cancellation);
                discover = (request, cancellation) => client.ListMetricsAsync(request).WaitAsync(cancellation);
            }
            catch { return Enum.GetValues<MonitorMetric>().ToDictionary(m => m, _ => new MetricResult(null, Failure)); }
        }

        // Run each metric independently so unavailable Agent data does not hide basic ECS monitoring.
        var entries = await Task.WhenAll(Enum.GetValues<MonitorMetric>().Select(async metric =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(queryTimeout);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                var value = metric == MonitorMetric.Disk
                    ? await ReadDisksAsync(settings, end, read, discover, deadline.Token).WaitAsync(deadline.Token)
                    : MetricRules.Latest(await ReadPointsAsync(metric, settings, end, null, read, deadline.Token).WaitAsync(deadline.Token));
                return new KeyValuePair<MonitorMetric, MetricResult>(metric, value is null
                    ? new(null, metric is MonitorMetric.Memory or MonitorMetric.Disk ? "暂无指标，请检查 CES Agent、磁盘维度及设备设置。" : "暂无基础监控指标，请检查实例、地域及云监控支持情况。")
                    : new(value));
            }
            catch (OperationCanceledException) { return new KeyValuePair<MonitorMetric, MetricResult>(metric, new(null, "本项读取超时，下一周期重试。")); }
            catch (IncompleteDiskException) { return new KeyValuePair<MonitorMetric, MetricResult>(metric, new(null, "磁盘指标不完整，请检查 CES Agent 与每个挂载点的数据。")); }
            catch { return new KeyValuePair<MonitorMetric, MetricResult>(metric, new(null, Failure)); }
        }));
        return entries.ToDictionary(p => p.Key, p => p.Value);
    }

    private static async Task<MetricValue?> ReadDisksAsync(IslandSettings settings, DateTimeOffset end,
        Func<ShowMetricDataRequest, CancellationToken, Task<ShowMetricDataResponse>> read,
        Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>> discover, CancellationToken token)
    {
        var dimensions = new Dictionary<string, List<MetricsDimensionResp>>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        int seen = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await discover(new ListMetricsRequest
            {
                Namespace = "AGT.ECS", MetricName = "disk_usedPercent", Dim0 = "instance_id," + settings.InstanceId,
                Limit = 1000, Start = cursor, Order = ListMetricsRequest.OrderEnum.ASC
            }, token).WaitAsync(token);
            var metrics = page.Metrics ?? new();
            seen += metrics.Count;
            foreach (var metric in metrics)
            {
                if (metric.Namespace != "AGT.ECS" || metric.MetricName != "disk_usedPercent" || metric.Dimensions is not { Count: >= 2 and <= 4 } dims ||
                    dims.Count(d => d.Name == "instance_id" && d.Value == settings.InstanceId) != 1 || dims.Count(d => d.Name == "mount_point") != 1 ||
                    dims.Any(d => string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Value)) || dims.Select(d => d.Name).Distinct().Count() != dims.Count) continue;
                var mount = dims.Single(d => d.Name == "mount_point").Value;
                if (string.IsNullOrWhiteSpace(settings.DiskDevice) || settings.DiskDevice == mount) dimensions[mount] = dims;
            }
            if (page.MetaData?.Total is int total && seen >= total) break;
            // Empty final pages are permitted. Nonempty pages with a marker must be followed even if short.
            if (metrics.Count == 0)
            {
                if (page.MetaData?.Total is int expected && seen < expected) throw new InvalidOperationException("Incomplete metric discovery");
                break;
            }
            cursor = page.MetaData?.Marker;
            if (string.IsNullOrEmpty(cursor))
            {
                if (page.MetaData?.Total is int expected && seen < expected || metrics.Count == 1000) throw new InvalidOperationException("Incomplete metric discovery");
                break;
            }
            if (!cursors.Add(cursor) || cursors.Count >= 100) throw new InvalidOperationException("Invalid metric pagination");
        } while (true);

        var samples = new List<MetricValue>();
        bool failed = false;
        foreach (var pair in dimensions)
        {
            try
            {
                var diskSamples = await ReadPointsAsync(MonitorMetric.Disk, settings, end, pair, read, token);
                if (diskSamples.Count == 0) failed = true;
                samples.AddRange(diskSamples);
            }
            catch (OperationCanceledException) { throw; }
            catch { failed = true; }
        }
        // A missing disk could be the fullest disk: do not silently report a reassuring partial maximum.
        if (failed) throw new IncompleteDiskException();
        return MetricRules.SelectDisk(samples, settings.DiskDevice);
    }

    private static async Task<IReadOnlyList<MetricValue>> ReadPointsAsync(MonitorMetric metric, IslandSettings settings, DateTimeOffset end,
        KeyValuePair<string, List<MetricsDimensionResp>>? disk,
        Func<ShowMetricDataRequest, CancellationToken, Task<ShowMetricDataResponse>> read, CancellationToken token)
    {
        var agent = metric is MonitorMetric.Memory or MonitorMetric.Disk;
        var period = agent ? 60 : 300;
        var name = metric switch
        {
            MonitorMetric.Cpu => "cpu_util", MonitorMetric.Memory => "mem_usedPercent", MonitorMetric.Disk => "disk_usedPercent",
            MonitorMetric.NetworkIn => "network_incoming_bytes_aggregate_rate", MonitorMetric.NetworkOut => "network_outgoing_bytes_aggregate_rate",
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
        var dims = disk?.Value.Select(d => d.Name + "," + d.Value).ToArray() ?? new[] { "instance_id," + settings.InstanceId };
        var response = await read(new ShowMetricDataRequest
        {
            Namespace = agent ? "AGT.ECS" : "SYS.ECS", MetricName = name,
            Dim0 = dims[0], Dim1 = dims.Length > 1 ? dims[1] : null, Dim2 = dims.Length > 2 ? dims[2] : null, Dim3 = dims.Length > 3 ? dims[3] : null,
            Filter = ShowMetricDataRequest.FilterEnum.AVERAGE, Period = new ShowMetricDataRequest.PeriodEnum(period),
            From = end.AddMinutes(-10).ToUnixTimeMilliseconds(), To = end.ToUnixTimeMilliseconds()
        }, token).WaitAsync(token);
        if (response.MetricName != name) throw new InvalidOperationException("Unexpected metric response");
        return ParsePoints(response, metric is MonitorMetric.NetworkIn or MonitorMetric.NetworkOut, disk?.Key, period);
    }

    private sealed class IncompleteDiskException : Exception { }
}
