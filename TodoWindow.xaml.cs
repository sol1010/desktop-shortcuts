using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopShortcuts;

public partial class TodoWindow : Window
{
    private readonly App _app;
    private readonly TodoSession _session;
    private readonly TodoBoard _board;
    private readonly WidgetResizer _resizer;
    private Drawing.Point? _dockedPress;
    private TodoDay Day => _session.Day;
    private TodoItem? _itemToFocus;
    private bool _ready;
    private bool _allowClose;
    private nint Handle => new WindowInteropHelper(this).Handle;

    internal TodoWindow(App app, TodoSession session)
    {
        _app = app;
        _session = session;
        _board = session.Board;
        InitializeComponent();
        DataContext = session;
        Topmost = _board.AlwaysOnTop;
        RestorePreferredSize();
        Card.ContextMenu = BuildWindowMenu();
        _resizer = new WidgetResizer(this, ResizeLayer, SetManualSize, RestoreAutoHeight, SaveSize);
        _ready = true;
    }

    internal void Reveal(bool activate)
    {
        if (!IsVisible)
        {
            RestorePreferredSize();
            Show();
            FitOnScreen();
            Drawing.Size size = NativeWindow.Bounds(Handle).Size;
            Drawing.Rectangle area = Forms.Screen.FromPoint(NativeWindow.CursorPosition).WorkingArea;
            var location = new Drawing.Point(_board.X ?? area.Right - size.Width - 24, _board.Y ?? area.Top + 24);
            NativeWindow.Move(Handle, NativeWindow.KeepOnScreen(location, size));
        }

        if (!_board.IsVisible)
        {
            _board.IsVisible = true;
            _session.QueueSave();
        }

        if (activate)
        {
            Activate();
        }
    }

    internal void FitOnScreen() => _resizer.FitOnScreen();

    internal Drawing.Rectangle ScreenBounds => NativeWindow.Bounds(Handle);

    internal double StandaloneHeight => ActualHeight > 0 ? ActualHeight : double.IsFinite(Height) ? Height : 180;

    internal void AllowClose() => _allowClose = true;

    internal FrameworkElement MoveSurfaceToCalendar(double width, double height)
    {
        if (IsVisible)
        {
            Hide();
        }

        ResizeLayer.Visibility = Visibility.Collapsed;
        StandaloneHost.Child = null;
        SetManualSize(width, height);
        return WidgetSurface;
    }

    internal void RestoreSurface(Drawing.Point position)
    {
        StandaloneHost.Child = WidgetSurface;
        ResizeLayer.Visibility = Visibility.Visible;
        _board.X = position.X;
        _board.Y = position.Y;
    }

    private void OpenCalendar(object sender, RoutedEventArgs e) => _app.ShowCalendar();

    private void RestorePreferredSize()
    {
        if (_board.Width is double width && _board.Height is double height &&
            double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
        {
            SetManualSize(
                Math.Clamp(width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth)),
                Math.Clamp(height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight)));
        }
    }

    private void SetManualSize(double width, double height)
    {
        if (SizeToContent != SizeToContent.Manual)
        {
            SizeToContent = SizeToContent.Manual;
            TasksRow.Height = new GridLength(2, GridUnitType.Star);
            TasksRow.MinHeight = 30;
            MemoRow.Height = new GridLength(1, GridUnitType.Star);
            MemoRow.MinHeight = 26;
            TaskScroll.MaxHeight = double.PositiveInfinity;
        }

        Width = width;
        Height = height;
    }

    private void RestoreAutoHeight(double width)
    {
        Width = width;
        Height = double.NaN;
        TasksRow.Height = GridLength.Auto;
        TasksRow.MinHeight = 0;
        MemoRow.Height = new GridLength(84);
        MemoRow.MinHeight = 0;
        TaskScroll.MaxHeight = 220;
        SizeToContent = SizeToContent.Height;
    }

    private void SaveSize()
    {
        _board.Width = Width;
        _board.Height = Height;
        RecordPosition();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose && !e.Cancel)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(HideBoard);
        }
    }

    private ContextMenu BuildWindowMenu()
    {
        var menu = new ContextMenu();
        var paste = new MenuItem { Header = "항목 붙여넣기" };
        paste.Click += (_, _) => _session.PasteItem();
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        var topmost = new MenuItem { Header = "항상 위", IsCheckable = true };
        topmost.Click += (_, _) => _app.SetWidgetTopmost(this, topmost.IsChecked);
        var hide = new MenuItem { Header = "투두 목록 숨기기" };
        hide.Click += (_, _) => HideBoard();
        menu.Items.Add(topmost);
        menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            paste.IsEnabled = _session.CanPasteItem;
            topmost.IsChecked = _board.AlwaysOnTop;
            hide.Header = _board.DockedToCalendar ? "달력·투두 숨기기" : "투두 목록 숨기기";
        };
        return menu;
    }

    private void DragHeading(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        if (_board.DockedToCalendar)
        {
            _dockedPress = DragArea.CaptureMouse() ? NativeWindow.CursorPosition : null;
            return;
        }

        MoveStandalone(allowDock: true);
    }

    private void ContinueDockedDrag(object sender, MouseEventArgs e)
    {
        if (_dockedPress is not Drawing.Point start || e.LeftButton != MouseButtonState.Pressed ||
            !DragArea.IsMouseCaptured)
        {
            return;
        }

        Drawing.Point cursor = NativeWindow.CursorPosition;
        DpiScale dpi = VisualTreeHelper.GetDpi(DragArea);
        if (Math.Abs(cursor.X - start.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
            Math.Abs(cursor.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
        {
            return;
        }

        _dockedPress = null;
        DragArea.ReleaseMouseCapture();
        if (_app.DetachTodoForDrag() && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            MoveStandalone(allowDock: false);
        }

        e.Handled = true;
    }

    private void EndDockedPress(object sender, MouseButtonEventArgs e)
    {
        if (_dockedPress is not null)
        {
            _dockedPress = null;
            DragArea.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void MoveStandalone(bool allowDock)
    {
        Drawing.Point before = ScreenBounds.Location;
        DragMove();
        if (ScreenBounds.Location == before)
        {
            return;
        }

        if (allowDock && _app.TryDockTodo())
        {
            return;
        }

        _app.SnapWidget(this, skipCalendar: !allowDock);
        RecordPosition();
    }

    private void RecordPosition()
    {
        Drawing.Point position = NativeWindow.Bounds(Handle).Location;
        _board.X = position.X;
        _board.Y = position.Y;
        _session.QueueSave();
    }

    private void HideBoard()
    {
        if (_board.DockedToCalendar)
        {
            _app.HideDockedWidgets();
            return;
        }

        _board.IsVisible = false;
        _session.QueueSave();
        if (_session.Flush())
        {
            Hide();
        }
        else
        {
            _board.IsVisible = true;
        }
    }

    private void AddRoot(object sender, RoutedEventArgs e)
    {
        var item = new TodoItem();
        _itemToFocus = item;
        Day.Items.Add(item);
        _session.QueueSave();
    }

    private void AddChild(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem parent })
        {
            var child = new TodoItem();
            _itemToFocus = child;
            parent.Children.Add(child);
            _session.QueueSave();
        }
    }

    private void TaskInputLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox input && _itemToFocus is not null && ReferenceEquals(input.DataContext, _itemToFocus))
        {
            _itemToFocus = null;
            input.BringIntoView();
            input.Focus();
        }
    }

    private void CopyItem(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TodoItem item } && FindItemList(item) is not null)
        {
            _session.CopyItem(item);
        }
    }

    private void RemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TodoItem item })
        {
            return;
        }

        if (item.Children.Count > 0 && MessageBox.Show(this, "이 투두와 하위 투두를 함께 삭제할까요?",
                "투두 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        if (FindItemList(item)?.Remove(item) == true)
        {
            _session.QueueSave();
        }
    }

    private ObservableCollection<TodoItem>? FindItemList(TodoItem item)
    {
        if (Day.Items.Contains(item))
        {
            return Day.Items;
        }

        foreach (TodoItem parent in Day.Items)
        {
            if (parent.Children.Contains(item))
            {
                return parent.Children;
            }
        }

        return null;
    }

    private void ContentChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            _session.QueueSave();
        }
    }

    private void CheckChanged(object sender, RoutedEventArgs e) => _session.QueueSave();

}
