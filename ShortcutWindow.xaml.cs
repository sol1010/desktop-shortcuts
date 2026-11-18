using System;
using System.ComponentModel;
using System.Diagnostics;
using Drawing = System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;

namespace DesktopShortcuts;

public partial class ShortcutWindow : Window
{
    private readonly App _app;
    private ShortcutEntry _entry;
    private bool _pressed;
    private bool _dragging;
    private int _clickCount;
    private bool _previousClickCompleted;
    private Drawing.Point _pointerStart;
    private Drawing.Point _windowStart;
    private nint Handle => new WindowInteropHelper(this).Handle;

    internal ShortcutSize CurrentSize => _entry.Size;
    internal Drawing.Rectangle ScreenBounds => NativeWindow.Bounds(Handle);

    internal ShortcutWindow(App app, ShortcutEntry entry)
    {
        _app = app;
        _entry = entry;
        InitializeComponent();
        Surface.ContextMenu = CreateMenu();
        Apply(entry, refreshIcon: true);
    }

    internal void ShowAt(Drawing.Point defaultPosition)
    {
        Show();
        var savedPosition = new Drawing.Point(_entry.X ?? defaultPosition.X, _entry.Y ?? defaultPosition.Y);
        Place(savedPosition);
    }

    internal void FitOnScreen()
    {
        Place(NativeWindow.Bounds(Handle).Location);
    }

    internal void Apply(ShortcutEntry entry)
    {
        bool refreshIcon = entry.Kind != _entry.Kind || entry.TargetPath != _entry.TargetPath;
        bool layoutChanged = entry.Size != _entry.Size;
        Apply(entry, refreshIcon);
        if (layoutChanged)
        {
            UpdateLayout();
            FitOnScreen();
        }
    }

    private void Apply(ShortcutEntry entry, bool refreshIcon)
    {
        _entry = entry;
        int side = entry.Size.Width;
        Width = Height = side;

        // Scale the whole tile at the smallest sizes so its contents keep their proportions.
        int layoutSide = Math.Max(side, 32);
        Tile.Width = Tile.Height = layoutSide;
        double paddingScale = Math.Min(1, layoutSide / 48.0);
        Tile.Padding = new Thickness(5 * paddingScale, 4 * paddingScale, 5 * paddingScale, 7 * paddingScale);
        IconFace.MaxWidth = IconFace.MaxHeight = Math.Min(layoutSide * 0.43, 34);
        LabelText.Text = entry.Name;
        Surface.ToolTip = entry.Kind == TargetKind.Calendar ? entry.Name : $"{entry.Name}\n{entry.TargetPath}";
        Title = entry.Name;
        Topmost = entry.AlwaysOnTop;
        AutomationProperties.SetName(Surface, entry.Name);

        if (refreshIcon)
        {
            ImageSource? programIcon = entry.Kind == TargetKind.Program ? ReadProgramIcon(entry.TargetPath) : null;
            ProgramImage.Source = programIcon;
            FolderGlyph.Visibility = entry.Kind == TargetKind.Folder ? Visibility.Visible : Visibility.Collapsed;
            CalendarGlyph.Visibility = entry.Kind == TargetKind.Calendar ? Visibility.Visible : Visibility.Collapsed;
            ProgramImage.Visibility = programIcon is not null ? Visibility.Visible : Visibility.Collapsed;
            ProgramGlyph.Visibility = entry.Kind == TargetKind.Program && programIcon is null
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        MenuItem open = Item("열기", OpenTarget);
        MenuItem newTab = OpenModeItem("새 탭에서 열기", FolderOpenMode.NewTab);
        MenuItem newWindow = OpenModeItem("새 창에서 열기", FolderOpenMode.NewWindow);
        var topmost = Item("항상 위", () => _app.UpdateShortcut(_entry with { AlwaysOnTop = !_entry.AlwaysOnTop }));
        topmost.IsCheckable = true;

        var size = new MenuItem { Header = "크기" };
        MenuItem small = SizeItem("소형", ShortcutSize.Small);
        MenuItem medium = SizeItem("중형", ShortcutSize.Medium);
        MenuItem large = SizeItem("대형", ShortcutSize.Large);
        size.Items.Add(small);
        size.Items.Add(medium);
        size.Items.Add(large);
        size.Items.Add(new Separator());
        size.Items.Add(Item("직접 입력…", () => _app.ShowEditor(_entry, this, sizeOnly: true)));

        var activation = new MenuItem { Header = "열기 동작" };
        MenuItem doubleClick = ActivationItem("더블클릭", ShortcutActivation.DoubleClick);
        MenuItem singleClick = ActivationItem("한 번 클릭", ShortcutActivation.SingleClick);
        activation.Items.Add(doubleClick);
        activation.Items.Add(singleClick);

        menu.Items.Add(open);
        menu.Items.Add(newTab);
        menu.Items.Add(newWindow);
        menu.Items.Add(new Separator());
        menu.Items.Add(topmost);
        menu.Items.Add(size);
        menu.Items.Add(activation);
        menu.Items.Add(Item("이름 및 연결 변경…", () => _app.ShowEditor(_entry, this)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("새 버튼 추가…", () => _app.ShowEditor(null, this)));
        menu.Items.Add(Item("투두 목록 열기", () => _app.ShowTodo()));
        MenuItem showCalendar = Item("달력 열기", () => _app.ShowCalendar());
        menu.Items.Add(showCalendar);
        menu.Items.Add(Item("버튼 제거", () => _app.RemoveShortcut(_entry.Id, this)));

        menu.Opened += (_, _) =>
        {
            open.Header = _entry.Kind == TargetKind.Calendar ? "달력 열기" : "열기";
            showCalendar.Visibility = _entry.Kind == TargetKind.Calendar ? Visibility.Collapsed : Visibility.Visible;
            newTab.Visibility = _entry.Kind == TargetKind.Folder && ExplorerFolders.SupportsTabs
                ? Visibility.Visible : Visibility.Collapsed;
            newWindow.Visibility = _entry.Kind == TargetKind.Folder ? Visibility.Visible : Visibility.Collapsed;
            topmost.IsChecked = _entry.AlwaysOnTop;
            small.IsChecked = _entry.Size == ShortcutSize.Small;
            medium.IsChecked = _entry.Size == ShortcutSize.Medium;
            large.IsChecked = _entry.Size == ShortcutSize.Large;
            size.Header = $"크기 ({_entry.Size})";
            doubleClick.IsChecked = _entry.Activation == ShortcutActivation.DoubleClick;
            singleClick.IsChecked = _entry.Activation == ShortcutActivation.SingleClick;
        };

        return menu;
    }

    private MenuItem OpenModeItem(string label, FolderOpenMode mode)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) => await OpenTargetAsync(mode);
        return item;
    }

    private MenuItem SizeItem(string label, ShortcutSize size)
    {
        var item = Item(label, () => _app.UpdateShortcut(_entry with { Size = size }));
        item.IsCheckable = true;
        return item;
    }

    private MenuItem ActivationItem(string label, ShortcutActivation activation)
    {
        var item = Item(label, () => _app.UpdateShortcut(_entry with { Activation = activation }));
        item.IsCheckable = true;
        return item;
    }

    private static MenuItem Item(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    private void Press(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _clickCount = e.ClickCount;
        _pointerStart = NativeWindow.CursorPosition;
        _windowStart = NativeWindow.Bounds(Handle).Location;
        _dragging = false;
        _pressed = Surface.CaptureMouse();
        if (!_pressed)
        {
            _previousClickCompleted = false;
        }
    }

    private void Drag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Drawing.Point cursor = NativeWindow.CursorPosition;
        int dx = cursor.X - _pointerStart.X;
        int dy = cursor.Y - _pointerStart.Y;
        if (!_dragging)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _dragging = Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX ||
                        Math.Abs(dy) >= SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
        }

        if (_dragging)
        {
            NativeWindow.Move(Handle, DragPosition(dx, dy));
        }
    }

    private Drawing.Point DragPosition(int dx, int dy)
    {
        var position = new Drawing.Point(_windowStart.X + dx, _windowStart.Y + dy);
        var bounds = new Drawing.Rectangle(position, ScreenBounds.Size);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return ShortcutSnap.FindPosition(bounds, _app.GetNeighborBounds(this),
            NativeWindow.WorkingArea(bounds), dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void Release(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        e.Handled = true;
        bool moved = _dragging;
        bool overButton = Surface.InputHitTest(e.GetPosition(Surface)) is not null;
        bool launch = _entry.Activation == ShortcutActivation.SingleClick
            ? _clickCount == 1
            : _clickCount == 2 && _previousClickCompleted;
        _previousClickCompleted = !moved && overButton && _clickCount == 1;
        _pressed = false;
        _dragging = false;
        Surface.ReleaseMouseCapture();

        if (moved)
        {
            Drawing.Point cursor = NativeWindow.CursorPosition;
            Place(DragPosition(cursor.X - _pointerStart.X, cursor.Y - _pointerStart.Y));
            Drawing.Point position = NativeWindow.Bounds(Handle).Location;
            if (!_app.UpdateShortcut(_entry with { X = position.X, Y = position.Y }))
            {
                Place(_windowStart);
            }
        }
        else if (overButton && launch)
        {
            OpenTarget();
        }
    }

    private void CancelPress(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pressed)
        {
            _previousClickCompleted = false;
            if (_dragging)
            {
                Place(_windowStart);
            }
        }

        _pressed = false;
        _dragging = false;
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.IsRepeat && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            OpenTarget();
        }
    }

    private void Place(Drawing.Point desired)
    {
        NativeWindow.Move(Handle, NativeWindow.KeepOnScreen(desired, NativeWindow.Bounds(Handle).Size));
    }

    private void OpenTarget() => _ = OpenTargetAsync(FolderOpenMode.Default);

    private async Task OpenTargetAsync(FolderOpenMode mode)
    {
        if (_entry.Kind == TargetKind.Calendar)
        {
            _app.ShowCalendar();
            return;
        }

        string path = _entry.TargetPath;
        bool exists = _entry.Kind == TargetKind.Folder ? Directory.Exists(path) : File.Exists(path);
        if (!exists)
        {
            MessageBox.Show(this, $"연결 대상을 찾을 수 없습니다.\n\n{path}\n\n우클릭 메뉴에서 연결을 변경할 수 있습니다.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (_entry.Kind == TargetKind.Folder && mode == FolderOpenMode.NewTab)
            {
                await ExplorerFolders.OpenTabAsync(path);
                return;
            }

            ProcessStartInfo startInfo = ExplorerFolders.StartInfo(path, mode);
            if (_entry.Kind == TargetKind.Program)
            {
                startInfo.WorkingDirectory = Path.GetDirectoryName(path);
            }

            using Process? process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException
            or COMException or RuntimeBinderException or ElementNotAvailableException or ElementNotEnabledException)
        {
            MessageBox.Show(this, $"연결 대상을 열 수 없습니다.\n\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ImageSource? ReadProgramIcon(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using Drawing.Icon? icon = Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }
}
