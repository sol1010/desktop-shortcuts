using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace DesktopShortcuts;

internal sealed class WidgetResizer
{
    private readonly Window _window;
    private readonly Action<double, double> _setSize;
    private readonly Action<double> _restoreAutoHeight;
    private readonly Action _saveSize;
    private WidgetResizeGesture? _gesture;
    private bool _changed;
    private nint Handle => new WindowInteropHelper(_window).Handle;

    public WidgetResizer(Window window, WidgetResizeLayer layer, Action<double, double> setSize,
        Action<double> restoreAutoHeight, Action saveSize)
    {
        _window = window;
        _setSize = setSize;
        _restoreAutoHeight = restoreAutoHeight;
        _saveSize = saveSize;
        layer.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Start));
        layer.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(Resize));
        layer.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Complete));
        window.SizeChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitOnScreen);
    }

    public void FitOnScreen()
    {
        if (!_window.IsVisible || _gesture is not null)
        {
            return;
        }

        Drawing.Rectangle bounds = NativeWindow.Bounds(Handle);
        Drawing.Rectangle area = NativeWindow.WorkingArea(bounds);
        if (_window.SizeToContent == SizeToContent.Manual)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(_window);
            _window.Width = Math.Min(_window.Width, Math.Max(_window.MinWidth, area.Width / dpi.DpiScaleX));
            _window.Height = Math.Min(_window.Height, Math.Max(_window.MinHeight, area.Height / dpi.DpiScaleY));
        }

        Drawing.Point position = NativeWindow.KeepOnScreen(bounds.Location, bounds.Size);
        if (position != bounds.Location)
        {
            NativeWindow.Move(Handle, position);
        }
    }

    private void Start(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is not Thumb { Tag: WidgetResizeEdge edge })
        {
            return;
        }

        Drawing.Rectangle bounds = NativeWindow.Bounds(Handle);
        _gesture = new WidgetResizeGesture(edge, NativeWindow.CursorPosition, bounds, NativeWindow.WorkingArea(bounds),
            VisualTreeHelper.GetDpi(_window), _window.SizeToContent == SizeToContent.Manual);
        _changed = false;
    }

    private void Resize(object sender, DragDeltaEventArgs e)
    {
        if (_gesture is not WidgetResizeGesture gesture)
        {
            return;
        }

        Drawing.Rectangle bounds = gesture.Resize(NativeWindow.CursorPosition, _window.MinWidth, _window.MinHeight);
        if (!_changed && bounds == gesture.Bounds)
        {
            return;
        }

        ApplyBounds(bounds, gesture.Dpi);
        _changed = true;
    }

    private void Complete(object sender, DragCompletedEventArgs e)
    {
        WidgetResizeGesture? pending = _gesture;
        _gesture = null;
        if (pending is not WidgetResizeGesture gesture || !_changed)
        {
            return;
        }

        if (e.Canceled)
        {
            if (gesture.WasManual)
            {
                ApplyBounds(gesture.Bounds, gesture.Dpi);
            }
            else
            {
                _restoreAutoHeight(gesture.Bounds.Width / gesture.Dpi.DpiScaleX);
                NativeWindow.Move(Handle, gesture.Bounds.Location);
            }
            return;
        }

        FitOnScreen();
        _window.UpdateLayout();
        _saveSize();
    }

    private void ApplyBounds(Drawing.Rectangle bounds, DpiScale dpi)
    {
        _setSize(bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
        NativeWindow.Move(Handle, bounds.Location);
    }
}
