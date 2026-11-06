using HuaweiCloud.SDK.Ces.V1.Model;
using UsageIsland.App;
using UsageIsland.Core;

namespace UsageIsland.ProviderTests;

public static class HuaweiTests
{
    private const string Instance = "6f3c6f91-4b24-4e1b-b7d1-a94ac1cb011d";
    private static readonly IslandSettings Settings = new() { Vendor = CloudVendor.Huawei, InstanceId = Instance, RegionId = "cn-north-4", ProjectId = "0123456789abcdef0123456789abcdef" };
    private static readonly CloudCredentials Credentials = new("test-ak", "test-sk", CloudVendor.Huawei);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1800000600000);

    public static void Run(Action<string, Action> check)
    {
        check("Huawei preserves latest zero and actual millisecond timestamp", () =>
        {
            var points = HuaweiMonitorProvider.ParsePoints(Data("cpu_util", new Datapoint { Average = 90, Timestamp = 1800000000000 }, new Datapoint { Average = 0, Timestamp = 1800000060000 }), false, null, 300);
            var latest = MetricRules.Latest(points);
            Require(latest is { Value: 0, PeriodSeconds: 300 } && latest.SampledAt.ToUnixTimeMilliseconds() == 1800000060000);
        });
        check("Huawei converts byte rate to bits without dividing by period", () =>
        {
            var point = HuaweiMonitorProvider.ParsePoints(Data("network_outgoing_bytes_aggregate_rate", new Datapoint { Average = 125000, Timestamp = 1800000000000 }), true, null, 300).Single();
            Require(point.Value == 1000000 && point.PeriodSeconds == 300);
        });
        check("Huawei drops malformed samples instead of inventing current time", () =>
        {
            var points = HuaweiMonitorProvider.ParsePoints(Data("cpu_util", new Datapoint { Average = 5 }, new Datapoint { Average = double.NaN, Timestamp = 1800000000000 }, new Datapoint { Average = -1, Timestamp = 1800000000000 }, new Datapoint { Average = 1, Timestamp = long.MaxValue }), false, null, 300);
            Require(points.Count == 0);
        });
        check("Huawei requests scoped metrics with real window and correct periods", () =>
        {
            var requests = new List<ShowMetricDataRequest>();
            var provider = new HuaweiMonitorProvider((request, _) => { requests.Add(request); return Task.FromResult(Data(request.MetricName, new Datapoint { Average = 30, Timestamp = 1800000540000 })); }, (_, _) => Task.FromResult(Page(0)), () => Now);
            var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
            Require(requests.Count == 4 && result[MonitorMetric.Disk].Sample is null);
            Require(requests.All(r => r.Dim0 == "instance_id," + Instance && r.From == 1800000000000 && r.To == 1800000600000 && r.Filter == ShowMetricDataRequest.FilterEnum.AVERAGE));
            Require(requests.Single(r => r.MetricName == "mem_usedPercent") is { Namespace: "AGT.ECS" } mem && mem.Period.GetValue() == 60);
            Require(requests.Where(r => r.Namespace == "SYS.ECS").All(r => r.Period.GetValue() == 300));
            Require(result[MonitorMetric.NetworkOut].Sample?.Value == 240);
        });
        check("Huawei follows disk marker pages and keeps latest value per mount", () =>
        {
            var cursors = new List<string?>();
            var provider = new HuaweiMonitorProvider((request, _) => Task.FromResult(request.MetricName == "disk_usedPercent"
                ? request.Dim1 == "mount_point," + Instance + "-/" ? Data(request.MetricName, new Datapoint { Average = 99, Timestamp = 1800000000000 }, new Datapoint { Average = 10, Timestamp = 1800000540000 }) : Data(request.MetricName, new Datapoint { Average = 70, Timestamp = 1800000540000 })
                : Data(request.MetricName)), (request, _) =>
                {
                    cursors.Add(request.Start);
                    Require(request.Namespace == "AGT.ECS" && request.MetricName == "disk_usedPercent" && request.Dim0 == "instance_id," + Instance);
                    return Task.FromResult(request.Start is null ? Page(3, "cursor-one", Disk(Instance + "-/"), Disk("foreign", "00000000-0000-0000-0000-000000000000")) : Page(3, "cursor-two", Disk(Instance + "-/data")));
                }, () => Now);
            var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
            Require(cursors.SequenceEqual(new string?[] { null, "cursor-one" }));
            Require(result[MonitorMetric.Disk].Sample is { Value: 70, Device: Instance + "-/data" });
            var selected = provider.PollAsync(Settings with { DiskDevice = Instance + "-/" }, Credentials, default).GetAwaiter().GetResult();
            Require(selected[MonitorMetric.Disk].Sample?.Value == 10);
        });
        check("Huawei missing agent remains empty and individual errors stay isolated and safe", () =>
        {
            var provider = new HuaweiMonitorProvider((request, _) => request.MetricName == "cpu_util" ? Task.FromException<ShowMetricDataResponse>(new Exception("signed secret test-sk")) : Task.FromResult(Data(request.MetricName)), (_, _) => Task.FromResult(Page(0)));
            var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
            Require(result.Count == 5 && result.Values.All(v => v.Sample is null && !string.IsNullOrEmpty(v.Error)));
            Require(result.Values.All(v => !v.Error!.Contains("test-sk") && !v.Error.Contains("signed")));
        });
        check("Huawei failed memory query leaves successful CPU and network available", () =>
        {
            var provider = new HuaweiMonitorProvider((request, _) => request.MetricName == "mem_usedPercent" ? Task.FromException<ShowMetricDataResponse>(new Exception("secret")) : Task.FromResult(Data(request.MetricName, new Datapoint { Average = 20, Timestamp = 1800000540000 })), (_, _) => Task.FromResult(Page(0)));
            var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
            Require(result[MonitorMetric.Memory].Sample is null && result[MonitorMetric.Cpu].Sample?.Value == 20 && result[MonitorMetric.NetworkIn].Sample?.Value == 160);
        });
        check("Huawei rejects other vendor credentials before any SDK request", () =>
        {
            int calls = 0;
            var provider = new HuaweiMonitorProvider((_, _) => { calls++; return Task.FromResult(Data("cpu_util")); }, (_, _) => { calls++; return Task.FromResult(Page(0)); });
            try { provider.PollAsync(Settings, Credentials with { Vendor = CloudVendor.Aliyun }, default).GetAwaiter().GetResult(); } catch (ArgumentException) { }
            Require(calls == 0);
        });
        check("Huawei repeated disk marker fails disk only instead of truncating", () =>
        {
            int pages = 0;
            var provider = new HuaweiMonitorProvider((request, _) => Task.FromResult(Data(request.MetricName, new Datapoint { Average = 25, Timestamp = 1800000540000 })), (_, _) => { pages++; return Task.FromResult(Page(100, "repeat", Disk(Instance + "-/"))); });
            var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
            Require(pages == 2 && result[MonitorMetric.Disk].Sample is null && result[MonitorMetric.Disk].Error is not null && result[MonitorMetric.Cpu].Sample?.Value == 25);
        });
        check("Huawei missing discovered disk does not report a misleading partial maximum", () =>
        {
            foreach (bool invalid in new[] { false, true })
            {
                var provider = new HuaweiMonitorProvider((request, _) => Task.FromResult(request.MetricName == "disk_usedPercent" && request.Dim1 == "mount_point," + Instance + "-/data"
                    ? invalid ? Data(request.MetricName, new Datapoint { Average = double.NaN, Timestamp = 1800000540000 }) : Data(request.MetricName)
                    : Data(request.MetricName, new Datapoint { Average = 10, Timestamp = 1800000540000 })),
                    (_, _) => Task.FromResult(Page(2, null, Disk(Instance + "-/"), Disk(Instance + "-/data"))), () => Now);
                var result = provider.PollAsync(Settings, Credentials, default).GetAwaiter().GetResult();
                Require(result[MonitorMetric.Disk].Sample is null && result[MonitorMetric.Disk].Error?.Contains("不完整", StringComparison.Ordinal) == true);
                Require(result[MonitorMetric.Cpu].Sample?.Value == 10);
            }
        });
        check("Huawei already canceled poll does not invoke injected transports", () =>
        {
            int calls = 0;
            var provider = new HuaweiMonitorProvider((_, _) => { calls++; return new TaskCompletionSource<ShowMetricDataResponse>().Task; },
                (_, _) => { calls++; return new TaskCompletionSource<ListMetricsResponse>().Task; });
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var result = provider.PollAsync(Settings, Credentials, canceled.Token).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(calls == 0 && result.Count == 5 && result.Values.All(v => v.Sample is null && v.Error is not null));
        });
        check("Huawei query and discovery timeout even when injected transports ignore cancellation", () =>
        {
            var provider = new HuaweiMonitorProvider((_, _) => new TaskCompletionSource<ShowMetricDataResponse>().Task,
                (_, _) => new TaskCompletionSource<ListMetricsResponse>().Task, () => Now, TimeSpan.FromMilliseconds(40));
            var result = provider.PollAsync(Settings, Credentials, default).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(result.Count == 5 && result.Values.All(v => v.Sample is null && v.Error?.Contains("超时", StringComparison.Ordinal) == true));
        });
        check("Huawei in-flight cancellation completes even when injected transports ignore cancellation", () =>
        {
            var provider = new HuaweiMonitorProvider((_, _) => new TaskCompletionSource<ShowMetricDataResponse>().Task,
                (_, _) => new TaskCompletionSource<ListMetricsResponse>().Task);
            using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            var result = provider.PollAsync(Settings, Credentials, canceled.Token).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(result.Count == 5 && result.Values.All(v => v.Sample is null && v.Error is not null));
        });
    }

    private static ShowMetricDataResponse Data(string metric, params Datapoint[] points) => new() { MetricName = metric, Datapoints = points.ToList() };
    private static MetricInfoList Disk(string mount, string instance = Instance) => new()
    {
        Namespace = "AGT.ECS", MetricName = "disk_usedPercent", Unit = "%",
        Dimensions = new() { new() { Name = "instance_id", Value = instance }, new() { Name = "mount_point", Value = mount } }
    };
    private static ListMetricsResponse Page(int total, string? marker = null, params MetricInfoList[] metrics) => new()
    {
        Metrics = metrics.ToList(), MetaData = new() { Total = total, Count = metrics.Length, Marker = marker }
    };
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Huawei assertion failed"); }
}
