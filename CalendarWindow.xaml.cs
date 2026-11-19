using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopShortcuts;

public partial class CalendarWindow : Window
{
    private const double StandaloneMinHeight = 146;
    private const double FoldedMinHeight = 112;
    private const double WeeklyHeightThreshold = 290;
    private const double SingleLineMemoShare = 0;
    private const int MaxPreviewLines = 4;
    private readonly App _app;
    private readonly TodoSession _session;
    private readonly WidgetResizer _resizer;
    private readonly CalendarPlacement _placement;
    private readonly ObservableCollection<CalendarCell> _cells = [];
    private DateOnly _page;
    private bool _weekly;
    private bool _largeView;
    private bool _largeOnRight;
    private bool _allowClose;
    private TodoWindow? _dockedTodo;
    private bool _compactDock;
    private bool _memoDragWasAuto;
    private double? _pendingMemoShare;
    private Button? _dropDateButton;

    private nint Handle => new WindowInteropHelper(this).Handle;
    private bool IsMemoFolded => _weekly && _placement.WeeklyMemoCollapsed;
    private double CalendarMinimumHeight => IsMemoFolded ? FoldedMinHeight : StandaloneMinHeight;
    private int VisibleWeeks => _weekly ? 1
        : ((int)FirstOfMonth(_page).DayOfWeek + DateTime.DaysInMonth(_page.Year, _page.Month) + 6) / 7;
    private double MinimumDaysHeight => _weekly ? 32 : VisibleWeeks * (_largeView ? 36 : 24);
    private double NoteLineHeight => Math.Ceiling(NoteInput.FontFamily.LineSpacing * NoteInput.FontSize)
        + NoteInput.Padding.Top + NoteInput.Padding.Bottom;
    private double MinimumMemoHeight => NoteLineHeight + MemoBody.Padding.Top + MemoBody.Padding.Bottom;
    private double AvailableSectionHeight => CalendarLayout.ActualHeight - CalendarHeadingRow.ActualHeight
        - WeekdayRow.ActualHeight - MemoHeadingRow.ActualHeight - CalendarStatusRow.ActualHeight;
    private double? SavedMemoShare => _largeView ? _placement.LargeMemoShare
        : _weekly ? _placement.WeeklyMemoShare ?? _placement.MonthlyMemoShare : _placement.MonthlyMemoShare;
    private double ExpandedTodoHeight => Math.Max(_dockedTodo!.MinHeight, _session.Board.Height ?? _dockedTodo.StandaloneHeight);

    internal CalendarWindow(App app, TodoSession session)
    {
        _app = app;
        _session = session;
        _placement = session.Board.Calendar;
        _page = session.Date;

        InitializeComponent();
        DataContext = session;
        Topmost = _placement.AlwaysOnTop;
        Resources["CalendarDayFontSize"] = 12d;
        Resources["CalendarPreviewFontSize"] = 9d;
        Resources["CalendarWeekdayFontSize"] = 10d;
        _weekly = _placement.Height is double savedHeight && double.IsFinite(savedHeight) && savedHeight < WeeklyHeightThreshold;
        RefreshMemoLayout();
        if (_placement.Width is not null && _placement.Height is not null)
        {
            double height = PreferredDimension(_placement.Height, MinHeight, SystemParameters.VirtualScreenHeight, 450);
            SetManualSize(PreferredDimension(_placement.Width, MinWidth, SystemParameters.VirtualScreenWidth, Width), height);
        }

        _resizer = new WidgetResizer(this, ResizeLayer, SetManualSize, RestoreAutoHeight, SaveResizedSize);
        _ = new WidgetSectionResizer(MemoDivider, CalendarLayout, DaysRow, PreviewMemoSplit, FinishMemoResize);
        Card.Transparency = _placement.BackgroundTransparency;
        Card.ContextMenu = BuildWindowMenu();
        DayGrid.ItemsSource = _cells;
        session.PropertyChanged += SessionChanged;
        session.CalendarNoteChanged += CalendarNoteChanged;
        session.DragEnded += ClearDateDropPreview;
        Card.SizeChanged += (_, args) =>
        {
            UpdateViewForHeight(args.NewSize.Height);
            UpdateContentLayout();
        };
        DayGrid.SizeChanged += (_, _) => UpdatePreviewLayout();
        RenderPage();
    }

    internal void Reveal(bool activate)
    {
        if (!IsVisible)
        {
            Show();
            Drawing.Size size = NativeWindow.Bounds(Handle).Size;
            Drawing.Rectangle area = Forms.Screen.FromPoint(NativeWindow.CursorPosition).WorkingArea;
            var position = new Drawing.Point(_placement.X ?? area.Right - size.Width - 348, _placement.Y ?? area.Top + 24);
            NativeWindow.Move(Handle, NativeWindow.KeepOnScreen(position, size));
            if (_placement.IsLargeView)
            {
                if (_largeView)
                {
                    FitOnScreen();
                }
                else
                {
                    EnterLargeView(ScreenBounds);
                }
            }
        }

        if (!_placement.IsVisible)
        {
            _placement.IsVisible = true;
            _session.QueueSave();
        }

        if (activate)
        {
            Activate();
        }
    }

    internal Drawing.Rectangle ScreenBounds => NativeWindow.Bounds(Handle);
    internal bool HasDockedTodo => _dockedTodo is not null;

    internal void FitOnScreen()
    {
        if (!_largeView)
        {
            _resizer.FitOnScreen();
        }
        else if (IsVisible)
        {
            ApplyLargeLayout(NativeWindow.WorkingArea(ScreenBounds));
        }
    }

    private void SetViewMode(bool large)
    {
        if (_largeView == large)
        {
            return;
        }

        if (large)
        {
            SaveSize();
            EnterLargeView(ScreenBounds);
        }
        else
        {
            SetLargePresentation(false);
            RestoreNormalLayout();
        }

        _placement.IsLargeView = large;
        _session.QueueSave();
    }

    private void EnterLargeView(Drawing.Rectangle anchor)
    {
        Drawing.Rectangle workArea = NativeWindow.WorkingArea(anchor);
        _largeOnRight = CalendarViewGeometry.ChooseRightHalf(workArea, anchor);
        SetLargePresentation(true);
        ApplyLargeLayout(workArea);
    }

    private void SetLargePresentation(bool large)
    {
        _largeView = large;
        ResizeLayer.Visibility = large ? Visibility.Collapsed : Visibility.Visible;
        DragArea.Cursor = large ? Cursors.Arrow : Cursors.SizeAll;
        DragArea.ToolTip = large ? "우클릭으로 기본 모드" : "끌어서 이동 · 우클릭으로 설정";

        Resources["CalendarDayFontSize"] = large ? 17d : 12d;
        Resources["CalendarPreviewFontSize"] = large ? 12d : 9d;
        Resources["CalendarWeekdayFontSize"] = large ? 13d : 10d;
        MonthTitle.FontSize = large ? 20 : 14;
        NoteInput.FontSize = large ? 14 : 11;
        NoteInput.Padding = large ? new Thickness(8, 6, 8, 6) : new Thickness(3, 1, 3, 1);

        UpdateViewForHeight(large ? WeeklyHeightThreshold : _placement.Height ?? 450);
        RefreshMemoLayout();
    }

    private void ApplyLargeLayout(Drawing.Rectangle workArea)
    {
        Drawing.Rectangle bounds = CalendarViewGeometry.Half(workArea, _largeOnRight);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (HasDockedTodo)
        {
            ConfigureDockRows(Card.ActualHeight, ExpandedTodoHeight);
        }

        SetManualSize(bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
        if (IsVisible)
        {
            NativeWindow.Place(Handle, bounds);
        }
    }

    private void RestoreNormalLayout()
    {
        double width = PreferredDimension(_placement.Width, MinWidth, SystemParameters.VirtualScreenWidth, 354);
        double height = PreferredDimension(_placement.Height, CalendarMinimumHeight, SystemParameters.VirtualScreenHeight, 450);
        if (HasDockedTodo)
        {
            height += ConfigureDockRows(height, ExpandedTodoHeight);
        }

        SetManualSize(width, height);
        if (IsVisible)
        {
            UpdateLayout();
            Drawing.Rectangle current = ScreenBounds;
            var normal = new Drawing.Point(_placement.X ?? current.Left, _placement.Y ?? current.Top);
            NativeWindow.Move(Handle, NativeWindow.KeepOnScreen(normal, current.Size));
            FitOnScreen();
        }
    }

    internal void AttachTodo(TodoWindow todo)
    {
        if (HasDockedTodo)
        {
            return;
        }

        double calendarHeight = Math.Max(CalendarMinimumHeight, Card.ActualHeight > 0 ? Card.ActualHeight : _placement.Height ?? 450);
        double todoHeight = Math.Max(todo.MinHeight, todo.StandaloneHeight);
        double width = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
        _dockedTodo = todo;
        _session.Board.SizeManuallySet ??= _session.Board.KeepsEmptySize;
        _compactDock = _session.ShouldCompactEmptyTodo;
        _session.Board.Height ??= todoHeight;
        DockHost.Margin = new Thickness(0, WidgetDockGeometry.Gap, 0, 0);
        DockHost.Child = todo.MoveSurfaceToCalendar(this, width, todoHeight);
        DockHost.Visibility = Visibility.Visible;
        double todoPane = ConfigureDockRows(calendarHeight, todoHeight);
        if (_largeView)
        {
            _session.Board.Width ??= _placement.Width ?? 354;
            FitOnScreen();
            return;
        }

        SetManualSize(width, calendarHeight + todoPane);

        if (IsVisible)
        {
            FitOnScreen();
            UpdateLayout();
            SaveSize();
        }
    }

    private double ConfigureDockRows(double calendarHeight, double todoHeight)
    {
        double gap = WidgetDockGeometry.Gap;
        double minimumTodo = _compactDock ? TodoWindow.EmptyDockHeight : _dockedTodo!.MinHeight;
        double todoPane = (_compactDock ? minimumTodo : Math.Max(minimumTodo, todoHeight)) + gap;
        CalendarPanelRow.MinHeight = CalendarMinimumHeight;
        CalendarPanelRow.Height = new GridLength(_largeView ? 3 : calendarHeight, GridUnitType.Star);
        TodoPanelRow.MinHeight = minimumTodo + gap;
        TodoPanelRow.Height = _compactDock ? new GridLength(todoPane) : new GridLength(_largeView ? 1 : todoPane, GridUnitType.Star);
        MinHeight = CalendarMinimumHeight + TodoPanelRow.MinHeight;
        return todoPane;
    }

    private void UpdateDockedTodoLayout()
    {
        bool compact = _session.ShouldCompactEmptyTodo;
        if (!HasDockedTodo || _compactDock == compact)
        {
            return;
        }

        double calendarHeight = Card.ActualHeight > 0 ? Card.ActualHeight : _placement.Height ?? 450;
        _compactDock = compact;
        double todoPane = ConfigureDockRows(calendarHeight, ExpandedTodoHeight);
        if (!_largeView)
        {
            SetManualSize(Width, calendarHeight + todoPane);
            FitOnScreen();
        }
    }

    internal Drawing.Point DetachTodo()
    {
        UpdateLayout();
        var location = new Drawing.Point(_session.Board.X ?? 0, _session.Board.Y ?? 0);
        if (IsVisible && HasDockedTodo)
        {
            Point physical = DockHost.PointToScreen(new Point(0, 0));
            location = new Drawing.Point((int)Math.Round(physical.X), (int)Math.Round(physical.Y));
        }

        SaveSize();
        _dockedTodo = null;
        DockHost.Child = null;
        DockHost.Visibility = Visibility.Collapsed;
        TodoPanelRow.MinHeight = 0;
        TodoPanelRow.Height = GridLength.Auto;
        CalendarPanelRow.MinHeight = 0;
        CalendarPanelRow.Height = new GridLength(1, GridUnitType.Star);
        MinHeight = CalendarMinimumHeight;
        if (_largeView)
        {
            FitOnScreen();
        }
        else
        {
            SetManualSize(Width, _placement.Height ?? 450);
        }

        return location;
    }

    private double PreferredDimension(double? dimension, double minimum, double maximum, double fallback) =>
        dimension is double value && double.IsFinite(value)
            ? Math.Clamp(value, minimum, Math.Max(minimum, maximum))
            : fallback;

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

    private void UpdateContentLayout()
    {
        bool manual = SizeToContent == SizeToContent.Manual;
        bool folded = IsMemoFolded;
        double? share = manual && !folded ? _pendingMemoShare ?? SavedMemoShare : null;
        if (share is double split && split is >= SingleLineMemoShare and < 1)
        {
            DaysRow.MinHeight = MinimumDaysHeight;
            DaysRow.Height = new GridLength(1 - split, GridUnitType.Star);
            CalendarMemoRow.Height = split == SingleLineMemoShare
                ? new GridLength(MinimumMemoHeight) : new GridLength(split, GridUnitType.Star);
            CalendarMemoRow.MinHeight = MinimumMemoHeight;
            return;
        }

        DaysRow.MinHeight = manual ? _weekly ? MinimumDaysHeight : VisibleWeeks * 24 : 0;
        DaysRow.Height = manual ? new GridLength(_largeView ? 3 : 4, GridUnitType.Star)
            : new GridLength(_weekly ? MinimumDaysHeight : 270);
        bool expandMemo = manual && !_weekly;
        double memoHeight = folded ? 0 : MinimumMemoHeight;
        CalendarMemoRow.Height = expandMemo
            ? new GridLength(_largeView ? 2 : 1.2, GridUnitType.Star) : new GridLength(memoHeight);
        CalendarMemoRow.MinHeight = expandMemo ? Math.Max(_largeView ? 90 : 36, memoHeight) : memoHeight;
    }

    private bool PreviewMemoSplit(double daysHeight)
    {
        double available = AvailableSectionHeight;
        double maximumDaysHeight = available - MinimumMemoHeight;
        if (IsMemoFolded || maximumDaysHeight <= MinimumDaysHeight)
        {
            return false;
        }

        double? pending = daysHeight >= maximumDaysHeight - 0.5
            ? SingleLineMemoShare : WidgetSectionResizer.GetBottomShare(available, daysHeight, DaysRow.ActualHeight,
                MinimumDaysHeight, MinimumMemoHeight);
        if (pending is not double share || share == (_pendingMemoShare ?? SavedMemoShare))
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
        if (!canceled)
        {
            SaveMemoSplit(share);
        }
        else if (_memoDragWasAuto)
        {
            RestoreAutoHeight(Width);
        }
        else
        {
            UpdateContentLayout();
        }
    }

    private void SaveMemoSplit(double share)
    {
        if (_largeView)
        {
            _placement.LargeMemoShare = share;
            _session.QueueSave();
        }
        else
        {
            if (_weekly)
            {
                _placement.WeeklyMemoShare = share;
            }
            else
            {
                _placement.MonthlyMemoShare = share;
            }

            SaveSize();
        }
    }

    private void RefreshMemoLayout()
    {
        bool folded = IsMemoFolded;
        MemoLabel.Visibility = _weekly ? Visibility.Collapsed : Visibility.Visible;
        MemoToggle.Visibility = _weekly ? Visibility.Visible : Visibility.Collapsed;
        MemoToggle.Content = folded ? "▸ 메모" : "▾ 메모";
        MemoToggle.ToolTip = folded ? "메모 펼치기" : "메모 접기";
        AutomationProperties.SetName(MemoToggle, (string)MemoToggle.ToolTip);
        MemoHeader.Margin = new Thickness(0, 2, 0, folded ? 0 : 2);
        MemoDivider.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        MemoBody.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        UpdateContentLayout();
        UpdatePreviewLayout();

        if (HasDockedTodo)
        {
            CalendarPanelRow.MinHeight = CalendarMinimumHeight;
        }

        MinHeight = CalendarMinimumHeight + (HasDockedTodo ? TodoPanelRow.MinHeight : 0);
    }

    private void ToggleMemo(object sender, RoutedEventArgs e)
    {
        if (!_weekly)
        {
            return;
        }

        UpdateLayout();
        double panelHeight = Card.ActualHeight > 0 ? Card.ActualHeight : _placement.Height ?? StandaloneMinHeight;
        double desired;
        if (IsMemoFolded)
        {
            double preferred = _placement.WeeklyExpandedHeight ?? panelHeight + 34;
            desired = Math.Clamp(Math.Max(panelHeight, preferred), StandaloneMinHeight, WeeklyHeightThreshold - 1);
            _placement.WeeklyMemoCollapsed = false;
        }
        else
        {
            _placement.WeeklyExpandedHeight = panelHeight;
            desired = Math.Max(FoldedMinHeight, panelHeight - CalendarMemoRow.ActualHeight - 4);
            _placement.WeeklyMemoCollapsed = true;
        }

        RefreshMemoLayout();
        if (HasDockedTodo)
        {
            double todoHeight = !_compactDock && DockHost.ActualHeight > 0 ? DockHost.ActualHeight : ExpandedTodoHeight;
            desired += ConfigureDockRows(desired, todoHeight);
        }

        SetManualSize(Width, desired);
        FitOnScreen();
        UpdateLayout();
        SaveSize();
    }

    private void RestoreAutoHeight(double width)
    {
        Width = width;
        Height = double.NaN;
        SizeToContent = SizeToContent.Height;
        UpdateViewForHeight(WeeklyHeightThreshold);
        UpdateContentLayout();
    }

    private void SaveResizedSize()
    {
        if (HasDockedTodo && !_compactDock && !_largeView)
        {
            _session.Board.SizeManuallySet = true;
        }

        SaveSize();
    }

    private void SaveSize()
    {
        if (_largeView)
        {
            return;
        }

        _placement.Width = Width;
        if (HasDockedTodo)
        {
            _placement.Height = Card.ActualHeight > 0 ? Card.ActualHeight : _placement.Height ?? 450;
            _session.Board.Width = Width;
            if (!_compactDock && DockHost.ActualHeight > 0)
            {
                _session.Board.Height = DockHost.ActualHeight;
            }
        }
        else if (double.IsFinite(Height))
        {
            _placement.Height = Height;
        }
        else if (Card.ActualHeight > 0)
        {
            _placement.Height = Card.ActualHeight;
        }

        if (IsVisible)
        {
            RecordPosition();
        }
        else
        {
            _session.QueueSave();
        }
    }

    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose && !e.Cancel)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(HideCalendar);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _session.PropertyChanged -= SessionChanged;
        _session.CalendarNoteChanged -= CalendarNoteChanged;
        _session.DragEnded -= ClearDateDropPreview;
        base.OnClosed(e);
    }

    private ContextMenu BuildWindowMenu()
    {
        var menu = new ContextMenu();
        var large = new MenuItem { Header = "크게 보기", IsCheckable = true };
        var normal = new MenuItem { Header = "기본 모드", IsCheckable = true };
        large.Click += (_, _) => SetViewMode(true);
        normal.Click += (_, _) => SetViewMode(false);
        menu.Items.Add(large);
        menu.Items.Add(normal);
        menu.Items.Add(new Separator());
        var pin = new MenuItem { Header = "항상 위", IsCheckable = true };
        pin.Click += (_, _) => _app.SetWidgetTopmost(this, pin.IsChecked);
        var hide = new MenuItem { Header = "달력 숨기기" };
        hide.Click += (_, _) => HideCalendar();
        menu.Items.Add(pin);
        menu.Items.Add(Card.CreateTransparencyMenu(SetBackgroundTransparency));
        menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            large.IsChecked = _largeView;
            normal.IsChecked = !_largeView;
            pin.IsChecked = _placement.AlwaysOnTop;
            hide.Header = HasDockedTodo ? "달력·할 일 숨기기" : "달력 숨기기";
        };
        return menu;
    }

    internal bool SetBackgroundTransparency(int? value)
    {
        _placement.BackgroundTransparency = value;
        Card.Transparency = value;
        _dockedTodo?.ApplyBackgroundTransparency(value);
        _session.QueueSave();
        return true;
    }

    internal void HideCalendar()
    {
        _placement.IsVisible = false;
        _session.QueueSave();
        if (_session.Flush())
        {
            Hide();
        }
        else
        {
            _placement.IsVisible = true;
        }
    }

    private void DragHeading(object sender, MouseButtonEventArgs e)
    {
        if (_largeView || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        Drawing.Point before = ScreenBounds.Location;
        DragMove();
        if (ScreenBounds.Location == before)
        {
            return;
        }

        _app.SnapWidget(this);
        RecordPosition();
    }

    private void RecordPosition()
    {
        Drawing.Point position = ScreenBounds.Location;
        _placement.X = position.X;
        _placement.Y = position.Y;
        _session.QueueSave();
    }

    private void PreviousPeriod(object sender, RoutedEventArgs e) => MovePage(-1);
    private void NextPeriod(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int direction)
    {
        if (_weekly)
        {
            ChangePage(DateAt(WeekStart + direction * 7));
            return;
        }

        DateOnly month = FirstOfMonth(_page);
        if (direction < 0 ? month > DateOnly.MinValue : month < FirstOfMonth(DateOnly.MaxValue))
        {
            ChangePage(month.AddMonths(direction));
        }
    }

    private void JumpToday(object sender, RoutedEventArgs e)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (_session.Date == today)
        {
            ChangePage(today);
        }
        else
        {
            _session.Select(today);
        }
    }

    private void SelectDate(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarCell { IsDate: true } cell })
        {
            _session.Select(cell.Date);
        }
    }

    private void DragOverDate(object sender, DragEventArgs e)
    {
        bool allowed = sender is Button { DataContext: CalendarCell { IsDate: true } cell } &&
            _session.ReadDrag(e.Data) is { } drag && _session.CanMoveToDate(drag, cell.Date);
        SetDateDropPreview(allowed ? (Button)sender : null);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropOnDate(object sender, DragEventArgs e)
    {
        SetDateDropPreview(null);
        bool moved = sender is FrameworkElement { DataContext: CalendarCell { IsDate: true } cell } &&
            _session.ReadDrag(e.Data) is { } drag && _session.MoveToDate(drag, cell.Date);
        if (moved)
        {
            RenderPage();
        }
        e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void LeaveDate(object sender, DragEventArgs e)
    {
        if (sender == _dropDateButton)
        {
            SetDateDropPreview(null);
        }
    }

    private void ClearDateDropPreview(object? sender, EventArgs e) => SetDateDropPreview(null);

    private void SetDateDropPreview(Button? button)
    {
        if (_dropDateButton == button)
        {
            return;
        }

        _dropDateButton?.ClearValue(FrameworkElement.TagProperty);
        _dropDateButton = button;
        if (button is not null)
        {
            button.Tag = "DropTarget";
        }
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoSession.Date))
        {
            ChangePage(_session.Date);
        }
        else if (e.PropertyName == nameof(TodoSession.HasTodoContent))
        {
            UpdateDockedTodoLayout();
            RefreshSelectedCell();
        }
    }

    private void ChangePage(DateOnly date)
    {
        int previousWeeks = VisibleWeeks;
        _page = date;
        if (VisibleWeeks != previousWeeks)
        {
            UpdateContentLayout();
        }
        RenderPage();
    }

    private void UpdateViewForHeight(double height)
    {
        bool weekly = !_largeView && height < WeeklyHeightThreshold;
        if (height <= 0 || weekly == _weekly)
        {
            return;
        }

        if (weekly)
        {
            _page = FirstOfMonth(_page) == FirstOfMonth(_session.Date)
                ? _session.Date : FirstOfMonth(_page);
        }

        _weekly = weekly;
        RefreshMemoLayout();
        RenderPage();
    }

    private int WeekStart => _page.DayNumber - (int)_page.DayOfWeek;

    private void UpdatePreviewLayout()
    {
        double previewFontSize = (double)Resources["CalendarPreviewFontSize"];
        double dateFontSize = (double)Resources["CalendarDayFontSize"];
        double lineHeight = Math.Ceiling(previewFontSize * FontFamily.LineSpacing);
        double dateHeight = Math.Ceiling(dateFontSize * FontFamily.LineSpacing);
        double previewSpace = DayGrid.ActualHeight / VisibleWeeks - dateHeight - 6;
        int lines = Math.Clamp((int)(previewSpace / lineHeight), 1, MaxPreviewLines);
        double cellWidth = DayGrid.ActualWidth / 7 - 6;
        bool expanded = !IsMemoFolded && previewSpace >= lineHeight
            && (lines > 1 || cellWidth >= 6 * previewFontSize);
        Visibility expandedVisibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        double previewHeight = lines * lineHeight;
        if (Equals(Resources["ExpandedCalendarPreviewVisibility"], expandedVisibility)
            && Equals(Resources["CalendarPreviewHeight"], previewHeight))
        {
            return;
        }

        Resources["CompactCalendarPreviewVisibility"] = expanded ? Visibility.Collapsed : Visibility.Visible;
        Resources["ExpandedCalendarPreviewVisibility"] = expandedVisibility;
        Resources["CalendarPreviewHeight"] = previewHeight;
    }

    private void RenderPage()
    {
        SetDateDropPreview(null);
        DateOnly month = FirstOfMonth(_page);
        int first = _weekly ? WeekStart : month.DayNumber - (int)month.DayOfWeek;
        if (_weekly)
        {
            DateOnly start = DateAt(first);
            DateOnly end = DateAt(first + 6);
            MonthTitle.Text = $"{start.Month}.{start.Day}–{end.Month}.{end.Day}";
        }
        else
        {
            MonthTitle.Text = $"{month.Year}년 {month.Month}월";
        }

        PreviousButton.IsEnabled = _weekly ? first > 0 : month != DateOnly.MinValue;
        NextButton.IsEnabled = _weekly ? first + 6 < DateOnly.MaxValue.DayNumber : month != FirstOfMonth(DateOnly.MaxValue);
        PreviousButton.ToolTip = _weekly ? "이전 주" : "이전 달";
        NextButton.ToolTip = _weekly ? "다음 주" : "다음 달";
        AutomationProperties.SetName(PreviousButton, (string)PreviousButton.ToolTip);
        AutomationProperties.SetName(NextButton, (string)NextButton.ToolTip);

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        _cells.Clear();
        int count = VisibleWeeks * 7;
        for (int index = 0; index < count; index++)
        {
            int number = first + index;
            bool valid = number >= 0 && number <= DateOnly.MaxValue.DayNumber;
            DateOnly date = valid ? DateOnly.FromDayNumber(number) : default;
            bool isDate = valid && (_weekly || date.Month == month.Month);
            _cells.Add(new CalendarCell(date, isDate, date == _session.Date, date == today,
                isDate ? _session.Preview(date) : "", isDate ? GetExpandedPreview(date) : "",
                isDate && _session.HasTodoItems(date)));
        }

        UpdatePreviewLayout();
    }

    private static DateOnly DateAt(int dayNumber) =>
        DateOnly.FromDayNumber(Math.Clamp(dayNumber, 0, DateOnly.MaxValue.DayNumber));

    private void CalendarNoteChanged(object? sender, EventArgs e) => RefreshSelectedCell();

    private void RefreshSelectedCell()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is { IsDate: true } cell && cell.Date == _session.Date)
            {
                string preview = _session.Preview(cell.Date);
                string expandedPreview = GetExpandedPreview(cell.Date);
                bool hasTodoItems = _session.HasTodoItems(cell.Date);
                if (cell.Preview != preview || cell.ExpandedPreview != expandedPreview || cell.HasTodoItems != hasTodoItems)
                {
                    _cells[i] = cell with { Preview = preview, ExpandedPreview = expandedPreview, HasTodoItems = hasTodoItems };
                }
                return;
            }
        }
    }

    private string GetExpandedPreview(DateOnly date) => _session.GetCalendarNote(date).Trim().ReplaceLineEndings(" ");

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private sealed record CalendarCell(DateOnly Date, bool IsDate, bool IsSelected, bool IsToday, string Preview,
        string ExpandedPreview, bool HasTodoItems)
    {
        public string Number => IsDate ? Date.Day.ToString() : "";
        public string Label => IsDate ? $"{Date.Month}월 {Date.Day}일" : "";
        public string TodoStatus => HasTodoItems ? "할 일 있음" : "";
    }
}
