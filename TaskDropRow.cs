using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopShortcuts;

public sealed class TaskDropRow : Border
{
    private const double DropGapHeight = 24;

    internal bool IsAfter(double y) => y >= Padding.Top + (ActualHeight - Padding.Top - Padding.Bottom) / 2;

    internal void ShowDropGap(bool? after)
    {
        Padding = after switch
        {
            true => new Thickness(0, 0, 0, DropGapHeight),
            false => new Thickness(0, DropGapHeight, 0, 0),
            null => new Thickness(0)
        };
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if ((Padding.Top == 0 && Padding.Bottom == 0) || ActualWidth <= 6 ||
            TryFindResource("ShortcutFolderBrush") is not Brush accent)
        {
            return;
        }

        double top = Padding.Top > 0 ? 0 : ActualHeight - DropGapHeight;
        var slot = new Rect(3, top + 3, ActualWidth - 6, DropGapHeight - 6);
        drawingContext.PushOpacity(0.62);
        drawingContext.DrawRoundedRectangle(null, new Pen(accent, 1), slot, 4, 4);
        drawingContext.Pop();
    }
}
