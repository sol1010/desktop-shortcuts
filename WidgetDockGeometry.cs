using System;
using System.Drawing;

namespace DesktopShortcuts;

internal static class WidgetDockGeometry
{
    public const double Gap = 2;
    private const double CaptureDistance = 14;

    public static bool IsBelow(Rectangle todo, Rectangle calendar, double dpiY)
    {
        int overlap = Math.Min(todo.Right, calendar.Right) - Math.Max(todo.Left, calendar.Left);
        double gap = todo.Top - calendar.Bottom;
        return overlap >= Math.Min(todo.Width, calendar.Width) / 3.0 &&
            Math.Abs(gap - Gap * dpiY) <= CaptureDistance * dpiY;
    }
}
