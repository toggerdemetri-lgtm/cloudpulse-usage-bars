using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlibabaCloud.SDK.Cms20190101.Models;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.TeaUtil.Models;
using UsageIsland.Core;

namespace UsageIsland.App;

public sealed class AliyunMonitorProvider : ICloudMonitorProvider
{
    public async Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token)
    {
        if (settings.Vendor != CloudVendor.Aliyun || credentials.Vendor != CloudVendor.Aliyun)
            return Enum.GetValues<MonitorMetric>().ToDictionary(metric => metric, _ => new MetricResult(null, "云厂商与阿里云凭证不匹配，请重新保存设置。"));
        var client = new AlibabaCloud.SDK.Cms20190101.Client(new Config
        {
            AccessKeyId = credentials.AccessKeyId,
            AccessKeySecret = credentials.AccessKeySecret,
            RegionId = settings.RegionId,
            Endpoint = "metrics.cn-hangzhou.aliyuncs.com",
            Protocol = "https"
        });
        var end = DateTimeOffset.UtcNow;
        var reader = new MetricReader(async (name, cursor, cancellation) =>
        {
            try
            {
                var response = await client.DescribeMetricLastWithOptionsAsync(new DescribeMetricLastRequest
                {
                    Namespace = "acs_ecs_dashboard",
                    MetricName = name,
                    Dimensions = JsonSerializer.Serialize(new[] { new { instanceId = settings.InstanceId } }),
                    Period = "60",
                    StartTime = end.AddMinutes(-10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    EndTime = end.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Length = "1000",
                    NextToken = cursor
                }, new RuntimeOptions { ConnectTimeout = 5000, ReadTimeout = 10000, Autoretry = false }).WaitAsync(cancellation);
                if (response.Body?.Success != true) throw new MetricQueryException(SafeError(response.Body?.Code));
                return new MetricPage(response.Body.Datapoints ?? "[]", response.Body.NextToken);
            }
            catch (Tea.TeaException ex) { throw new MetricQueryException(SafeError(ex.Code)); }
        });
        return await reader.ReadAllAsync(settings, token);
    }

    // Never expose SDK messages, signed requests or exception payloads containing credentials.
    private static string SafeError(string? code) => code switch
    {
        "AccessForbidden" or "Forbidden" or "Forbidden.RAM" or "NoPermission" => "没有读取监控数据的权限，请检查 RAM 只读策略。",
        "InvalidAccessKeyId.NotFound" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidSecurityToken.Expired" => "凭证无效，请检查 AccessKey ID、Secret 及本机时间。",
        "Throttling.User" or "Throttling" => "请求受到限流，将在下一周期重试。",
        "ResourceNotFound" => "未找到实例或监控数据，请检查地域和实例 ID。",
        _ => "阿里云查询未成功，请检查凭证、权限、网络及 API 调用额度。"
    };
}
