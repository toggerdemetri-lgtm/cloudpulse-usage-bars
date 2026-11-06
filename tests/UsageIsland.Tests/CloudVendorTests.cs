using UsageIsland.App;
using UsageIsland.Core;
using System.IO;

internal static class CloudVendorTests
{
    public static void Run(Action<string, Action> check)
    {
        void Require(bool ok) { if (!ok) throw new Exception("cloud isolation or compatibility assertion failed"); }
        check("old settings default to Aliyun", () => Require(new IslandSettings().Vendor == CloudVendor.Aliyun));
        check("Tencent CVM accepts ins IDs", () => Require((new IslandSettings { Vendor = CloudVendor.Tencent, InstanceId = "ins-example", RegionId = "ap-beijing" }).Validate().Count == 0));
        check("Huawei requires project ID", () => Require((new IslandSettings { Vendor = CloudVendor.Huawei, InstanceId = Guid.NewGuid().ToString(), RegionId = "cn-north-4" }).Validate().Count > 0));
        check("AWS accepts EC2 IDs", () => Require((new IslandSettings { Vendor = CloudVendor.Aws, InstanceId = "i-0123456789abcdef0", RegionId = "us-east-1" }).Validate().Count == 0));
        check("unknown cloud fails closed", () => Require((new IslandSettings { Vendor = (CloudVendor)123 }).Validate().Count > 0));
        check("credentials never stringify secrets", () => Require(!new CloudCredentials("fake-id", "fake-secret").ToString().Contains("fake-")));
        check("secret reuse requires matching vendor and key ID", () =>
        {
            var saved = new CloudCredentials("test-key", "test-secret");
            Require(CredentialPolicy.Resolve(CloudVendor.Aliyun, "test-key", "", null, saved, false)!.AccessKeySecret == "test-secret");
            bool rejected = false;
            try { CredentialPolicy.Resolve(CloudVendor.Tencent, "test-key", "", null, saved, false); } catch (ArgumentException) { rejected = true; }
            Require(rejected);
            rejected = false;
            try { CredentialPolicy.Resolve(CloudVendor.Aliyun, "changed-key", "", null, saved, false); } catch (ArgumentException) { rejected = true; }
            Require(rejected);
        });
        check("null archived profile returns warning instead of crashing", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "UsageIsland-null-profile-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                var store = new ConfigurationStore(directory);
                File.WriteAllText(store.FilePath, "{\"settings\":{},\"profiles\":{\"Aws\":null}}");
                var profile = store.LoadProfile(CloudVendor.Aws);
                Require(profile.Settings.Vendor == CloudVendor.Aws && profile.Credentials is null && profile.Warning is not null);
            }
            finally { Directory.Delete(directory, true); }
        });
        check("wrong-cloud credentials cannot be saved", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "UsageIsland-multicloud-" + Guid.NewGuid());
            try
            {
                var store = new ConfigurationStore(directory);
                bool rejected = false;
                try { store.Save(new IslandSettings { Vendor = CloudVendor.Tencent, InstanceId = "ins-test", RegionId = "ap-beijing" }, new CloudCredentials("fake-id", "fake-secret")); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected && !File.Exists(store.FilePath));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        check("saved cloud profiles keep separate encrypted credentials", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "UsageIsland-profiles-" + Guid.NewGuid());
            try
            {
                var store = new ConfigurationStore(directory);
                store.Save(new(), new("ali-fake", "ali-secret-fake"));
                store.Save(new IslandSettings { Vendor = CloudVendor.Tencent, InstanceId = "ins-test", RegionId = "ap-beijing" }, new("ten-fake", "ten-secret-fake", CloudVendor.Tencent));
                Require(store.Load().Settings.Vendor == CloudVendor.Tencent);
                Require(store.LoadProfile(CloudVendor.Aliyun).Credentials!.AccessKeyId == "ali-fake");
                Require(store.LoadProfile(CloudVendor.Tencent).Credentials!.AccessKeyId == "ten-fake");
                var saved = File.ReadAllText(store.FilePath);
                Require(!saved.Contains("ali-secret-fake") && !saved.Contains("ten-secret-fake") && !saved.Contains("ali-fake"));
                store.Save(new IslandSettings { Vendor = CloudVendor.Tencent, InstanceId = "ins-test", RegionId = "ap-beijing" }, null);
                Require(store.LoadProfile(CloudVendor.Tencent).Credentials is null && store.LoadProfile(CloudVendor.Aliyun).Credentials is not null);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        check("legacy Aliyun encrypted JSON migrates without refilling credentials", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "UsageIsland-legacy-profile-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                var store = new ConfigurationStore(directory);
                var raw = System.Text.Encoding.UTF8.GetBytes("{\"accessKeyId\":\"legacy-fake-id\",\"accessKeySecret\":\"legacy-fake-secret\"}");
                var blob = System.Security.Cryptography.ProtectedData.Protect(raw, System.Text.Encoding.UTF8.GetBytes("UsageIsland/credentials/v1"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllText(store.FilePath, System.Text.Json.JsonSerializer.Serialize(new { settings = new { instanceId = "i-legacy", regionId = "cn-beijing" }, protectedCredentials = Convert.ToBase64String(blob) }));
                var loaded = store.Load();
                Require(loaded.Warning is null && loaded.Settings.Vendor == CloudVendor.Aliyun && loaded.Credentials?.AccessKeySecret == "legacy-fake-secret");
                store.Save(new IslandSettings { Vendor = CloudVendor.Tencent, InstanceId = "ins-test", RegionId = "ap-beijing" }, null);
                var legacy = store.LoadProfile(CloudVendor.Aliyun);
                Require(legacy.Warning is null && legacy.Credentials?.AccessKeyId == "legacy-fake-id");
            }
            finally { Directory.Delete(directory, true); }
        });
        check("basic AWS sample retains actual aggregation period", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var sample = new MetricValue(15, now.AddSeconds(-300), PeriodSeconds: 300);
            Require(MetricRules.Evaluate(sample, null, now, new()).Tone == BarTone.Green);
            Require(MetricRules.Evaluate(sample with { SampledAt = now.AddSeconds(-600) }, null, now, new()).Tone == BarTone.Green);
            Require(MetricRules.Evaluate(sample with { SampledAt = now.AddSeconds(-721) }, null, now, new()).Tone == BarTone.Gray);
        });
    }
}
