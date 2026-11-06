namespace UsageIsland.Core;

public sealed class MetricCache
{
    private readonly Dictionary<MonitorMetric, MetricResult> results = new();
    public MetricResult Get(MonitorMetric metric) => results.GetValueOrDefault(metric) ?? new(null, "未配置或尚未读取数据");
    public void Clear() => results.Clear();
    public void Apply(IReadOnlyDictionary<MonitorMetric, MetricResult> incoming)
    {
        foreach (var (metric, result) in incoming)
        {
            var previous = Get(metric);
            var sample = result.Sample;
            if (sample is null || previous.Sample?.SampledAt > sample.SampledAt) sample = previous.Sample;
            results[metric] = new(sample, result.Error);
        }
    }
}
