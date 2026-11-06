namespace UsageIsland.Core;

public readonly record struct WidgetLayout(WidgetMode Mode, double Width, double Height);

public static class DockGeometry
{
    public static WidgetLayout DesktopLayout => new(WidgetMode.Desktop, WidgetGeometry.LogicalWidth, WidgetGeometry.LogicalHeight);
    public static bool Contains(DesktopArea rect, int x, int y) => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    public static WidgetLayout TaskbarLayout(DesktopArea taskbar, uint dpi)
    {
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        bool horizontal = taskbar.Right - taskbar.Left >= taskbar.Bottom - taskbar.Top;
        return horizontal
            ? new(WidgetMode.Taskbar, 42, Math.Clamp((taskbar.Bottom - taskbar.Top) / scale - 4, 12, 36))
            : new(WidgetMode.Taskbar, Math.Clamp((taskbar.Right - taskbar.Left) / scale - 4, 12, 42), 36);
    }
    public static (int X, int Y) PlaceOnTaskbar(int x, int y, DesktopArea taskbar, uint dpi)
    {
        var layout = TaskbarLayout(taskbar, dpi);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int width = (int)Math.Round(layout.Width * scale), height = (int)Math.Round(layout.Height * scale);
        return taskbar.Right - taskbar.Left >= taskbar.Bottom - taskbar.Top
            ? (Math.Clamp(x, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - width)), taskbar.Top + (taskbar.Bottom - taskbar.Top - height) / 2)
            : (taskbar.Left + (taskbar.Right - taskbar.Left - width) / 2, Math.Clamp(y, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - height)));
    }
    public static WidgetMode ModeAt(int x, int y, IEnumerable<DesktopArea> taskbars) => taskbars.Any(rect => Contains(rect, x, y)) ? WidgetMode.Taskbar : WidgetMode.Desktop;
    public static (int X, int Y) PlaceDragPreview(int x, int y, DesktopArea screen, WidgetLayout layout, uint dpi)
    {
        if (layout.Mode == WidgetMode.Desktop) return WidgetGeometry.ConstrainPosition(x, y, screen, dpi);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int width = (int)Math.Round(layout.Width * scale), height = (int)Math.Round(layout.Height * scale);
        return (Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width)), Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height)));
    }
}
