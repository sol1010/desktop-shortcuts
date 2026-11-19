using System;
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
    internal const double EmptyDockHeight = 86;
    private const double MinimumTasksHeight = 30;
    private readonly App _app;
    private readonly TodoSession _session;
    private readonly TodoBoard _board;
    private readonly WidgetResizer _resizer;
    private Drawing.Point? _dockedPress;
    private Point _itemDragStart;
    private TaskDropRow? _dropRow;
    private bool _dropAfter;
    private TodoDay Day => _session.Day;
    private TodoItem? _itemToFocus;
    private bool _ready;
    private bool _allowClose;
    private CalendarWindow? _dockedCalendar;
    private bool _memoDragWasAuto;
    private double? _pendingMemoShare;
    private nint Handle => new WindowInteropHelper(this).Handle;
    private bool IsDocked => _dockedCalendar is not null;
    private bool IsCompactDock => IsDocked && _session.ShouldCompactEmptyTodo;
    private double NoteLineHeight => Math.Ceiling(NoteInput.FontFamily.LineSpacing * NoteInput.FontSize)
        + NoteInput.Padding.Top + NoteInput.Padding.Bottom;
    private double MinimumMemoHeight => NoteLineHeight + MemoBody.Padding.Top + MemoBody.Padding.Bottom;

    internal TodoWindow(App app, TodoSession session)
    {
        _app = app;
        _session = session;
        _board = session.Board;
        InitializeComponent();
        DataContext = session;
        Topmost = _board.AlwaysOnTop;
        UpdateContentLayout();
        RestorePreferredSize();
        Card.Transparency = _board.BackgroundTransparency;
        Card.ContextMenu = BuildWindowMenu();
        _resizer = new WidgetResizer(this, ResizeLayer, SetManualSize, RestoreAutoHeight, SaveSize);
        _ = new WidgetSectionResizer(MemoDivider, TodoLayout, TasksRow, PreviewMemoSplit, FinishMemoResize);
        session.PropertyChanged += SessionChanged;
        session.DragEnded += ClearTaskDropPreview;
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

    internal FrameworkElement MoveSurfaceToCalendar(CalendarWindow calendar, double width, double height)
    {
        if (IsVisible)
        {
            Hide();
        }

        ResizeLayer.Visibility = Visibility.Collapsed;
        StandaloneHost.Child = null;
        _dockedCalendar = calendar;
        ApplyBackgroundTransparency(_board.Calendar.BackgroundTransparency);
        SetManualSize(width, height);
        UpdateContentLayout();
        return WidgetSurface;
    }

    internal void RestoreSurface(Drawing.Point position)
    {
        StandaloneHost.Child = WidgetSurface;
        ResizeLayer.Visibility = Visibility.Visible;
        _dockedCalendar = null;
        ApplyBackgroundTransparency(_board.BackgroundTransparency);
        UpdateContentLayout();
        _board.X = position.X;
        _board.Y = position.Y;
    }

    internal void ApplyBackgroundTransparency(int? value) => Card.Transparency = value;

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
            UpdateContentLayout();
        }

        Width = width;
        Height = height;
    }

    private void RestoreAutoHeight(double width)
    {
        Width = width;
        Height = double.NaN;
        SizeToContent = SizeToContent.Height;
        UpdateContentLayout();
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsDocked && e.PropertyName == nameof(TodoSession.HasTodoContent))
        {
            UpdateContentLayout();
        }
    }

    private void MemoTextChanged(object sender, TextChangedEventArgs e) => UpdateContentLayout();

    private void UpdateContentLayout()
    {
        bool manual = SizeToContent == SizeToContent.Manual;
        bool compact = IsCompactDock;
        bool hasNote = !string.IsNullOrWhiteSpace(NoteInput.Text);
        double? share = manual && !compact ? _pendingMemoShare ?? _board.MemoShare : null;
        bool hasSplit = share is > 0 and < 1;

        TaskScroll.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MemoDivider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactMemoLine.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        TaskScroll.MaxHeight = manual ? double.PositiveInfinity : 220;
        TasksRow.MinHeight = manual && !compact ? MinimumTasksHeight : 0;
        NoteInput.MaxHeight = !compact && (hasNote || hasSplit) ? double.PositiveInfinity : NoteLineHeight;
        MemoBody.VerticalAlignment = manual && !compact && (hasNote || hasSplit) ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        MemoBody.MaxHeight = manual ? double.PositiveInfinity : 84;

        if (hasSplit)
        {
            TasksRow.Height = new GridLength(1 - share!.Value, GridUnitType.Star);
            MemoRow.Height = new GridLength(share.Value, GridUnitType.Star);
            MemoRow.MinHeight = MinimumMemoHeight;
        }
        else
        {
            TasksRow.Height = manual && !compact ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
            bool expandMemo = manual && !compact && hasNote;
            MemoRow.Height = expandMemo ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            MemoRow.MinHeight = expandMemo ? 26 : 0;
        }
    }

    private bool PreviewMemoSplit(double tasksHeight)
    {
        double available = TodoLayout.ActualHeight - TodoHeadingRow.ActualHeight - MemoHeadingRow.ActualHeight
            - TodoStatusRow.ActualHeight;
        if (IsCompactDock || WidgetSectionResizer.GetBottomShare(available, tasksHeight, TasksRow.ActualHeight,
                MinimumTasksHeight, MinimumMemoHeight) is not double share)
        {
            return false;
        }

        if (_pendingMemoShare is null)
        {
            _memoDragWasAuto = SizeToContent != SizeToContent.Manual;
        }

        _pendingMemoShare = share;
        if (SizeToContent != SizeToContent.Manual)
        {
            SetManualSize(Width, Card.ActualHeight);
        }

        UpdateContentLayout();
        return true;
    }

    private void FinishMemoResize(bool canceled)
    {
        if (_pendingMemoShare is not double share)
        {
            return;
        }

        _pendingMemoShare = null;
        if (canceled)
        {
            if (_memoDragWasAuto)
            {
                RestoreAutoHeight(Width);
            }
            else
            {
                UpdateContentLayout();
            }
            return;
        }

        _board.MemoShare = share;
        _board.SizeManuallySet = true;
        if (IsDocked)
        {
            _session.QueueSave();
        }
        else
        {
            SaveSize();
        }
    }

    private void SaveSize()
    {
        _board.SizeManuallySet = true;
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

    protected override void OnClosed(EventArgs e)
    {
        _session.PropertyChanged -= SessionChanged;
        _session.DragEnded -= ClearTaskDropPreview;
        base.OnClosed(e);
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
        var hide = new MenuItem { Header = "할 일 목록 숨기기" };
        hide.Click += (_, _) => HideBoard();
        menu.Items.Add(topmost);
        menu.Items.Add(Card.CreateTransparencyMenu(value =>
        {
            if (_dockedCalendar is not null)
            {
                return _dockedCalendar.SetBackgroundTransparency(value);
            }

            _board.BackgroundTransparency = value;
            _session.QueueSave();
            return true;
        }));
        menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            paste.IsEnabled = _session.CanPasteItem;
            topmost.IsChecked = _board.AlwaysOnTop;
            hide.Header = _board.DockedToCalendar ? "달력·할 일 숨기기" : "할 일 목록 숨기기";
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
            parent.AreChildrenCollapsed = false;
            _itemToFocus = child;
            parent.Children.Add(child);
            _session.NotifyTaskCountsChanged();
            _session.QueueSave();
        }
    }

    private void ToggleChildren(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem parent })
        {
            parent.AreChildrenCollapsed = !parent.AreChildrenCollapsed;
            _session.QueueSave();
        }
    }

    private void PressItemDrag(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement grip && grip.CaptureMouse())
        {
            _itemDragStart = e.GetPosition(WidgetSurface);
            e.Handled = true;
        }
    }

    private void ContinueItemDrag(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true, DataContext: TodoItem item } grip ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector delta = e.GetPosition(WidgetSurface) - _itemDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        grip.ReleaseMouseCapture();
        if (_session.StartDrag(item) is { } drag)
        {
            try
            {
                DragDrop.DoDragDrop(grip, new DataObject(TodoDrag.Format, drag.Token), DragDropEffects.Move);
            }
            finally
            {
                _session.EndDrag();
            }
        }
        e.Handled = true;
    }

    private void ReleaseItemDrag(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } grip)
        {
            grip.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void ShowItemDragCursor(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.SizeAll);
        e.Handled = true;
    }

    private void DragOverTask(object sender, DragEventArgs e)
    {
        if (_session.ReadDrag(e.Data) is not { } drag)
        {
            return;
        }

        TaskDropRow? row = FindDropRow(e.OriginalSource as DependencyObject, drag);
        SetTaskDropPreview(row, row is not null && row.IsAfter(e.GetPosition(row).Y));
        e.Effects = row is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropOnTask(object sender, DragEventArgs e)
    {
        if (_session.ReadDrag(e.Data) is not { } drag)
        {
            return;
        }

        TaskDropRow? row = FindDropRow(e.OriginalSource as DependencyObject, drag);
        bool after = row is not null && row.IsAfter(e.GetPosition(row).Y);
        SetTaskDropPreview(null, false);
        bool moved = row?.DataContext is TodoItem anchor && _session.Reorder(drag, anchor, after);
        e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private TaskDropRow? FindDropRow(DependencyObject? element, TodoDrag drag)
    {
        while (element is not null && element != TaskScroll)
        {
            if (element is TaskDropRow { DataContext: TodoItem anchor } row && _session.CanReorder(drag, anchor))
            {
                return row;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent;
        }

        return null;
    }

    private void LeaveTaskList(object sender, DragEventArgs e)
    {
        if (_dropRow is not null && !new Rect(TaskScroll.RenderSize).Contains(Mouse.GetPosition(TaskScroll)))
        {
            SetTaskDropPreview(null, false);
        }
    }

    private void ClearTaskDropPreview(object? sender, EventArgs e) => SetTaskDropPreview(null, false);

    private void SetTaskDropPreview(TaskDropRow? row, bool after)
    {
        if (_dropRow == row && _dropAfter == after)
        {
            return;
        }

        _dropRow?.ShowDropGap(null);
        _dropRow = row;
        _dropAfter = after;
        row?.ShowDropGap(after);
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
        if (sender is MenuItem { DataContext: TodoItem item } && Day.FindList(item) is not null)
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

        if (item.Children.Count > 0 && MessageBox.Show(this, "이 할 일과 하위 할 일을 함께 삭제할까요?",
                "할 일 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        if (Day.FindList(item)?.Remove(item) == true)
        {
            _session.NotifyTaskCountsChanged();
            _session.QueueSave();
        }
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
