using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DesktopShortcuts;

internal sealed class WidgetSectionResizer
{
    private double _startY;
    private double _startHeight;

    public WidgetSectionResizer(Thumb divider, FrameworkElement layout, RowDefinition topRow,
        Func<double, bool> preview, Action<bool> complete)
    {
        divider.DragStarted += (_, _) =>
        {
            _startY = Mouse.GetPosition(layout).Y;
            _startHeight = topRow.ActualHeight;
        };
        divider.DragDelta += (_, _) => preview(_startHeight + Mouse.GetPosition(layout).Y - _startY);
        divider.DragCompleted += (_, args) => complete(args.Canceled);
        divider.KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Up or Key.Down))
            {
                return;
            }

            if (preview(topRow.ActualHeight + (args.Key == Key.Up ? -8 : 8)))
            {
                complete(false);
            }

            args.Handled = true;
        };
    }

    public static double? GetBottomShare(double available, double topHeight, double currentTopHeight,
        double minimumTopHeight, double minimumBottomHeight)
    {
        double maximumTopHeight = available - minimumBottomHeight;
        if (maximumTopHeight <= minimumTopHeight)
        {
            return null;
        }

        topHeight = Math.Clamp(topHeight, minimumTopHeight, maximumTopHeight);
        return Math.Abs(topHeight - currentTopHeight) < 0.5 ? null : 1 - topHeight / available;
    }
}
