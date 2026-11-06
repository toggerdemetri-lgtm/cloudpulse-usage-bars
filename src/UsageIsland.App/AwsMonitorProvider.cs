using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;
using UsageIsland.Core;
using Metric = Amazon.CloudWatch.Model.Metric;

namespace UsageIsland.App;

public sealed class AwsMonitorProvider : ICloudMonitorProvider
{
    private const int MaxPages = 10;
    private const int MaxAgentSeries = 64;
    private readonly Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>>? read;
    private readonly Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>>? list;
    private readonly Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
    private readonly TimeSpan queryTimeout = TimeSpan.FromSeconds(10);

    public AwsMonitorProvider() { }

    // The delegates replace only network I/O; parsing, validation and cancellation remain real.
    public AwsMonitorProvider(Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>> read,
        Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>> list, Func<DateTimeOffset>? clock = null, TimeSpan? queryTimeout = null)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        if (clock is not null) this.clock = clock;
        if (queryTimeout.HasValue)
        {
            if (queryTimeout.Value <= TimeSpan.Zero || queryTimeout.Value > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(queryTimeout));
            this.queryTimeout = queryTimeout.Value;
        }
    }

    public async Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token)
    {
        if (settings.Vendor != CloudVendor.Aws || credentials.Vendor != CloudVendor.Aws)
            return Failure("AWS 凭证与当前云厂商不匹配，请重新填写所选厂商凭证。");
        if (!ValidRegion(settings.RegionId) || string.IsNullOrWhiteSpace(settings.InstanceId) || !settings.InstanceId.StartsWith("i-", StringComparison.Ordinal))
            return Failure("请检查 AWS 地域和 EC2 实例 ID。");
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) || string.IsNullOrWhiteSpace(credentials.AccessKeySecret))
            return Failure("请填写 AWS Access Key ID 和 Secret Access Key。");
        if (token.IsCancellationRequested) return Failure("本项读取已取消，下一周期重试。");

        AmazonCloudWatchClient? client = null;
        try
        {
            Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>> query;
            Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>> discover;
            if (read is not null && list is not null) { query = read; discover = list; }
            else
            {
                // Explicit credentials prevent SDK fallback to environment, profiles or IMDS.
                AWSCredentials awsCredentials = string.IsNullOrWhiteSpace(credentials.SessionToken)
                    ? new BasicAWSCredentials(credentials.AccessKeyId, credentials.AccessKeySecret)
                    : new SessionAWSCredentials(credentials.AccessKeyId, credentials.AccessKeySecret, credentials.SessionToken);
                client = new AmazonCloudWatchClient(awsCredentials, new AmazonCloudWatchConfig
                {
                    RegionEndpoint = RegionEndpoint.GetBySystemName(settings.RegionId),
                    IgnoreConfiguredEndpointUrls = true,
                    UseHttp = false,
                    Timeout = TimeSpan.FromSeconds(5),
                    MaxErrorRetry = 0,
                    DisableLogging = true,
                    LogMetrics = false,
                    LogResponse = false
                });
                query = client.GetMetricStatisticsAsync;
                discover = client.ListMetricsAsync;
            }
            var end = clock();
            var results = await Task.WhenAll(Enum.GetValues<MonitorMetric>().Select(async metric =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(queryTimeout);
                try
                {
                    var value = metric is MonitorMetric.Memory or MonitorMetric.Disk
                        ? await ReadAgentAsync(metric, settings, end, query, discover, deadline.Token).WaitAsync(deadline.Token)
                        : await ReadEc2Async(metric, settings, end, query, deadline.Token).WaitAsync(deadline.Token);
                    return new KeyValuePair<MonitorMetric, MetricResult>(metric, value);
                }
                catch (OperationCanceledException) { return new(metric, new(null, "本项读取超时或已取消，下一周期重试。")); }
                catch (QueryLimitException) { return new(metric, new(null, "AWS 指标分页或维度数量超出限制，请缩小 CloudWatch Agent 指标范围。")); }
                catch (AmazonServiceException ex) { return new(metric, new(null, SafeError(ex.ErrorCode))); }
                catch { return new(metric, new(null, SafeError(null))); }
            }));
            return results.ToDictionary(p => p.Key, p => p.Value);
        }
        catch { return Failure(SafeError(null)); }
        finally { client?.Dispose(); }
    }

    private static async Task<MetricResult> ReadEc2Async(MonitorMetric metric, IslandSettings settings, DateTimeOffset end,
        Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>> query, CancellationToken token)
    {
        int period = settings.AwsDetailedMonitoring ? 60 : 300;
        // A detailed-monitoring instance queried at 300s can have a partially filled
        // current bucket. Dividing that partial Sum by 300 would understate the rate.
        end = DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds() / period * period);
        bool network = metric is MonitorMetric.NetworkIn or MonitorMetric.NetworkOut;
        var request = Request("AWS/EC2", metric switch
        {
            MonitorMetric.Cpu => "CPUUtilization",
            MonitorMetric.NetworkIn => "NetworkIn",
            MonitorMetric.NetworkOut => "NetworkOut",
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        }, [new Dimension { Name = "InstanceId", Value = settings.InstanceId }], end, period, network);
        var response = await query(request, token).WaitAsync(token);
        var sample = MetricRules.Latest(Values(response, end, period, network));
        return sample is null ? new(null, "暂无 AWS EC2 指标，请检查地域、实例 ID 和监控采样周期。") : new(sample);
    }

    private static async Task<MetricResult> ReadAgentAsync(MonitorMetric metric, IslandSettings settings, DateTimeOffset end,
        Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>> query,
        Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>> discover, CancellationToken token)
    {
        string name = metric == MonitorMetric.Memory ? "mem_used_percent" : "disk_used_percent";
        var series = new List<Metric>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        for (int page = 0; ; page++)
        {
            token.ThrowIfCancellationRequested();
            var response = await discover(new ListMetricsRequest
            {
                Namespace = "CWAgent", MetricName = name, NextToken = next,
                Dimensions = [new DimensionFilter { Name = "InstanceId", Value = settings.InstanceId }]
            }, token).WaitAsync(token);
            foreach (var descriptor in response.Metrics ?? [])
            {
                var dimensions = descriptor.Dimensions;
                if (descriptor.Namespace != "CWAgent" || descriptor.MetricName != name || dimensions is null || dimensions.Count is < 1 or > 30 ||
                    dimensions.Any(d => string.IsNullOrEmpty(d.Name) || string.IsNullOrEmpty(d.Value)) ||
                    dimensions.GroupBy(d => d.Name, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
                    !dimensions.Any(d => d.Name == "InstanceId" && d.Value == settings.InstanceId)) continue;
                if (metric == MonitorMetric.Disk)
                {
                    if (DiskIdentity(descriptor) is null) continue;
                    if (!string.IsNullOrWhiteSpace(settings.DiskDevice) && !dimensions.Any(d => d.Name is "path" or "device" && d.Value == settings.DiskDevice)) continue;
                }
                // Length prefixes avoid delimiter collisions in arbitrary agent dimensions.
                var fingerprint = string.Concat(dimensions.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => $"{d.Name.Length}:{d.Name}{d.Value.Length}:{d.Value}"));
                if (!fingerprints.Add(fingerprint)) continue;
                series.Add(descriptor);
                if (series.Count > MaxAgentSeries) throw new QueryLimitException();
            }
            next = response.NextToken;
            if (string.IsNullOrEmpty(next)) break;
            if (page + 1 >= MaxPages || !cursors.Add(next)) throw new QueryLimitException();
        }
        var values = new List<MetricValue>();
        foreach (var descriptor in series)
        {
            token.ThrowIfCancellationRequested();
            var response = await query(Request("CWAgent", name, descriptor.Dimensions, end, 60, false), token).WaitAsync(token);
            var seriesValues = Values(response, end, 60, false, metric == MonitorMetric.Disk ? DiskIdentity(descriptor) : null).ToList();
            if (metric == MonitorMetric.Disk && seriesValues.Count == 0)
                return new(null, "AWS 磁盘指标不完整：已发现的选中磁盘暂无有效采样，请检查 CloudWatch Agent 及磁盘设置。");
            values.AddRange(seriesValues);
        }
        // Device aliases were filtered above; the returned identity consistently uses path first.
        var sample = metric == MonitorMetric.Disk ? MetricRules.SelectDisk(values, null) : MetricRules.Latest(values);
        return sample is null
            ? new(null, "暂无指标，请检查官方 CloudWatch Agent、CWAgent 命名空间、InstanceId 维度及磁盘设置（新指标可延迟15分钟出现）。")
            : new(sample);
    }

    private static GetMetricStatisticsRequest Request(string ns, string name, List<Dimension> dimensions, DateTimeOffset end, int period, bool network) => new()
    {
        Namespace = ns, MetricName = name,
        Dimensions = dimensions.Select(d => new Dimension { Name = d.Name, Value = d.Value }).ToList(),
        StartTime = end.AddMinutes(-15).UtcDateTime, EndTime = end.UtcDateTime, Period = period,
        Statistics = [network ? "Sum" : "Average"], Unit = network ? StandardUnit.Bytes : StandardUnit.Percent
    };

    private static IEnumerable<MetricValue> Values(GetMetricStatisticsResponse response, DateTimeOffset end, int period, bool network, string? device = null)
    {
        foreach (var point in response.Datapoints ?? [])
        {
            double? raw = network ? point.Sum : point.Average;
            if (!raw.HasValue || !double.IsFinite(raw.Value) || raw.Value < 0 || (!network && raw.Value > 100) || !point.Timestamp.HasValue) continue;
            if (point.Unit is not null && point.Unit != (network ? StandardUnit.Bytes : StandardUnit.Percent)) continue;
            var sampledAt = new DateTimeOffset(DateTime.SpecifyKind(point.Timestamp.Value, DateTimeKind.Utc));
            if (sampledAt < end.AddMinutes(-15) || sampledAt >= end) continue;
            double value = network ? raw.Value / period * 8 : raw.Value;
            if (double.IsFinite(value)) yield return new(value, sampledAt, device, period);
        }
    }

    private static string? DiskIdentity(Metric metric) => metric.Dimensions.FirstOrDefault(d => d.Name == "path")?.Value ?? metric.Dimensions.FirstOrDefault(d => d.Name == "device")?.Value;
    private static bool ValidRegion(string region) => !string.IsNullOrWhiteSpace(region) && region.Length <= 64 && region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && region.Contains('-');
    private static IReadOnlyDictionary<MonitorMetric, MetricResult> Failure(string message) => Enum.GetValues<MonitorMetric>().ToDictionary(m => m, _ => new MetricResult(null, message));
    private sealed class QueryLimitException : Exception { }
    // Only fixed messages cross the UI boundary; SDK messages, requests and payloads stay private.
    private static string SafeError(string? code) => code switch
    {
        "AccessDenied" or "AccessDeniedException" or "UnauthorizedOperation" => "没有读取 AWS CloudWatch 指标的权限，请检查 IAM 只读策略。",
        "InvalidClientTokenId" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "ExpiredTokenException" or "UnrecognizedClientException" => "AWS 凭证或会话令牌无效，请检查凭证、令牌有效期及本机时间。",
        "Throttling" or "ThrottlingException" or "LimitExceededException" => "AWS 监控请求受到限流，将在下一周期重试。",
        _ => "AWS 监控查询未成功，请检查凭证、地域、权限及网络。"
    };
}
