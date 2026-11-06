using System.Globalization;
using Newtonsoft.Json;
using TencentCloud.Monitor.V20180724.Models;
using UsageIsland.App;
using UsageIsland.Core;

namespace UsageIsland.ProviderTests;

public static class TencentTests
{
    private static readonly IslandSettings Settings = new() { Vendor = CloudVendor.Tencent, InstanceId = "ins-test", RegionId = "ap-guangzhou" };
    private static readonly CloudCredentials Credentials = new("test-id", "test-secret", CloudVendor.Tencent, "test-token");

    public static void Run(Action<string, Action> check)
    {
        check("Tencent raw response preserves second precision lost by SDK float DTO", () =>
        {
            var json = """
                {"Response":{"MetricName":"CpuUsage","Period":60,"DataPoints":[{"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000060],"Values":[12.123456789]}]}}
                """;
            var result = TencentMonitorProvider.ParseRaw(json, MonitorMetric.Cpu, Settings.InstanceId);
            Require(result.Sample?.SampledAt.ToUnixTimeSeconds() == 1700000060 && result.Sample.Value == 12.123456789);
        });
        check("Tencent selects requested aggregate array and handles null values", () =>
        {
            var response = Response("WanOuttraffic", """
                [{"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000000,1700000128],"Values":[],"MaxValues":[1.25,null]}]
                """);
            Require(TencentMonitorProvider.Parse(response, MonitorMetric.NetworkOut, Settings.InstanceId).Sample?.Value == 1_250_000);
        });
        check("Tencent preserves latest zero and real Unix timestamp", () =>
        {
            var response = Response("CpuUsage", """
                [{"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000128,1700000000],"Values":[0,82]}]
                """);
            var result = TencentMonitorProvider.Parse(response, MonitorMetric.Cpu, Settings.InstanceId);
            Require(result.Sample is { Value: 0 } && result.Sample.SampledAt.ToUnixTimeSeconds() == 1700000128 && result.Error is null);
        });
        check("Tencent does not fabricate missing metric zero", () =>
        {
            var result = TencentMonitorProvider.Parse(Response("MemUsage", "[]"), MonitorMetric.Memory, Settings.InstanceId);
            Require(result.Sample is null && result.Error is not null);
        });
        check("Tencent rejects mismatched timestamp value arrays", () =>
        {
            var response = Response("CpuUsage", """
                [{"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000000,1700000060],"Values":[72]}]
                """);
            var result = TencentMonitorProvider.Parse(response, MonitorMetric.Cpu, Settings.InstanceId);
            Require(result.Sample is null && result.Error is not null);
        });
        check("Tencent filters foreign and absent instance dimensions", () =>
        {
            var response = Response("CpuUsage", """
                [{"Dimensions":[{"Name":"InstanceId","Value":"ins-other"}],"Timestamps":[1700000120],"Values":[99]},
                 {"Dimensions":[],"Timestamps":[1700000180],"Values":[100]},
                 {"Dimensions":[{"Name":"instanceId","Value":"ins-test"}],"Timestamps":[1700000240],"Values":[98]},
                 {"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000000],"Values":[42]}]
                """);
            Require(TencentMonitorProvider.Parse(response, MonitorMetric.Cpu, Settings.InstanceId).Sample?.Value == 42);
        });
        check("Tencent rejects wrong returned metric and nonfinite data", () =>
        {
            Require(TencentMonitorProvider.Parse(Response("MemUsage", Points("22")), MonitorMetric.Cpu, Settings.InstanceId).Sample is null);
            Require(TencentMonitorProvider.Parse(Response("CpuUsage", Points("-1")), MonitorMetric.Cpu, Settings.InstanceId).Sample is null);
            Require(TencentMonitorProvider.Parse(Response("CpuUsage", Points("101")), MonitorMetric.Cpu, Settings.InstanceId).Sample is null);
            var response = Response("CpuUsage", Points("22"));
            response.DataPoints[0].Values[0] = float.NaN;
            Require(TencentMonitorProvider.Parse(response, MonitorMetric.Cpu, Settings.InstanceId).Sample is null);
        });
        check("Tencent converts public Mbps to bits per second", () =>
        {
            Require(TencentMonitorProvider.Parse(Response("WanOuttraffic", Points("1.25")), MonitorMetric.NetworkOut, Settings.InstanceId).Sample?.Value == 1_250_000);
            Require(TencentMonitorProvider.Parse(Response("WanIntraffic", Points("0")), MonitorMetric.NetworkIn, Settings.InstanceId).Sample?.Value == 0);
        });
        check("Tencent request uses CVM exact dimension, documented metric and 60 second ten minute window", () =>
        {
            var expected = new Dictionary<string, long> { ["CpuUsage"] = 1, ["MemUsage"] = 1, ["CvmDiskUsage"] = 2, ["WanOuttraffic"] = 2, ["WanIntraffic"] = 2 };
            var requests = new List<GetMonitorDataRequest>();
            var provider = new TencentMonitorProvider((request, _) =>
            {
                lock (requests) requests.Add(request);
                return Task.FromResult(Response(request.MetricName, Points("2")));
            });
            var results = provider.PollAsync(Settings, Credentials, CancellationToken.None).GetAwaiter().GetResult();
            Require(results.Count == 5 && results.Values.All(result => result.Error is null));
            Require(requests.Count == 5 && requests.Select(r => r.MetricName).Distinct().Count() == 5);
            foreach (var request in requests)
            {
                Require(request.Namespace == "QCE/CVM" && request.Period == 60 && expected[request.MetricName] == request.SpecifyStatistics);
                Require(request.Instances.Length == 1 && request.Instances[0].Dimensions.Length == 1);
                Require(request.Instances[0].Dimensions[0].Name == "InstanceId" && request.Instances[0].Dimensions[0].Value == "ins-test");
                var start = DateTimeOffset.Parse(request.StartTime, CultureInfo.InvariantCulture);
                var end = DateTimeOffset.Parse(request.EndTime, CultureInfo.InvariantCulture);
                Require(end - start == TimeSpan.FromMinutes(10) && (DateTimeOffset.UtcNow - end).Duration() < TimeSpan.FromSeconds(10));
            }
        });
        check("Tencent rejects explicit disk device without replacing it with aggregate", () =>
        {
            var provider = new TencentMonitorProvider((request, _) =>
            {
                Require(request.MetricName != "CvmDiskUsage");
                return Task.FromResult(Response(request.MetricName, Points("2")));
            });
            var results = provider.PollAsync(Settings with { DiskDevice = "/dev/vda1" }, Credentials, CancellationToken.None).GetAwaiter().GetResult();
            Require(results[MonitorMetric.Disk].Sample is null && results[MonitorMetric.Disk].Error?.Contains("指定磁盘") == true);
            Require(results[MonitorMetric.Cpu].Sample?.Value == 2);
        });
        check("Tencent isolates SDK exceptions and redacts secret payloads", () =>
        {
            var provider = new TencentMonitorProvider((request, _) => request.MetricName == "MemUsage"
                ? throw new InvalidOperationException("test-secret Authorization: signed-request test-token")
                : Task.FromResult(Response(request.MetricName, Points("2"))));
            var results = provider.PollAsync(Settings, Credentials, CancellationToken.None).GetAwaiter().GetResult();
            Require(results[MonitorMetric.Memory].Sample is null && results[MonitorMetric.Memory].Error is not null);
            Require(results.Where(r => r.Key != MonitorMetric.Memory).All(r => r.Value.Error is null));
            Require(results.Values.All(r => r.Error is null || (!r.Error.Contains("test-secret") && !r.Error.Contains("signed-request") && !r.Error.Contains("test-token"))));
        });
        check("Tencent cancellation produces safe per metric timeout", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var calls = 0;
            var provider = new TencentMonitorProvider((request, _) => { calls++; return Task.FromResult(Response(request.MetricName, "[]")); });
            var results = provider.PollAsync(Settings, Credentials, cancellation.Token).GetAwaiter().GetResult();
            Require(calls == 0 && results.Values.All(r => r.Sample is null && r.Error?.Contains("超时") == true));
        });
        check("Tencent does not send other vendor credentials", () =>
        {
            var calls = 0;
            var provider = new TencentMonitorProvider((request, _) => { calls++; return Task.FromResult(Response(request.MetricName, "[]")); });
            var results = provider.PollAsync(Settings, Credentials with { Vendor = CloudVendor.Aliyun }, CancellationToken.None).GetAwaiter().GetResult();
            Require(calls == 0 && results.Values.All(r => r.Sample is null && r.Error is not null));
        });
    }

    private static string Points(string value) => """
        [{"Dimensions":[{"Name":"InstanceId","Value":"ins-test"}],"Timestamps":[1700000000],"Values":[
        """ + value + "]}]";
    private static GetMonitorDataResponse Response(string metric, string points) => JsonConvert.DeserializeObject<GetMonitorDataResponse>("{\"MetricName\":\"" + metric + "\",\"Period\":60,\"DataPoints\":" + points + "}")!;
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("腾讯云适配断言失败"); }
}
