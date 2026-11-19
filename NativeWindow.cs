using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopShortcuts;

internal static class NativeWindow
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x0008;
    private const int WS_EX_TRANSPARENT = 0x0020;
    private const int WS_EX_TOOLWINDOW = 0x0080;
    private const int DWMWA_CLOAKED = 14;

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

    internal static void SetTopmost(nint handle, bool pinned)
    {
        SetWindowPos(handle, pinned ? -1 : -2, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    internal static void RestoreTopmostOrder(IEnumerable<nint> pinned)
    {
        var remaining = new Dictionary<nint, Rectangle>();
        foreach (nint handle in pinned)
        {
            if (IsVisibleOnDesktop(handle))
            {
                remaining.Add(handle, Bounds(handle));
            }
        }
        if (remaining.Count == 0)
        {
            return;
        }

        var normalWindowsAbove = new List<Rectangle>();
        var misplaced = new List<nint>();
        EnumWindows((window, _) =>
        {
            int style = GetWindowLong(window, GWL_EXSTYLE);
            if (remaining.Remove(window, out Rectangle tile))
            {
                if ((style & WS_EX_TOPMOST) == 0 || normalWindowsAbove.Exists(bounds => bounds.IntersectsWith(tile)))
                {
                    misplaced.Add(window);
                }
            }
            else if ((style & (WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) == 0 && IsVisibleOnDesktop(window))
            {
                normalWindowsAbove.Add(Bounds(window));
            }
            return remaining.Count > 0;
        }, 0);

        for (int index = misplaced.Count - 1; index >= 0; index--)
        {
            SetTopmost(misplaced[index], pinned: true);
        }
    }

    private static bool IsVisibleOnDesktop(nint handle) => handle != 0 && IsWindowVisible(handle) && !IsIconic(handle) &&
        (DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0);

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

    private delegate bool WindowEnumerator(nint window, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowEnumerator callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
