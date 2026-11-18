using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopShortcuts;

internal static class NativeWindow
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static Point CursorPosition
    {
        get
        {
            GetCursorPos(out NativePoint point);
            return new Point(point.X, point.Y);
        }
    }

    public static Rectangle Bounds(nint handle)
    {
        GetWindowRect(handle, out NativeRect rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static void Move(nint handle, Point position)
    {
        SetWindowPos(handle, 0, position.X, position.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void Place(nint handle, Rectangle bounds)
    {
        SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static Rectangle WorkingArea(Rectangle bounds) => Screen.FromRectangle(bounds).WorkingArea;

    public static Point KeepOnScreen(Point position, Size windowSize)
    {
        var desired = new Rectangle(position, windowSize);
        Rectangle area = WorkingArea(desired);
        return new Point(
            Math.Clamp(position.X, area.Left, Math.Max(area.Left, area.Right - windowSize.Width)),
            Math.Clamp(position.Y, area.Top, Math.Max(area.Top, area.Bottom - windowSize.Height)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
