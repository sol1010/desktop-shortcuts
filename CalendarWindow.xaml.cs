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

    private nint Handle => new WindowInteropHelper(this).Handle;
    private bool IsMemoFolded => _weekly && _placement.WeeklyMemoCollapsed;
    private double CalendarMinimumHeight => IsMemoFolded ? FoldedMinHeight : StandaloneMinHeight;

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

        _resizer = new WidgetResizer(this, ResizeLayer, SetManualSize, RestoreAutoHeight, SaveSize);
        Card.ContextMenu = BuildWindowMenu();
        DayGrid.ItemsSource = _cells;
        session.PropertyChanged += SessionChanged;
        session.NoteChanged += RefreshSelectedPreview;
        Card.SizeChanged += (_, args) => UpdateViewForHeight(args.NewSize.Height);
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
    internal bool HasDockedTodo => DockHost.Child is not null;

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
            CalendarPanelRow.Height = new GridLength(3, GridUnitType.Star);
            TodoPanelRow.Height = new GridLength(1, GridUnitType.Star);
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
            double todoPane = Math.Max(TodoPanelRow.MinHeight, (_session.Board.Height ?? 180) + WidgetDockGeometry.Gap);
            CalendarPanelRow.Height = new GridLength(height, GridUnitType.Star);
            TodoPanelRow.Height = new GridLength(todoPane, GridUnitType.Star);
            height += todoPane;
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
        double gap = WidgetDockGeometry.Gap;

        DockHost.Margin = new Thickness(0, gap, 0, 0);
        DockHost.Child = todo.MoveSurfaceToCalendar(width, todoHeight);
        DockHost.Visibility = Visibility.Visible;
        CalendarPanelRow.MinHeight = CalendarMinimumHeight;
        CalendarPanelRow.Height = new GridLength(calendarHeight, GridUnitType.Star);
        TodoPanelRow.MinHeight = todo.MinHeight + gap;
        TodoPanelRow.Height = new GridLength(todoHeight + gap, GridUnitType.Star);
        MinHeight = CalendarMinimumHeight + todo.MinHeight + gap;
        if (_largeView)
        {
            _session.Board.Width ??= _placement.Width ?? 354;
            _session.Board.Height ??= todoHeight;
            FitOnScreen();
            return;
        }

        SetManualSize(width, calendarHeight + todoHeight + gap);

        if (IsVisible)
        {
            FitOnScreen();
            UpdateLayout();
            SaveSize();
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
            ConfigureManualRows();
        }

        Width = width;
        Height = height;
    }

    private void ConfigureManualRows()
    {
        DaysRow.Height = _weekly ? new GridLength(IsMemoFolded ? 32 : 34) : new GridLength(_largeView ? 3 : 4, GridUnitType.Star);
        DaysRow.MinHeight = _weekly ? 24 : 144;
        CalendarMemoRow.Height = IsMemoFolded ? new GridLength(0) : new GridLength(_largeView ? 2 : 1, GridUnitType.Star);
        CalendarMemoRow.MinHeight = IsMemoFolded ? 0 : _largeView ? 90 : _weekly ? 26 : 36;
    }

    private void RefreshMemoLayout()
    {
        bool folded = IsMemoFolded;
        MemoLabel.Visibility = _weekly ? Visibility.Collapsed : Visibility.Visible;
        MemoToggle.Visibility = _weekly ? Visibility.Visible : Visibility.Collapsed;
        MemoToggle.Content = folded ? "▸ 메모" : "▾ 메모";
        MemoToggle.ToolTip = folded ? "메모 펼치기" : "메모 접기";
        AutomationProperties.SetName(MemoToggle, (string)MemoToggle.ToolTip);
        MemoHeader.Margin = folded ? new Thickness(0, 2, 0, 0) : new Thickness(0, 4, 0, 4);
        MemoBody.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;

        if (HasDockedTodo)
        {
            CalendarPanelRow.MinHeight = CalendarMinimumHeight;
        }

        MinHeight = CalendarMinimumHeight + (HasDockedTodo ? TodoPanelRow.MinHeight : 0);
        if (SizeToContent == SizeToContent.Manual)
        {
            ConfigureManualRows();
        }
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
            double todoHeight = DockHost.ActualHeight > 0 ? DockHost.ActualHeight : _session.Board.Height ?? 180;
            CalendarPanelRow.Height = new GridLength(desired, GridUnitType.Star);
            TodoPanelRow.Height = new GridLength(todoHeight + WidgetDockGeometry.Gap, GridUnitType.Star);
            desired += todoHeight + WidgetDockGeometry.Gap;
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
        DaysRow.Height = new GridLength(270);
        DaysRow.MinHeight = 0;
        CalendarMemoRow.Height = new GridLength(80);
        CalendarMemoRow.MinHeight = 0;
        SizeToContent = SizeToContent.Height;
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
            _session.Board.Height = DockHost.ActualHeight > 0 ? DockHost.ActualHeight : _session.Board.Height ?? 180;
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
        _session.NoteChanged -= RefreshSelectedPreview;
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
        menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            large.IsChecked = _largeView;
            normal.IsChecked = !_largeView;
            pin.IsChecked = _placement.AlwaysOnTop;
            hide.Header = HasDockedTodo ? "달력·투두 숨기기" : "달력 숨기기";
        };
        return menu;
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

    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoSession.Date))
        {
            ChangePage(_session.Date);
        }
    }

    private void ChangePage(DateOnly date)
    {
        _page = date;
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

    private void RenderPage()
    {
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
        int count = _weekly ? 7 : 42;
        for (int index = 0; index < count; index++)
        {
            int number = first + index;
            bool valid = number >= 0 && number <= DateOnly.MaxValue.DayNumber;
            DateOnly date = valid ? DateOnly.FromDayNumber(number) : default;
            bool isDate = valid && (_weekly || date.Month == month.Month);
            _cells.Add(new CalendarCell(date, isDate, date == _session.Date, date == today,
                isDate ? _session.Preview(date) : ""));
        }
    }

    private static DateOnly DateAt(int dayNumber) =>
        DateOnly.FromDayNumber(Math.Clamp(dayNumber, 0, DateOnly.MaxValue.DayNumber));

    private void RefreshSelectedPreview(object? sender, EventArgs e)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is { IsDate: true } cell && cell.Date == _session.Date)
            {
                _cells[i] = cell with { Preview = _session.Preview(cell.Date) };
                return;
            }
        }
    }

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private sealed record CalendarCell(DateOnly Date, bool IsDate, bool IsSelected, bool IsToday, string Preview)
    {
        public string Number => IsDate ? Date.Day.ToString() : "";
        public string Label => IsDate ? $"{Date.Month}월 {Date.Day}일" : "";
    }
}
