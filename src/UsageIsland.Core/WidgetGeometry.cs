namespace UsageIsland.Core;

public readonly record struct DesktopArea(int Left, int Top, int Right, int Bottom);

public static class WidgetGeometry
{
    public const double LogicalWidth = 192;
    public const double LogicalHeight = 6;
    public const double VisibleLeft = 0;
    public const double VisibleTop = 0;
    public const double VisibleRight = LogicalWidth;
    public const double VisibleBottom = LogicalHeight;
    public static (int X, int Y) ConstrainPosition(int x, int y, DesktopArea area, uint dpi)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int minX = area.Left - (int)Math.Floor(VisibleLeft * scale);
        int minY = area.Top - (int)Math.Floor(VisibleTop * scale);
        int maxX = Math.Max(minX, area.Right - (int)Math.Ceiling(VisibleRight * scale));
        int maxY = Math.Max(minY, area.Bottom - (int)Math.Ceiling(VisibleBottom * scale));
        int snap = (int)Math.Round(8 * scale);
        int Constrain(int value, int min, int max)
        {
            value = Math.Clamp(value, min, max);
            if ((long)value - min <= snap && (long)value - min <= (long)max - value) return min;
            if ((long)max - value <= snap) return max;
            return value;
        }
        return (Constrain(x, minX, maxX), Constrain(y, minY, maxY));
    }
    public static (int Width, int Height) PixelSize(uint dpi)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        return ((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
    }
}
