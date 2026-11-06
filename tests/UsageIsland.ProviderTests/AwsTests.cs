using Amazon.CloudWatch.Model;
using UsageIsland.App;
using UsageIsland.Core;

namespace UsageIsland.ProviderTests;

public static class AwsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);
    private static readonly IslandSettings Settings = new() { Vendor = CloudVendor.Aws, InstanceId = "i-test", RegionId = "cn-north-1" };
    private static readonly CloudCredentials Credentials = new("test-id", "test-secret", CloudVendor.Aws, "test-token");

    public static void Run(Action<string, Action> check)
    {
        check("AWS basic monitoring converts byte Sum over 300 seconds and preserves timestamp", () =>
        {
            var provider = Provider((r, _) =>
            {
                Require(r.Period == 300 && r.StartTime == Now.AddMinutes(-15).UtcDateTime && r.EndTime == Now.UtcDateTime);
                Require(r.Namespace == "AWS/EC2" && r.Dimensions.Single().Value == "i-test");
                return Task.FromResult(Points(new Datapoint { Sum = 30_000, Average = 40, Timestamp = Now.AddMinutes(-5).UtcDateTime }));
            });
            var result = Poll(provider);
            Require(result[MonitorMetric.NetworkOut].Sample is { Value: 800, PeriodSeconds: 300 } sample && sample.SampledAt == Now.AddMinutes(-5));
            Require(result[MonitorMetric.NetworkOut].Error is null);
            Require(result[MonitorMetric.Cpu].Sample is { Value: 40, PeriodSeconds: 300 });
        });
        check("AWS explicit detailed monitoring uses 60 second Sum conversion", () =>
        {
            var provider = Provider((r, _) =>
            {
                Require(r.Period == 60);
                Require(r.Statistics.Single() == (r.MetricName == "CPUUtilization" ? "Average" : "Sum"));
                return Task.FromResult(Points(new Datapoint { Sum = 6_000, Average = 11, Timestamp = Now.AddMinutes(-1).UtcDateTime }));
            });
            var result = Poll(provider, Settings with { AwsDetailedMonitoring = true });
            Require(result[MonitorMetric.NetworkIn].Sample is { Value: 800, PeriodSeconds: 60 });
        });
        check("AWS excludes incomplete five minute buckets from network rate", () =>
        {
            var provider = new AwsMonitorProvider((r, _) =>
            {
                Require(r.EndTime == Now.UtcDateTime);
                return Task.FromResult(Points(new Datapoint { Sum = 30_000, Average = 40, Timestamp = Now.AddMinutes(-5).UtcDateTime },
                    new Datapoint { Sum = 6_000, Average = 99, Timestamp = Now.UtcDateTime }));
            }, (_, _) => Task.FromResult(new ListMetricsResponse { Metrics = [] }), () => Now.AddSeconds(150));
            Require(Poll(provider)[MonitorMetric.NetworkOut].Sample is { Value: 800 } sample && sample.SampledAt == Now.AddMinutes(-5));
        });
        check("AWS agent lookup uses every returned dimension and rejects other instances", () =>
        {
            var descriptor = Agent("mem_used_percent", ("InstanceId", "i-test"), ("ImageId", "ami-test"), ("InstanceType", "t3.micro"));
            var provider = Provider((r, _) => Task.FromResult(r.Namespace == "CWAgent" && r.Dimensions.Count == 3 && r.Dimensions.Any(d => d.Name == "ImageId" && d.Value == "ami-test")
                ? Points(new Datapoint { Average = 42, Timestamp = Now.AddMinutes(-1).UtcDateTime }) : Points()),
                (r, _) => Task.FromResult(new ListMetricsResponse { Metrics = r.MetricName == "mem_used_percent" ? [descriptor, Agent("mem_used_percent", ("InstanceId", "i-other"))] : [] }));
            Require(Poll(provider)[MonitorMetric.Memory].Sample is { Value: 42, PeriodSeconds: 60 });
        });
        check("AWS disk pagination deduplicates dimensions and chooses highest latest per path", () =>
        {
            var root = Agent("disk_used_percent", ("InstanceId", "i-test"), ("path", "/"), ("device", "nvme0n1p1"));
            var data = Agent("disk_used_percent", ("InstanceId", "i-test"), ("path", "/data"), ("device", "nvme1n1"));
            int queried = 0;
            var provider = Provider((r, _) =>
            {
                if (r.Namespace != "CWAgent") return Task.FromResult(Points());
                queried++;
                return Task.FromResult(r.Dimensions.Any(d => d.Value == "/data")
                    ? Points(new Datapoint { Average = 99, Timestamp = Now.AddMinutes(-3).UtcDateTime }, new Datapoint { Average = 48, Timestamp = Now.AddMinutes(-1).UtcDateTime })
                    : Points(new Datapoint { Average = 78, Timestamp = Now.AddMinutes(-1).UtcDateTime }));
            }, (r, _) => Task.FromResult(r.MetricName != "disk_used_percent" ? new ListMetricsResponse { Metrics = [] }
                : new ListMetricsResponse { Metrics = r.NextToken is null ? [root] : [root, data], NextToken = r.NextToken is null ? "page2" : null }));
            Require(Poll(provider)[MonitorMetric.Disk].Sample is { Value: 78, Device: "/" });
            Require(queried == 2);
            Require(Poll(provider, Settings with { DiskDevice = "nvme1n1" })[MonitorMetric.Disk].Sample is { Value: 48, Device: "/data" });
        });
        check("AWS unsorted samples keep newest valid value without fabricating absent statistics", () =>
        {
            var provider = Provider((_, _) => Task.FromResult(Points(
                new Datapoint { Average = 20, Timestamp = Now.AddMinutes(-1).UtcDateTime },
                new Datapoint { Average = 90, Timestamp = Now.AddMinutes(-4).UtcDateTime },
                new Datapoint { Average = double.NaN, Timestamp = Now.UtcDateTime })));
            var result = Poll(provider);
            Require(result[MonitorMetric.Cpu].Sample is { Value: 20 });
            Require(result[MonitorMetric.NetworkIn].Sample is null);
        });
        foreach (bool invalid in new[] { false, true })
        {
            check("AWS highest disk refuses incomplete " + (invalid ? "invalid" : "empty") + " discovered disk series", () =>
            {
                var result = Poll(ProviderWithMissingDisk(invalid));
                Require(result[MonitorMetric.Disk].Sample is null && result[MonitorMetric.Disk].Error?.Contains("不完整", StringComparison.Ordinal) == true);
                Require(result[MonitorMetric.Cpu].Sample is { Value: 24 } && result[MonitorMetric.Memory].Sample is { Value: 31 });
                Require(result[MonitorMetric.NetworkOut].Sample is { Value: 160 } && result[MonitorMetric.NetworkOut].Error is null);
            });
        }
        check("AWS explicit disk ignores unselected missing disks and rejects selected missing disk", () =>
        {
            var provider = ProviderWithMissingDisk(false);
            Require(Poll(provider, Settings with { DiskDevice = "/" })[MonitorMetric.Disk].Sample is { Value: 10, Device: "/" });
            var missing = Poll(provider, Settings with { DiskDevice = "/data" })[MonitorMetric.Disk];
            Require(missing.Sample is null && missing.Error?.Contains("不完整", StringComparison.Ordinal) == true);
        });
        check("AWS absent agent metrics stay missing and explain official agent requirement", () =>
        {
            var result = Poll(Provider((_, _) => Task.FromResult(Points())));
            Require(result.Count == 5 && result.Values.All(v => v.Sample is null));
            Require(result[MonitorMetric.Memory].Error?.Contains("CloudWatch Agent", StringComparison.Ordinal) == true);
        });
        check("AWS metric failures are isolated and SDK payloads never become user errors", () =>
        {
            var provider = Provider((r, _) => r.MetricName == "CPUUtilization"
                ? Task.FromException<GetMetricStatisticsResponse>(new Amazon.CloudWatch.AmazonCloudWatchException("test-secret test-token signedpayload") { ErrorCode = "AccessDenied" })
                : Task.FromResult(Points(new Datapoint { Sum = 3_000, Timestamp = Now.AddMinutes(-5).UtcDateTime })));
            var result = Poll(provider);
            Require(result[MonitorMetric.Cpu].Sample is null && result[MonitorMetric.Cpu].Error?.Contains("权限", StringComparison.Ordinal) == true);
            Require(result[MonitorMetric.NetworkOut].Sample is { Value: 80 });
            Require(result.Values.All(v => v.Error is null || (!v.Error.Contains("test-secret") && !v.Error.Contains("test-token") && !v.Error.Contains("signedpayload"))));
        });
        check("AWS repeated pagination token fails only that metric without looping", () =>
        {
            int pages = 0;
            var provider = Provider((_, _) => Task.FromResult(Points(new Datapoint { Average = 12, Sum = 600, Timestamp = Now.AddMinutes(-5).UtcDateTime })),
                (r, _) => { if (r.MetricName != "disk_used_percent") return Task.FromResult(new ListMetricsResponse { Metrics = [] }); pages++; return Task.FromResult(new ListMetricsResponse { Metrics = [], NextToken = "same" }); });
            var result = Poll(provider);
            Require(pages == 2 && result[MonitorMetric.Disk].Error is not null && result[MonitorMetric.Cpu].Sample?.Value == 12);
        });
        check("AWS pagination has a finite page limit even with unique tokens", () =>
        {
            int pages = 0;
            var provider = Provider((_, _) => Task.FromResult(Points()), (r, _) => Task.FromResult(r.MetricName == "disk_used_percent"
                ? new ListMetricsResponse { Metrics = [], NextToken = (++pages).ToString() } : new ListMetricsResponse { Metrics = [] }));
            Require(Poll(provider)[MonitorMetric.Disk].Error is not null && pages <= 20);
        });
        check("AWS rejects cross vendor settings and credentials before sending requests", () =>
        {
            int requests = 0;
            var provider = Provider((_, _) => { requests++; return Task.FromResult(Points()); }, (_, _) => { requests++; return Task.FromResult(new ListMetricsResponse()); });
            var rejected = provider.PollAsync(Settings, Credentials with { Vendor = CloudVendor.Aliyun }, default).GetAwaiter().GetResult();
            Require(rejected.Count == 5 && rejected.Values.All(v => v.Error is not null));
            var wrongSettings = Poll(provider, Settings with { Vendor = CloudVendor.Tencent });
            Require(wrongSettings.Count == 5 && wrongSettings.Values.All(v => v.Error is not null));
            Require(requests == 0);
        });
        check("AWS rejects unsafe region input without sending credentials", () =>
        {
            int requests = 0;
            var provider = Provider((_, _) => { requests++; return Task.FromResult(Points()); });
            var rejected = Poll(provider, Settings with { RegionId = "evil.example/path" });
            Require(rejected.Count == 5 && rejected.Values.All(v => v.Error is not null));
            Require(requests == 0);
        });
        check("AWS query timeout is bounded even if transport ignores cancellation", () =>
        {
            var provider = new AwsMonitorProvider((_, _) => new TaskCompletionSource<GetMetricStatisticsResponse>().Task,
                (_, _) => Task.FromResult(new ListMetricsResponse { Metrics = [] }), () => Now, TimeSpan.FromMilliseconds(40));
            var result = provider.PollAsync(Settings, Credentials, default).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(result[MonitorMetric.Cpu].Error is not null && result[MonitorMetric.Cpu].Sample is null);
        });
        check("AWS cancellation completes promptly with safe per metric errors", () =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var result = Provider((_, _) => new TaskCompletionSource<GetMetricStatisticsResponse>().Task).PollAsync(Settings, Credentials, canceled.Token).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(result.Count == 5 && result.Values.All(v => v.Error is not null && v.Sample is null));
        });
    }

    private static AwsMonitorProvider Provider(Func<GetMetricStatisticsRequest, CancellationToken, Task<GetMetricStatisticsResponse>> read,
        Func<ListMetricsRequest, CancellationToken, Task<ListMetricsResponse>>? list = null) => new(read, list ?? ((_, _) => Task.FromResult(new ListMetricsResponse { Metrics = [] })), () => Now);
    private static IReadOnlyDictionary<MonitorMetric, MetricResult> Poll(AwsMonitorProvider provider, IslandSettings? settings = null) => provider.PollAsync(settings ?? Settings, Credentials, default).GetAwaiter().GetResult();
    private static AwsMonitorProvider ProviderWithMissingDisk(bool invalidDisk) => Provider((r, _) =>
        Task.FromResult(r.Namespace == "AWS/EC2" ? Points(new Datapoint { Average = 24, Sum = 6_000, Timestamp = Now.AddMinutes(-5).UtcDateTime })
        : r.MetricName == "mem_used_percent" ? Points(new Datapoint { Average = 31, Timestamp = Now.AddMinutes(-1).UtcDateTime })
        : r.Dimensions.Any(d => d.Name == "path" && d.Value == "/") ? Points(new Datapoint { Average = 10, Timestamp = Now.AddMinutes(-1).UtcDateTime })
        : invalidDisk ? Points(new Datapoint { Average = double.NaN, Timestamp = Now.AddMinutes(-1).UtcDateTime }, new Datapoint { Average = 150, Timestamp = Now.AddMinutes(-1).UtcDateTime }) : Points()),
        (r, _) => Task.FromResult(new ListMetricsResponse { Metrics = r.MetricName == "mem_used_percent"
            ? [Agent("mem_used_percent", ("InstanceId", "i-test"))]
            : [Agent("disk_used_percent", ("InstanceId", "i-test"), ("path", "/")), Agent("disk_used_percent", ("InstanceId", "i-test"), ("path", "/data"))] }));
    private static GetMetricStatisticsResponse Points(params Datapoint[] points) => new() { Datapoints = points.ToList() };
    private static Metric Agent(string name, params (string Name, string Value)[] dimensions) => new() { Namespace = "CWAgent", MetricName = name, Dimensions = dimensions.Select(d => new Dimension { Name = d.Name, Value = d.Value }).ToList() };
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("AWS contract failed"); }
}
