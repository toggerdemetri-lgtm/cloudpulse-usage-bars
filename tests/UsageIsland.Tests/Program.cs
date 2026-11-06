using UsageIsland.Core;
using UsageIsland.App;
using System.IO;

var now = DateTimeOffset.FromUnixTimeMilliseconds(1800000000000);
var settings = new IslandSettings { BandwidthMbps = 5 };
int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Equal<T>(T want, T got) { if (!EqualityComparer<T>.Default.Equals(want, got)) throw new Exception($"expected {want}; got {got}"); }
MetricValue Sample(double n, int age = 0, string? disk = null) => new(n, now.AddSeconds(-age), disk);
foreach (var (number, tone) in new[] { (0d, BarTone.Green), (59.99, BarTone.Green), (60d, BarTone.Yellow), (79.99, BarTone.Yellow), (80d, BarTone.Red), (100d, BarTone.Red) })
    Check($"threshold {number}", () => Equal(tone, MetricRules.Evaluate(Sample(number), null, now, settings).Tone));
Check("network 4Mbps / 5Mbps is 80%", () => { var bar = MetricRules.Evaluate(Sample(4000000), null, now, settings, true); Equal(.8, bar.Fill); Equal(BarTone.Red, bar.Tone); });
Check("over-capacity retains value and clamps fill", () => { var bar = MetricRules.Evaluate(Sample(6000000), null, now, settings, true); Equal(1d, bar.Fill); Equal(6000000d, bar.Sample!.Value); });
Check("missing bandwidth is gray", () => Equal(BarTone.Gray, MetricRules.Evaluate(Sample(1), null, now, new(), true).Tone));
Check("stale measured from sample not fetch", () => Equal(BarTone.Gray, MetricRules.Evaluate(Sample(5, 181), null, now, settings).Tone));
Check("180 seconds still valid", () => Equal(BarTone.Green, MetricRules.Evaluate(Sample(5, 180), null, now, settings).Tone));
Check("future sample is invalid", () => Equal(BarTone.Gray, MetricRules.Evaluate(Sample(5, -61), null, now, settings).Tone));
Check("error preserves stale reading without green", () => { var bar = MetricRules.Evaluate(Sample(5), "断网", now, settings); Equal(BarTone.Gray, bar.Tone); Equal(5d, bar.Sample!.Value); });
Check("missing sample differs from zero", () => { var bar = MetricRules.Evaluate(null, null, now, settings); Equal(BarTone.Gray, bar.Tone); Equal<MetricValue?>(null, bar.Sample); });
Check("NaN cannot become green", () => Equal(BarTone.Gray, MetricRules.Evaluate(Sample(double.NaN), null, now, settings).Tone));
Check("negative rate cannot become green", () => Equal(BarTone.Gray, MetricRules.Evaluate(Sample(-1), null, now, settings, true).Tone));
Check("speed units use bits not bytes", () => Equal("4.00 Mbps", MetricRules.FormatSpeed(4000000)));
Check("small rate units", () => Equal("37.52 Kbps", MetricRules.FormatSpeed(37516)));
Check("latest is sorted by timestamp", () => Equal(5d, MetricRules.Latest(new[] { Sample(5), Sample(9, 60) })!.Value));
Check("multi-disk uses latest per device then max", () => Equal("b", MetricRules.SelectDisk(new[] { Sample(99, 60, "a"), Sample(10, 0, "a"), Sample(80, 0, "b") }, null)!.Device));
Check("explicit disk never falls back to another", () => Equal<MetricValue?>(null, MetricRules.SelectDisk(new[] { Sample(80, 0, "b") }, "a")));
Check("explicit disk uses its latest point", () => Equal(10d, MetricRules.SelectDisk(new[] { Sample(99, 60, "a"), Sample(10, 0, "a"), Sample(80, 0, "b") }, "a")!.Value));
Check("parser handles json string datapoints and filters instance", () =>
{
    var data = MetricRules.Parse("[{\"timestamp\":1800000000000,\"instanceId\":\"mine\",\"Average\":\"39.102\",\"device\":\"/dev/vda1\"},{\"timestamp\":1800000000000,\"instanceId\":\"other\",\"Average\":90}]", "mine");
    Equal(1, data.Count); Equal(39.102, data[0].Value); Equal(now, data[0].SampledAt); Equal("/dev/vda1", data[0].Device);
});
Check("malformed points do not erase valid siblings", () => Equal(1, MetricRules.Parse("[{\"instanceId\":\"mine\",\"timestamp\":1800000000000,\"Average\":5},{\"instanceId\":\"mine\",\"timestamp\":\"bad\",\"Average\":6}]", "mine").Count));
Check("missing Average cannot turn into zero", () => Equal(0, MetricRules.Parse("[{\"instanceId\":\"mine\",\"timestamp\":1800000000000}]", "mine").Count));
Check("bad thresholds rejected", () => Equal(true, (settings with { YellowThreshold = 90, RedThreshold = 80 }).Validate().Count > 0));
Check("nonfinite bandwidth rejected", () => Equal(true, (settings with { BandwidthMbps = double.NaN }).Validate().Count > 0));
Check("zero bandwidth rejected", () => Equal(true, (settings with { BandwidthMbps = 0 }).Validate().Count > 0));
Check("default empty credentials does not invalidate settings", () => Equal(0, new IslandSettings().Validate().Count));
Check("refresh cannot overlap", () =>
{
    var gate = new RefreshGate();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = gate.TryRunAsync(() => release.Task);
    Equal(false, gate.TryRunAsync(() => Task.CompletedTask).GetAwaiter().GetResult());
    release.SetResult(); Equal(true, first.GetAwaiter().GetResult());
    Equal(true, gate.TryRunAsync(() => Task.CompletedTask).GetAwaiter().GetResult());
});
Check("refresh gate releases after error", () =>
{
    var gate = new RefreshGate();
    try { gate.TryRunAsync(() => throw new InvalidOperationException()).GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
    Equal(true, gate.TryRunAsync(() => Task.CompletedTask).GetAwaiter().GetResult());
});
var configDir = Path.Combine(Path.GetTempPath(), "UsageIsland-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(configDir);
var store = new ConfigurationStore(configDir);
try
{
    Check("missing config starts unconfigured", () => { var loaded = store.Load(); Equal<CloudCredentials?>(null, loaded.Credentials); Equal<string?>(null, loaded.Warning); });
    Check("credentials persist encrypted and round trip", () =>
    {
        store.Save(settings with { ScreenX = -1200, PositionLocked = true }, new("LTAI_TEST_ONLY", "fake-secret-test-only"));
        var json = File.ReadAllText(store.FilePath);
        Equal(false, json.Contains("fake-secret-test-only")); Equal(false, json.Contains("LTAI_TEST_ONLY"));
        var loaded = store.Load(); Equal("fake-secret-test-only", loaded.Credentials!.AccessKeySecret); Equal(-1200, loaded.Settings.ScreenX); Equal(true, loaded.Settings.PositionLocked);
    });
    Check("tampered credentials fail closed", () =>
    {
        File.WriteAllText(store.FilePath, "{\"settings\":{},\"protectedCredentials\":\"broken\"}");
        var loaded = store.Load(); Equal<CloudCredentials?>(null, loaded.Credentials); Equal(true, loaded.Warning is not null);
    });
    Check("malformed settings warn without overwriting file", () =>
    {
        File.WriteAllText(store.FilePath, "not-json"); var loaded = store.Load(); Equal(true, loaded.Warning is not null); Equal("not-json", File.ReadAllText(store.FilePath));
    });
    Check("clear credentials removes encrypted secret", () => { store.Save(settings, null); Equal<CloudCredentials?>(null, store.Load().Credentials); });
}
finally
{
    if (Directory.Exists(configDir)) { foreach (var file in Directory.GetFiles(configDir)) File.Delete(file); Directory.Delete(configDir); }
}
Check("disk pagination retains selection across pages", () =>
{
    int requests = 0;
    var reader = new MetricReader((metric, token, ct) =>
    {
        Equal("diskusage_utilization", metric); requests++;
        if (token is null) return Task.FromResult(new MetricPage("[{\"instanceId\":\"mine\",\"timestamp\":1800000000000,\"Average\":10,\"device\":\"a\"}]", "page2"));
        Equal("page2", token);
        return Task.FromResult(new MetricPage("[{\"instanceId\":\"mine\",\"timestamp\":1800000000000,\"Average\":90,\"device\":\"b\"}]"));
    });
    var result = reader.ReadAsync(MonitorMetric.Disk, settings with { InstanceId = "mine" }, default).GetAwaiter().GetResult();
    Equal(90d, result.Sample!.Value); Equal("b", result.Sample.Device); Equal(2, requests);
});
Check("query failures return safe error without exception payload", () =>
{
    var reader = new MetricReader((_, _, _) => throw new Exception("sensitive mock payload"));
    var result = reader.ReadAsync(MonitorMetric.Memory, settings, default).GetAwaiter().GetResult();
    Equal<MetricValue?>(null, result.Sample); Equal(false, result.Error!.Contains("sensitive"));
});
Check("empty agent data signals absent monitoring", () =>
{
    var reader = new MetricReader((_, _, _) => Task.FromResult(new MetricPage("[]")));
    var result = reader.ReadAsync(MonitorMetric.Memory, settings, default).GetAwaiter().GetResult();
    Equal<MetricValue?>(null, result.Sample); Equal(true, result.Error is not null);
});
Check("repeated pagination cursor stops rather than loops", () =>
{
    var reader = new MetricReader((_, _, _) => Task.FromResult(new MetricPage("[]", "same")));
    Equal(true, reader.ReadAsync(MonitorMetric.Disk, settings, default).GetAwaiter().GetResult().Error is not null);
});
Check("partial failure retains value but grays only failed metric", () =>
{
    var cache = new MetricCache();
    cache.Apply(new Dictionary<MonitorMetric, MetricResult> { [MonitorMetric.Cpu] = new(Sample(20)), [MonitorMetric.Memory] = new(Sample(40)) });
    cache.Apply(new Dictionary<MonitorMetric, MetricResult> { [MonitorMetric.Cpu] = new(null, "网络失败"), [MonitorMetric.Memory] = new(Sample(41)) });
    Equal(20d, cache.Get(MonitorMetric.Cpu).Sample!.Value);
    Equal(BarTone.Gray, MetricRules.Evaluate(cache.Get(MonitorMetric.Cpu).Sample, cache.Get(MonitorMetric.Cpu).Error, now, settings).Tone);
    Equal(41d, cache.Get(MonitorMetric.Memory).Sample!.Value);
    Equal<string?>(null, cache.Get(MonitorMetric.Memory).Error);
});
Check("out-of-order response cannot regress timestamp", () =>
{
    var cache = new MetricCache(); cache.Apply(new Dictionary<MonitorMetric, MetricResult> { [MonitorMetric.Cpu] = new(Sample(20)) });
    cache.Apply(new Dictionary<MonitorMetric, MetricResult> { [MonitorMetric.Cpu] = new(Sample(90, 60)) }); Equal(20d, cache.Get(MonitorMetric.Cpu).Sample!.Value);
});
Check("mixed DPI uses immutable design size", () => { Equal((192, 6), WidgetGeometry.PixelSize(96)); Equal((288, 9), WidgetGeometry.PixelSize(144)); Equal((384, 12), WidgetGeometry.PixelSize(192)); Equal((384, 12), WidgetGeometry.PixelSize(192)); });
Check("one canceled metric preserves successful siblings", () =>
{
    using var cancellation = new CancellationTokenSource();
    var reader = new MetricReader(async (metric, _, token) =>
    {
        if (metric == "diskusage_utilization") { await Task.Delay(Timeout.Infinite, token); }
        return new MetricPage("[{\"instanceId\":\"mine\",\"timestamp\":1800000000000,\"Average\":25}]");
    });
    var task = reader.ReadAllAsync(settings with { InstanceId = "mine" }, cancellation.Token);
    cancellation.Cancel(); var result = task.GetAwaiter().GetResult();
    Equal(25d, result[MonitorMetric.Cpu].Sample!.Value); Equal<string?>(null, result[MonitorMetric.Memory].Error);
    Equal<MetricValue?>(null, result[MonitorMetric.Disk].Sample); Equal(true, result[MonitorMetric.Disk].Error is not null);
});
var desktop = new DesktopArea(0, 0, 1920, 1040);
Check("visible bars reach top and left with entire window on screen", () => Equal((0, 0), WidgetGeometry.ConstrainPosition(-200, -200, desktop, 96)));
Check("visible bars reach bottom and right without clipping", () => Equal((1728, 1034), WidgetGeometry.ConstrainPosition(3000, 2000, desktop, 96)));
Check("near edge snaps by visible content", () => Equal((0, 0), WidgetGeometry.ConstrainPosition(-10, -4, desktop, 96)));
Check("interior placement is not moved", () => Equal((200, 300), WidgetGeometry.ConstrainPosition(200, 300, desktop, 96)));
Check("edge offsets scale at 200 percent DPI", () => Equal((0, 0), WidgetGeometry.ConstrainPosition(-200, -200, new(0, 0, 3840, 2080), 192)));
Check("left secondary monitor keeps negative coordinates", () => Equal((-1920, 0), WidgetGeometry.ConstrainPosition(-3000, -100, new(-1920, 0, 0, 1080), 96)));
Check("taskbar reserved area respected", () => Equal((0, 40), WidgetGeometry.ConstrainPosition(-100, -100, new(0, 40, 1920, 1080), 96)));
Check("startup command quotes Unicode path with spaces", () => Equal("\"C:\\My Apps\\云脉.exe\" --autostart", StartupRegistration.BuildCommand("C:\\My Apps\\云脉.exe")));
Check("startup rejects relative path", () => { bool rejected = false; try { StartupRegistration.BuildCommand("cloud.exe"); } catch (ArgumentException) { rejected = true; } Equal(true, rejected); });
Check("startup rejects commands exceeding Run key length", () => { bool rejected = false; try { StartupRegistration.BuildCommand("C:\\" + new string('x', 260) + ".exe"); } catch (ArgumentException) { rejected = true; } Equal(true, rejected); });
var testRegistryPath = @"Software\UsageIsland\Tests\" + Guid.NewGuid().ToString("N");
var startup = new StartupRegistration(testRegistryPath);
try
{
    Check("enable startup stores command and disable keeps other entries", () =>
    {
        startup.SetEnabled(true, Environment.ProcessPath!);
        Equal("\"" + Environment.ProcessPath + "\" --autostart", startup.ReadCommand());
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testRegistryPath)) key.SetValue("OtherApplication", "keep");
        startup.SetEnabled(false, Environment.ProcessPath!); Equal<string?>(null, startup.ReadCommand());
        using var savedKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRegistryPath); Equal("keep", savedKey!.GetValue("OtherApplication"));
    });
    Check("failed config save restores startup registration", () =>
    {
        startup.SetEnabled(true, Environment.ProcessPath!); string? before = startup.ReadCommand();
        bool failedSave = false;
        try { startup.Apply(false, Environment.ProcessPath!, () => throw new IOException("test save failure")); } catch (IOException) { failedSave = true; }
        Equal(true, failedSave); Equal(before, startup.ReadCommand());
    });
}
finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(testRegistryPath, false); }
var compatibilityDir = Path.Combine(Path.GetTempPath(), "UsageIsland-compat-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(compatibilityDir);
try
{
    var compatibleStore = new ConfigurationStore(compatibilityDir);
    Check("old config defaults startup off", () => { File.WriteAllText(compatibleStore.FilePath, "{\"settings\":{}}"); Equal(false, compatibleStore.Load().Settings.StartWithWindows); });
    Check("dock and startup preferences persist with position", () => { compatibleStore.Save(settings with { StartWithWindows = true, DisplayMode = WidgetMode.Taskbar, ScreenX = -14, ScreenY = -9 }, null); var loaded = compatibleStore.Load(); Equal(true, loaded.Settings.StartWithWindows); Equal(WidgetMode.Taskbar, loaded.Settings.DisplayMode); Equal(-14, loaded.Settings.ScreenX); Equal(-9, loaded.Settings.ScreenY); });
}
finally { foreach (var file in Directory.GetFiles(compatibilityDir)) File.Delete(file); Directory.Delete(compatibilityDir); }
var bottomTaskbar = new DesktopArea(0, 1040, 1920, 1080);
Check("unknown display mode is rejected", () => Equal(true, (settings with { DisplayMode = (WidgetMode)99 }).Validate().Count > 0));
Check("pointer entering taskbar selects vertical mode", () => Equal(WidgetMode.Taskbar, DockGeometry.ModeAt(500, 1040, new[] { bottomTaskbar })));
Check("pointer leaving taskbar restores desktop mode", () => Equal(WidgetMode.Desktop, DockGeometry.ModeAt(500, 1039, new[] { bottomTaskbar })));
Check("taskbar hit excludes far right boundary", () => Equal(WidgetMode.Desktop, DockGeometry.ModeAt(1920, 1060, new[] { bottomTaskbar })));
Check("secondary taskbar accepts negative screen coordinates", () => Equal(WidgetMode.Taskbar, DockGeometry.ModeAt(-1000, 1060, new[] { bottomTaskbar, new DesktopArea(-1920, 1040, 0, 1080) })));
Check("no taskbar keeps desktop mode", () => Equal(WidgetMode.Desktop, DockGeometry.ModeAt(10, 10, Array.Empty<DesktopArea>())));
Check("taskbar uses compact vertical layout", () => Equal(new WidgetLayout(WidgetMode.Taskbar, 42, 36), DockGeometry.TaskbarLayout(bottomTaskbar, 96)));
Check("small taskbar shrinks vertical layout height", () => Equal(new WidgetLayout(WidgetMode.Taskbar, 42, 28), DockGeometry.TaskbarLayout(new(0, 1048, 1920, 1080), 96)));
Check("high DPI retains logical dimensions", () => Equal(new WidgetLayout(WidgetMode.Taskbar, 42, 36), DockGeometry.TaskbarLayout(new(0, 2080, 3840, 2160), 192)));
Check("vertical taskbar fits its width", () => Equal(new WidgetLayout(WidgetMode.Taskbar, 28, 36), DockGeometry.TaskbarLayout(new(0, 0, 32, 1080), 96)));
Check("bottom taskbar centers perpendicular to edge", () => Equal((500, 1042), DockGeometry.PlaceOnTaskbar(500, 1040, bottomTaskbar, 96)));
Check("taskbar right edge keeps all four bars visible", () => Equal((1878, 1042), DockGeometry.PlaceOnTaskbar(3000, 1040, bottomTaskbar, 96)));
Check("high DPI taskbar position uses physical pixels", () => Equal((500, 2084), DockGeometry.PlaceOnTaskbar(500, 2080, new(0, 2080, 3840, 2160), 192)));
Check("desktop layout stays unchanged after switching", () => Equal(new WidgetLayout(WidgetMode.Desktop, 192, 6), DockGeometry.DesktopLayout));
Check("drag preview follows bottom-left pointer instead of taskbar centering", () => Equal((0, 1404), DockGeometry.PlaceDragPreview(-20, 1421, new(0, 0, 2560, 1440), new(WidgetMode.Taskbar, 42, 36), 96)));
Check("drag preview keeps bottom-right window on screen", () => Equal((2518, 1404), DockGeometry.PlaceDragPreview(2538, 1421, new(0, 0, 2560, 1440), new(WidgetMode.Taskbar, 42, 36), 96)));
Check("drag preview keeps free interior position", () => Equal((400, 400), DockGeometry.PlaceDragPreview(400, 400, new(0, 0, 2560, 1440), new(WidgetMode.Taskbar, 42, 36), 96)));
Check("drag preview scales window containment at 200 percent", () => Equal((3756, 2088), DockGeometry.PlaceDragPreview(3800, 2150, new(0, 0, 3840, 2160), new(WidgetMode.Taskbar, 42, 36), 192)));
Check("desktop drag preview preserves visible edge alignment", () => Equal((0, 0), DockGeometry.PlaceDragPreview(-100, -100, new(0, 0, 2560, 1440), DockGeometry.DesktopLayout, 96)));
Check("desktop native window remains fully inside all four screen edges", () =>
{
    var area = new DesktopArea(0, 0, 2560, 1392);
    var size = WidgetGeometry.PixelSize(96);
    foreach (var target in new[] { (-1000, -1000), (3000, -1000), (-1000, 3000), (3000, 3000) })
    {
        var p = WidgetGeometry.ConstrainPosition(target.Item1, target.Item2, area, 96);
        Equal(true, p.X >= area.Left && p.Y >= area.Top && p.X + size.Width <= area.Right && p.Y + size.Height <= area.Bottom);
    }
});
CloudVendorTests.Run(Check);
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
