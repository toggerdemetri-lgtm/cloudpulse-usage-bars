using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TencentCloud.Common;
using TencentCloud.Common.Profile;
using TencentCloud.Monitor.V20180724;
using TencentCloud.Monitor.V20180724.Models;
using UsageIsland.Core;

namespace UsageIsland.App;

public sealed class TencentMonitorProvider : ICloudMonitorProvider
{
    private readonly Func<GetMonitorDataRequest, CancellationToken, Task<string>>? query;
    public TencentMonitorProvider() { }
    public TencentMonitorProvider(Func<GetMonitorDataRequest, CancellationToken, Task<GetMonitorDataResponse>> query)
        => this.query = async (request, token) => SerializeResponse(await query(request, token).ConfigureAwait(false));

    public async Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token)
    {
        if (credentials.Vendor != CloudVendor.Tencent || settings.Vendor != CloudVendor.Tencent)
            return Enum.GetValues<MonitorMetric>().ToDictionary(metric => metric, _ => new MetricResult(null, "云厂商与腾讯云凭证不匹配，请重新保存设置。"));
        var end = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(Enum.GetValues<MonitorMetric>().Select(async metric =>
        {
            MetricResult result;
            try
            {
                token.ThrowIfCancellationRequested();
                if (metric == MonitorMetric.Disk && !string.IsNullOrWhiteSpace(settings.DiskDevice))
                    result = new(null, "腾讯云当前仅提供所有磁盘使用率最大值，不支持指定磁盘；请清空磁盘设备设置。");
                else
                {
                    var request = new GetMonitorDataRequest
                    {
                        Namespace = "QCE/CVM",
                        MetricName = MetricName(metric),
                        Instances = new[] { new Instance { Dimensions = new[] { new Dimension { Name = "InstanceId", Value = settings.InstanceId } } } },
                        Period = 60,
                        StartTime = end.AddMinutes(-10).ToString("O", CultureInfo.InvariantCulture),
                        EndTime = end.ToString("O", CultureInfo.InvariantCulture),
                        SpecifyStatistics = IsMaximum(metric) ? 2 : 1
                    };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    var raw = await (query is not null
                        ? query(request, timeout.Token)
                        : QuerySdk(settings, credentials, request, timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
                    result = ParseRaw(raw, metric, settings.InstanceId);
                }
            }
            catch (OperationCanceledException) { result = new(null, "本项读取超时，下一周期重试"); }
            catch (TencentCloudSDKException ex) { result = new(null, SafeError(ex.ErrorCode)); }
            catch { result = new(null, "腾讯云监控读取失败，请检查网络、地域及配置。"); }
            return new KeyValuePair<MonitorMetric, MetricResult>(metric, result);
        })).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static Task<string> QuerySdk(IslandSettings settings, CloudCredentials credentials, GetMonitorDataRequest request, CancellationToken token)
    {
        var client = new RawMonitorClient(new Credential
        {
            SecretId = credentials.AccessKeyId,
            SecretKey = credentials.AccessKeySecret,
            Token = credentials.SessionToken
        }, settings.RegionId, new ClientProfile
        {
            HttpProfile = new HttpProfile { Endpoint = "monitor.tencentcloudapi.com", Protocol = HttpProfile.REQ_HTTPS, Timeout = 10 }
        });
        return client.Read(request).WaitAsync(token);
    }

    // The generated SDK DTO uses float?[] for Unix timestamps and loses seconds.
    // Keep SDK signing, HTTPS, API error handling and raw JSON, then parse time ourselves.
    private sealed class RawMonitorClient(Credential credential, string region, ClientProfile profile) : MonitorClient(credential, region, profile)
    {
        public Task<string> Read(GetMonitorDataRequest request) => InternalRequest(request, "GetMonitorData");
    }

    public static MetricResult Parse(GetMonitorDataResponse response, MonitorMetric metric, string instanceId)
        => ParseRaw(SerializeResponse(response), metric, instanceId);

    private static string SerializeResponse(GetMonitorDataResponse response) => JsonConvert.SerializeObject(new
    {
        Response = new
        {
            response.MetricName,
            DataPoints = response.DataPoints?.Select(point => new
            {
                point.Dimensions,
                // Widen stored float values before JSON serialization to preserve their exact value.
                Timestamps = point.Timestamps?.Select(value => (double?)value),
                Values = point.Values?.Select(value => (double?)value),
                MaxValues = point.MaxValues?.Select(value => (double?)value),
                AvgValues = point.AvgValues?.Select(value => (double?)value)
            })
        }
    });

    public static MetricResult ParseRaw(string response, MonitorMetric metric, string instanceId)
    {
        var missing = new MetricResult(null, metric is MonitorMetric.NetworkIn or MonitorMetric.NetworkOut
            ? "暂无公网指标，请检查地域、实例及公网 IP。"
            : "暂无指标，请检查地域、实例及腾讯云监控组件。");
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("Response", out var body) || body.ValueKind != JsonValueKind.Object ||
                body.TryGetProperty("Error", out _) || !body.TryGetProperty("MetricName", out var metricName) || metricName.GetString() != MetricName(metric) ||
                !body.TryGetProperty("DataPoints", out var points) || points.ValueKind != JsonValueKind.Array) return missing;
            var values = new List<MetricValue>();
            foreach (var point in points.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("Dimensions", out var dimensions) || dimensions.ValueKind != JsonValueKind.Array) continue;
                var ids = dimensions.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "InstanceId").ToArray();
                if (ids.Length != 1 || !ids[0].TryGetProperty("Value", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != instanceId) continue;
                if (!point.TryGetProperty("Timestamps", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array) continue;
                var aggregate = IsMaximum(metric) ? "MaxValues" : "AvgValues";
                if (!point.TryGetProperty(aggregate, out var samples) || samples.ValueKind != JsonValueKind.Array || samples.GetArrayLength() == 0)
                    if (!point.TryGetProperty("Values", out samples) || samples.ValueKind != JsonValueKind.Array) continue;
                if (timestamps.GetArrayLength() != samples.GetArrayLength()) continue;
                for (var i = 0; i < timestamps.GetArrayLength(); i++)
                {
                    if (!TryNumber(timestamps[i], out var seconds) || seconds < 0 || seconds > 253402300799 || Math.Truncate(seconds) != seconds ||
                        !TryNumber(samples[i], out var value) || value < 0 || (!IsNetwork(metric) && value > 100)) continue;
                    // Documented public network metric unit is decimal Mbps; core uses bit/s.
                    if (IsNetwork(metric)) value *= 1_000_000;
                    if (double.IsFinite(value)) values.Add(new(value, DateTimeOffset.FromUnixTimeSeconds((long)seconds)));
                }
            }
            var latest = MetricRules.Latest(values);
            return latest is null ? missing : new(latest);
        }
        catch { return new(null, "腾讯云监控数据格式无效，请稍后重试。"); }
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value);
    }
    private static bool IsNetwork(MonitorMetric metric) => metric is MonitorMetric.NetworkIn or MonitorMetric.NetworkOut;
    private static bool IsMaximum(MonitorMetric metric) => metric == MonitorMetric.Disk || IsNetwork(metric);
    private static string MetricName(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Cpu => "CpuUsage",
        MonitorMetric.Memory => "MemUsage",
        MonitorMetric.Disk => "CvmDiskUsage",
        MonitorMetric.NetworkIn => "WanIntraffic",
        MonitorMetric.NetworkOut => "WanOuttraffic",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
    // Only locally authored messages leave the adapter; SDK payloads may include signatures.
    private static string SafeError(string? code) => code switch
    {
        "UnauthorizedOperation" or "AuthFailure.UnauthorizedOperation" => "没有读取监控数据的权限，请检查腾讯云监控只读策略。",
        "AuthFailure.SecretIdNotFound" or "AuthFailure.SignatureFailure" or "AuthFailure.TokenFailure" or "AuthFailure.InvalidSecretId" or "AuthFailure.TokenExpired" => "腾讯云凭证无效，请检查 SecretId、SecretKey、临时令牌及本机时间。",
        "RequestLimitExceeded" => "腾讯云请求受到限流，将在下一周期重试。",
        "ResourceNotFound" => "未找到腾讯云实例或监控数据，请检查地域和实例 ID。",
        _ => "腾讯云查询未成功，请检查凭证、权限、网络及 API 调用额度。"
    };
}
