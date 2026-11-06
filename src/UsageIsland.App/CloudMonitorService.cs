using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UsageIsland.Core;

namespace UsageIsland.App;

public sealed class CloudMonitorService : ICloudMonitorProvider
{
    public Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token)
    {
        if (settings.Validate().Count > 0 || credentials.Vendor != settings.Vendor)
            return Task.FromResult<IReadOnlyDictionary<MonitorMetric, MetricResult>>(Enum.GetValues<MonitorMetric>().ToDictionary(m => m, _ => new MetricResult(null, "配置或凭证所属厂商不匹配，请打开设置重新填写。")));
        ICloudMonitorProvider provider = settings.Vendor switch
        {
            CloudVendor.Aliyun => new AliyunMonitorProvider(), CloudVendor.Tencent => new TencentMonitorProvider(),
            CloudVendor.Huawei => new HuaweiMonitorProvider(), CloudVendor.Aws => new AwsMonitorProvider(),
            _ => throw new ArgumentException("云厂商无效。")
        };
        return provider.PollAsync(settings, credentials, token);
    }
}
