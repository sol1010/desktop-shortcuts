using System;
using System.Drawing;
using System.Windows;

namespace DesktopShortcuts;

[Flags]
internal enum WidgetResizeEdge { Left = 1, Right = 2, Top = 4, Bottom = 8 }

internal readonly record struct WidgetResizeGesture(
    WidgetResizeEdge Edge, System.Drawing.Point Cursor, Rectangle Bounds, Rectangle WorkArea, DpiScale Dpi, bool WasManual)
{
    public Rectangle Resize(System.Drawing.Point cursor, double minWidth, double minHeight)
    {
        int dx = cursor.X - Cursor.X;
        int dy = cursor.Y - Cursor.Y;
        int width = (int)Math.Ceiling(minWidth * Dpi.DpiScaleX);
        int height = (int)Math.Ceiling(minHeight * Dpi.DpiScaleY);

        int left = Edge.HasFlag(WidgetResizeEdge.Left)
            ? Math.Clamp(Bounds.Left + dx, Math.Min(WorkArea.Left, Bounds.Right - width), Bounds.Right - width) : Bounds.Left;
        int right = Edge.HasFlag(WidgetResizeEdge.Right)
            ? Math.Clamp(Bounds.Right + dx, Bounds.Left + width, Math.Max(WorkArea.Right, Bounds.Left + width)) : Bounds.Right;
        int top = Edge.HasFlag(WidgetResizeEdge.Top)
            ? Math.Clamp(Bounds.Top + dy, Math.Min(WorkArea.Top, Bounds.Bottom - height), Bounds.Bottom - height) : Bounds.Top;
        int bottom = Edge.HasFlag(WidgetResizeEdge.Bottom)
            ? Math.Clamp(Bounds.Bottom + dy, Bounds.Top + height, Math.Max(WorkArea.Bottom, Bounds.Top + height)) : Bounds.Bottom;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}
