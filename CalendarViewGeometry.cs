using System.Drawing;

namespace DesktopShortcuts;

internal static class CalendarViewGeometry
{
    public static bool ChooseRightHalf(Rectangle workArea, Rectangle current) =>
        current.Left + current.Width / 2.0 >= workArea.Left + workArea.Width / 2.0;

    public static Rectangle Half(Rectangle workArea, bool right)
    {
        int leftWidth = workArea.Width / 2;
        return right
            ? new Rectangle(workArea.Left + leftWidth, workArea.Top, workArea.Width - leftWidth, workArea.Height)
            : new Rectangle(workArea.Left, workArea.Top, leftWidth, workArea.Height);
    }
}
