namespace UsageIsland.Core;

public sealed record MetricPage(string Datapoints, string? NextToken = null);
public sealed class MetricQueryException(string safeMessage) : Exception(safeMessage);

public sealed class MetricReader(Func<string, string?, CancellationToken, Task<MetricPage>> query)
{
    public async Task<IReadOnlyDictionary<MonitorMetric, MetricResult>> ReadAllAsync(IslandSettings settings, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(Enum.GetValues<MonitorMetric>().Select(async metric =>
        {
            MetricResult result;
            try { result = await ReadAsync(metric, settings, cancellationToken); }
            catch (OperationCanceledException) { result = new(null, "本项读取超时，下一周期重试"); }
            return new KeyValuePair<MonitorMetric, MetricResult>(metric, result);
        }));
        return results.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    public static string MetricName(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Cpu => "cpu_total",
        MonitorMetric.Memory => "memory_usedutilization",
        MonitorMetric.Disk => "diskusage_utilization",
        MonitorMetric.NetworkIn => "InternetInRate",
        MonitorMetric.NetworkOut => "InternetOutRate",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
    public async Task<MetricResult> ReadAsync(MonitorMetric metric, IslandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var values = new List<MetricValue>();
            var cursors = new HashSet<string>();
            string? token = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await query(MetricName(metric), token, cancellationToken);
                values.AddRange(MetricRules.Parse(string.IsNullOrWhiteSpace(page.Datapoints) ? "[]" : page.Datapoints, settings.InstanceId));
                token = string.IsNullOrEmpty(page.NextToken) ? null : page.NextToken;
                if (token is not null && (!cursors.Add(token) || cursors.Count >= 100)) throw new MetricQueryException("监控分页异常，请稍后重试。");
            } while (token is not null);
            var sample = metric == MonitorMetric.Disk ? MetricRules.SelectDisk(values, settings.DiskDevice) : MetricRules.Latest(values);
            return sample is null
                ? new(null, metric is MonitorMetric.Cpu or MonitorMetric.Memory or MonitorMetric.Disk ? "暂无指标，请检查云监控插件及磁盘设备设置。" : "暂无公网指标，请检查实例是否绑定公网 IP。")
                : new(sample);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MetricQueryException ex) { return new(null, ex.Message); }
        catch { return new(null, "监控读取失败，请检查网络、地域及配置。"); }
    }
}
