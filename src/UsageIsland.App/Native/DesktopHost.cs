using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using UsageIsland.Core;

namespace UsageIsland.App.Native;

// Desktop and taskbar displays are children of the corresponding Explorer surface.
// While dragging, use one temporary top-level window so neither parent's clipping blocks a drop.
public sealed class DesktopHost : IDisposable
{
    private readonly Window window;
    private readonly IntPtr handle;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer dragTimer;
    private readonly HwndSource? source;
    private IntPtr parent;
    private int screenX, screenY;
    private bool dragging;
    private double grabX, grabY;
    private TaskbarSurface? selectedTaskbar;
    private sealed record TaskbarSurface(IntPtr Handle, DesktopArea Area);
    public WidgetMode Mode { get; private set; }
    public WidgetLayout Layout { get; private set; } = DockGeometry.DesktopLayout;
    public (int X, int Y) Position => GetWindowRect(handle, out var bounds) ? (bounds.Left, bounds.Top) : (screenX, screenY);
    public bool IsAttached => IsWindow(handle) && (dragging || (parent != IntPtr.Zero && IsWindow(parent) && GetParent(handle) == parent));
    public string ParentClass => ClassName(parent);
    public string? Error { get; private set; }
    public event Action? AttachmentChanged;
    public event Action<WidgetLayout>? LayoutChanged;
    public event Action? DragReleased;
    public event Action<bool>? DragStateChanged;

    public DesktopHost(Window window, int? x = null, int? y = null, WidgetMode mode = WidgetMode.Desktop)
    {
        this.window = window; Mode = mode;
        handle = new WindowInteropHelper(window).Handle;
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        screenX = x ?? area.Left + 16; screenY = y ?? area.Top + 16;
        source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { if (dragging) return; if (!IsAttached) Attach(); else Place(screenX, screenY); };
        timer.Start();
        // Background/no-activate windows cannot rely on capture outside their visible pixels.
        // Sample only during an explicit drag; this does not inject input or steal foreground focus.
        dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        dragTimer.Tick += (_, _) =>
        {
            if (!dragging) return;
            UpdateDrag(CursorPosition());
            if (!LeftButtonDown) { dragTimer.Stop(); DragReleased?.Invoke(); if (dragging) EndDrag(); }
        };
    }

    public bool Attach()
    {
        if (dragging) return true;
        IntPtr target;
        if (Mode == WidgetMode.Taskbar)
        {
            selectedTaskbar = FindTaskbars().OrderBy(bar => Distance(bar.Area, screenX + 21, screenY + 18)).FirstOrDefault();
            target = selectedTaskbar?.Handle ?? IntPtr.Zero;
        }
        else target = FindDesktop();
        if (target == IntPtr.Zero) return Fail(Mode == WidgetMode.Taskbar ? "任务栏尚未就绪，正在重试；可从托盘移回桌面。" : "未找到 Windows 桌面宿主，正在重试。可从托盘打开设置。");
        if (!Reparent(target)) return Fail("显示挂载失败，错误码：" + Marshal.GetLastPInvokeError());
        parent = target; Error = null;
        Place(screenX, screenY); ShowWindow(handle, 4); AttachmentChanged?.Invoke();
        return true;
    }

    public bool BeginDrag(POINT cursor)
    {
        if (!IsAttached || dragging || !GetWindowRect(handle, out var bounds)) return false;
        grabX = Math.Clamp((cursor.X - bounds.Left) / (double)Math.Max(1, bounds.Right - bounds.Left), 0, 1);
        grabY = Math.Clamp((cursor.Y - bounds.Top) / (double)Math.Max(1, bounds.Bottom - bounds.Top), 0, 1);
        screenX = bounds.Left; screenY = bounds.Top;
        if (!Reparent(IntPtr.Zero)) { Error = "拖动窗口分离失败，错误码：" + Marshal.GetLastPInvokeError() + "，父窗口：" + ClassName(GetParent(handle)); AttachmentChanged?.Invoke(); return false; }
        parent = IntPtr.Zero; dragging = true;
        DragStateChanged?.Invoke(true);
        SetWindowPos(handle, new IntPtr(-1), screenX, screenY, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0010 | 0x0020 | 0x0040);
        dragTimer.Start();
        return true;
    }

    public void UpdateDrag(POINT cursor)
    {
        if (!dragging) return;
        selectedTaskbar = FindTaskbars().FirstOrDefault(bar => DockGeometry.Contains(bar.Area, cursor.X, cursor.Y));
        Mode = selectedTaskbar is null ? WidgetMode.Desktop : WidgetMode.Taskbar;
        uint dpi = DpiAt(cursor);
        SetLayout(Mode == WidgetMode.Taskbar ? DockGeometry.TaskbarLayout(selectedTaskbar!.Area, dpi) : DockGeometry.DesktopLayout);
        double scale = dpi / 96.0;
        int x = cursor.X - (int)Math.Round(grabX * Layout.Width * scale);
        int y = cursor.Y - (int)Math.Round(grabY * Layout.Height * scale);
        Place(x, y, cursor);
    }

    public void EndDrag()
    {
        if (!dragging) return;
        var position = Position; screenX = position.X; screenY = position.Y;
        dragging = false; dragTimer.Stop(); Attach(); DragStateChanged?.Invoke(false);
    }

    public void ReturnToDesktop()
    {
        dragging = false; dragTimer.Stop(); Mode = WidgetMode.Desktop; selectedTaskbar = null;
        DragStateChanged?.Invoke(false);
        var area = Forms.Screen.PrimaryScreen!.WorkingArea; screenX = area.Left + 16; screenY = area.Top + 16;
        Attach();
    }

    // Physical screen coordinates, mapped to the selected Explorer parent's client coordinates.
    public void Place(int x, int y, POINT? cursor = null)
    {
        if (!IsAttached) return;
        double oldScale = Math.Max(1, GetDpiForWindow(handle)) / 96.0;
        var anchor = cursor ?? new POINT { X = x + (int)Math.Round(Layout.Width * oldScale / 2), Y = y + (int)Math.Round(Layout.Height * oldScale / 2) };
        uint dpi = DpiAt(anchor);
        if (Mode == WidgetMode.Taskbar)
        {
            if (!dragging && GetWindowRect(parent, out var rect)) selectedTaskbar = new(parent, new(rect.Left, rect.Top, rect.Right, rect.Bottom));
            if (selectedTaskbar is null) return;
            SetLayout(DockGeometry.TaskbarLayout(selectedTaskbar.Area, dpi));
        }
        else
        {
            SetLayout(DockGeometry.DesktopLayout);
        }
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y));
        if (dragging)
        {
            var area = screen.Bounds;
            (x, y) = DockGeometry.PlaceDragPreview(x, y, new(area.Left, area.Top, area.Right, area.Bottom), Layout, dpi);
        }
        else if (Mode == WidgetMode.Taskbar) (x, y) = DockGeometry.PlaceOnTaskbar(x, y, selectedTaskbar!.Area, dpi);
        else
        {
            var area = screen.WorkingArea;
            (x, y) = WidgetGeometry.ConstrainPosition(x, y, new(area.Left, area.Top, area.Right, area.Bottom), dpi);
        }
        screenX = x; screenY = y;
        var point = new POINT { X = x, Y = y };
        if (!dragging) MapWindowPoints(IntPtr.Zero, parent, ref point, 1);
        double scale = dpi / 96.0;
        SetWindowPos(handle, dragging ? new IntPtr(-1) : IntPtr.Zero, point.X, point.Y, (int)Math.Round(Layout.Width * scale), (int)Math.Round(Layout.Height * scale), 0x0010 | 0x0020 | 0x0040);
    }

    private void SetLayout(WidgetLayout layout)
    {
        if (Layout == layout) return;
        Layout = layout; LayoutChanged?.Invoke(layout);
    }
    private bool Reparent(IntPtr target)
    {
        long oldStyle = GetWindowLongPtr(handle, -16).ToInt64();
        long oldExtendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
        IntPtr oldParent = (oldStyle & 0x40000000L) != 0 ? GetParent(handle) : IntPtr.Zero;
        long style = target == IntPtr.Zero ? (oldStyle & ~0x40000000L) | 0x80000000L : (oldStyle & ~0x80000000L) | 0x40000000L;
        // Detach before changing child -> popup; attach after changing popup -> child.
        Marshal.SetLastPInvokeError(0);
        if (target != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (target == IntPtr.Zero) SetParent(handle, target);
        SetWindowLongPtr(handle, -16, new IntPtr(style));
        long extendedStyle = oldExtendedStyle | 0x08000080L;
        if (target != IntPtr.Zero) extendedStyle &= ~0x8L;
        SetWindowLongPtr(handle, -20, new IntPtr(extendedStyle));
        if (target != IntPtr.Zero) SetParent(handle, target);
        // WPF may give a popup a hidden owner for ShowInTaskbar=false. GetParent returns
        // that owner, so use GA_ROOT to distinguish it from an actual child parent.
        bool success = target == IntPtr.Zero ? GetAncestor(handle, 2) == handle : GetParent(handle) == target;
        if (success) return true;
        int error = Marshal.GetLastPInvokeError();
        if (oldParent == IntPtr.Zero) SetParent(handle, IntPtr.Zero);
        SetWindowLongPtr(handle, -16, new IntPtr(oldStyle));
        SetWindowLongPtr(handle, -20, new IntPtr(oldExtendedStyle));
        if (oldParent != IntPtr.Zero) SetParent(handle, oldParent);
        Marshal.SetLastPInvokeError(error);
        return false;
    }
    private bool Fail(string message) { Error = message; ShowWindow(handle, 0); AttachmentChanged?.Invoke(); return false; }
    private static IntPtr FindDesktop()
    {
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((top, _) => { desktop = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null); return desktop == IntPtr.Zero; }, IntPtr.Zero);
        return desktop;
    }
    private static List<TaskbarSurface> FindTaskbars()
    {
        var results = new List<TaskbarSurface>();
        EnumWindows((top, _) =>
        {
            if (ClassName(top) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsWindowVisible(top) && GetWindowRect(top, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                results.Add(new(top, new(rect.Left, rect.Top, rect.Right, rect.Bottom)));
            return true;
        }, IntPtr.Zero);
        return results;
    }
    private static double Distance(DesktopArea area, int x, int y)
    {
        double dx = x - (double)Math.Clamp(x, area.Left, area.Right), dy = y - (double)Math.Clamp(y, area.Top, area.Bottom);
        return dx * dx + dy * dy;
    }
    private static uint DpiAt(POINT point) => GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out var x, out _) == 0 && x > 0 ? x : 96;
    public static POINT CursorPosition() { GetCursorPos(out var point); return point; }
    public static bool LeftButtonDown => (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 2 : 1) & 0x8000) != 0;
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x02E0) window.Dispatcher.BeginInvoke(() => { if (dragging) UpdateDrag(CursorPosition()); else Place(screenX, screenY); });
        return IntPtr.Zero;
    }
    public void Dispose() { timer.Stop(); dragTimer.Stop(); source?.RemoveHook(WindowMessage); }
    private static string ClassName(IntPtr value) { var text = new StringBuilder(128); if (value != IntPtr.Zero) GetClassName(value, text, text.Capacity); return text.ToString(); }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT point, uint count);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT bounds);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
}
