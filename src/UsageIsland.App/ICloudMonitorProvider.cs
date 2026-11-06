using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UsageIsland.Core;

namespace UsageIsland.App;

public interface ICloudMonitorProvider
{
    Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> PollAsync(IslandSettings settings, CloudCredentials credentials, CancellationToken token);
}
