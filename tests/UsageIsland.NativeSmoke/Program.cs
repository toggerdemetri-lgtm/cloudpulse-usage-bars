using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.IO;
using UsageIsland.App;
using UsageIsland.App.Native;
using UsageIsland.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var checks = new List<string>();
        int exitCode = 1;
        var app = new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new IslandWindow(new IslandSettings { ScreenX = 16, ScreenY = 16 });
        DesktopHost? host = null;
        window.Closed += (_, _) => host?.Dispose();
        window.Show();
        host = (DesktopHost?)typeof(IslandWindow).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        window.Dispatcher.BeginInvoke(async () =>
        {
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
            try
            {
                async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200); }
                void CheckVisibleBars(string stage)
                {
                    var native = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    GetWindowRect(native, out var rect);
                    var matrix = PresentationSource.FromVisual(window)!.CompositionTarget.TransformToDevice;
                    var bars = (Panel)window.FindName("Bars");
                    for (int index = 0; index < 4; index++)
                    {
                        var track = (Border)((Border)bars.Children[index]).Child;
                        var center = track.TranslatePoint(new Point(track.ActualWidth / 2, track.ActualHeight / 2), window);
                        var hit = WindowFromPoint(new POINT { X = rect.Left + (int)Math.Round(center.X * matrix.M11), Y = rect.Top + (int)Math.Round(center.Y * matrix.M22) });
                        if (host!.Mode == WidgetMode.Desktop && host.ParentClass == "SHELLDLL_DefView" && hit != IntPtr.Zero && GetAncestor(hit, 2) != GetAncestor(native, 2))
                        {
                            checks.Add(stage + " bar " + index + " correctly behind a normal application"); continue;
                        }
                        Check(hit == native, stage + " bar input target " + index + ": hit=" + hit + " expected=" + native);
                    }
                }
                void PausePhysicalInputWatchdog()
                {
                    // This integration test drives the host explicitly without holding a real
                    // mouse button; keep the preview alive until its explicit EndDrag call.
                    ((DispatcherTimer)typeof(DesktopHost).GetField("dragTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!).Stop();
                }
                await Frame();
                Check(host!.IsAttached && host.ParentClass == "SHELLDLL_DefView", "initial desktop attachment");
                CheckVisibleBars("initial desktop");
                var taskbar = FindWindow("Shell_TrayWnd", null);
                Check(taskbar != IntPtr.Zero && GetWindowRect(taskbar, out _), "real taskbar available");
                GetWindowRect(taskbar, out var taskbarRect);
                var original = host.Position;
                if (!host.BeginDrag(new() { X = original.X + 96, Y = original.Y + 3 })) throw new InvalidOperationException("desktop detaches for drag: " + host.Error);
                checks.Add("desktop detaches for drag");
                PausePhysicalInputWatchdog();
                var dragHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Check(GetAncestor(dragHandle, 2) == dragHandle, "drag window is top level even with WPF hidden owner");
                host.UpdateDrag(new() { X = taskbarRect.Left + 420, Y = taskbarRect.Top + (taskbarRect.Bottom - taskbarRect.Top) / 2 });
                Check(host.Mode == WidgetMode.Taskbar && host.Layout.Width <= 42 && host.Layout.Height <= 36, "taskbar preview selects vertical layout");
                await Frame(); CheckVisibleBars("floating taskbar preview");
                host.EndDrag();
                Check(host.IsAttached && host.ParentClass == "Shell_TrayWnd", "drop attaches to taskbar");
                var nativeHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Check(GetParent(nativeHandle) == taskbar, "taskbar is actual native parent");
                GetWindowRect(nativeHandle, out var docked);
                Check(docked.Left >= taskbarRect.Left && docked.Right <= taskbarRect.Right && docked.Top >= taskbarRect.Top && docked.Bottom <= taskbarRect.Bottom, "docked window stays inside taskbar");
                await Frame(); CheckVisibleBars("taskbar middle");
                Check(host.BeginDrag(new() { X = docked.Left + 20, Y = docked.Top + 18 }), "left-corner drag begins");
                PausePhysicalInputWatchdog();
                host.UpdateDrag(new() { X = taskbarRect.Left + 1, Y = taskbarRect.Bottom - 1 });
                await Frame(); CheckVisibleBars("floating left-corner preview");
                Check(WindowFromPoint(new POINT { X = taskbarRect.Left + 1, Y = taskbarRect.Bottom - 1 }) == nativeHandle, "left-corner preview keeps the pointer inside its input surface");
                host.EndDrag();
                await Frame(); CheckVisibleBars("taskbar left corner");
                GetWindowRect(nativeHandle, out docked);
                Check(host.BeginDrag(new() { X = docked.Left + (docked.Right - docked.Left) / 2, Y = docked.Top + (docked.Bottom - docked.Top) / 2 }), "taskbar detaches for drag");
                PausePhysicalInputWatchdog();
                host.UpdateDrag(new() { X = 112, Y = 19 });
                Check(host.Mode == WidgetMode.Desktop && host.Layout == DockGeometry.DesktopLayout, "drag out restores horizontal layout");
                host.EndDrag();
                await Frame(); CheckVisibleBars("restored desktop");
                Check(host.IsAttached && host.ParentClass == "SHELLDLL_DefView", "drop restores desktop parent");
                Check((GetWindowLongPtr(nativeHandle, -20).ToInt64() & 8) == 0, "desktop mode clears topmost flag");
                Check(GetWindowRect(nativeHandle, out var restored) && restored.Right - restored.Left == 192 && restored.Bottom - restored.Top == 6, "restored window keeps original size");
                host.ReturnToDesktop();
                await Frame(); CheckVisibleBars("rescued desktop");
                Check(host.Mode == WidgetMode.Desktop && host.IsAttached, "tray rescue returns to desktop");
                var rescued = host.Position;
                Check(host.BeginDrag(new() { X = rescued.X + 36, Y = rescued.Y + 3 }), "top-left edge drag begins");
                PausePhysicalInputWatchdog();
                host.UpdateDrag(new() { X = 0, Y = 0 });
                await Frame(); CheckVisibleBars("top-left floating preview");
                Check(host.Position == (0, 0), "top-left preview keeps entire native window on screen");
                Check(WindowFromPoint(new POINT { X = 0, Y = 0 }) == nativeHandle, "top-left preview retains pointer hit");
                host.EndDrag();
                await Frame(); CheckVisibleBars("top-left desktop drop");
                Check(host.Position == (0, 0), "top-left drop preserves position");
                window.Close();
                // Exercise the application's real drop/lifetime path without starting
                // monitoring or loading any user's configuration or credentials.
                typeof(App).GetMethod("CreateWidget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
                await Frame();
                var beforeDrop = (IslandWindow)app.MainWindow;
                var beforeHandle = new System.Windows.Interop.WindowInteropHelper(beforeDrop).Handle;
                host = (DesktopHost)typeof(IslandWindow).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(beforeDrop)!;
                var start = host.Position;
                Check(host.BeginDrag(new() { X = start.X + 36, Y = start.Y + 3 }), "application drop begins");
                PausePhysicalInputWatchdog();
                typeof(IslandWindow).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(beforeDrop, true);
                typeof(IslandWindow).GetMethod("FinishDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(beforeDrop, null);
                var droppedPosition = host.Position;
                var droppedMode = host.Mode;
                await Frame();
                var afterDrop = (IslandWindow)app.MainWindow;
                var afterHandle = new System.Windows.Interop.WindowInteropHelper(afterDrop).Handle;
                Check(afterHandle != beforeHandle, "drop creates a fresh presentation HWND instead of reusing the reparented surface");
                var replacementHost = (DesktopHost)typeof(IslandWindow).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(afterDrop)!;
                Check(replacementHost.Position == droppedPosition && replacementHost.Mode == droppedMode, "fresh presentation preserves drop position and mode");
                Check(replacementHost.IsAttached && !beforeDrop.IsVisible, "fresh presentation attaches before old window is retired");
                Check(!IsWindow(beforeHandle), "old native window is destroyed after handoff");
                afterDrop.ReturnToDesktop(); afterDrop.ReturnToDesktop();
                await Frame();
                Check(app.Windows.OfType<IslandWindow>().Count() == 1, "duplicate reset requests leave exactly one widget");
                IslandSettings? savedCloud = null;
                CloudCredentials? savedCredential = null;
                var settingsPreview = new SettingsWindow(new IslandSettings { BandwidthMbps = 3 }, new("fake-ali-key", "fake-secret"), "测试临时配置（不保存）", null,
                    (s, c) => { savedCloud = s; savedCredential = c; },
                    vendor => new(CloudCatalog.Defaults(vendor) with { BandwidthMbps = 5, InstanceId = vendor == CloudVendor.Aws ? "i-test-instance" : "" }, null, null));
                settingsPreview.Show(); await Frame();
                var vendorBox = (ComboBox)settingsPreview.FindName("VendorBox");
                var keyBox = (TextBox)settingsPreview.FindName("AccessKeyBox");
                var instanceBox = (TextBox)settingsPreview.FindName("InstanceBox");
                vendorBox.SelectedValue = CloudVendor.Tencent;
                Check(keyBox.Text.Length == 0 && ((TextBox)settingsPreview.FindName("RegionBox")).Text == "ap-beijing", "cloud switch never carries Aliyun key into Tencent form");
                Check(!((TextBox)settingsPreview.FindName("DiskBox")).IsEnabled, "Tencent aggregate disk disables fixed device selector");
                instanceBox.Text = "ins-unsaved-draft";
                vendorBox.SelectedValue = CloudVendor.Aliyun;
                Check(keyBox.Text == "fake-ali-key", "switch back restores original vendor credential draft");
                vendorBox.SelectedValue = CloudVendor.Tencent;
                Check(instanceBox.Text == "ins-unsaved-draft", "unsaved per vendor form survives switching");
                vendorBox.SelectedValue = CloudVendor.Huawei;
                Check(((StackPanel)settingsPreview.FindName("ProjectPanel")).Visibility == Visibility.Visible, "Huawei shows regional project field");
                vendorBox.SelectedValue = CloudVendor.Aws;
                Check(((CheckBox)settingsPreview.FindName("AwsDetailedBox")).Visibility == Visibility.Visible, "AWS shows explicit detailed monitoring selection");
                keyBox.Text = "fake-aws-key"; ((PasswordBox)settingsPreview.FindName("SecretBox")).Password = "fake-aws-secret";
                await Frame();
                var snapshotPath = Path.Combine(Environment.CurrentDirectory, "artifacts", "settings-1.1.0.png");
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                var bitmap = new RenderTargetBitmap((int)settingsPreview.ActualWidth, (int)settingsPreview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(settingsPreview);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(snapshotPath)) encoder.Save(stream);
                ((Button)settingsPreview.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(savedCloud?.Vendor == CloudVendor.Aws && savedCredential?.Vendor == CloudVendor.Aws && savedCredential.AccessKeyId == "fake-aws-key", "settings save binds the chosen vendor to its own credentials");
                exitCode = 0;
            }
            catch (Exception ex) { checks.Add("FAIL " + ex.Message); }
            finally
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = exitCode == 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(exitCode);
            }
        }, DispatcherPriority.ApplicationIdle);
        app.Run(); return exitCode;
    }
    private sealed class TestApplication : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
