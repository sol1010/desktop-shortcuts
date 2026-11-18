using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Tray = System.Windows.Forms;

namespace DesktopShortcuts;

public partial class App : Application
{
    private const string AppName = "바탕화면 바로가기";
    private readonly Dictionary<Guid, ShortcutWindow> _windows = [];
    private List<ShortcutEntry> _entries = [];
    private Mutex? _instance;
    private Tray.NotifyIcon? _trayIcon;
    private ShortcutEditor? _editor;
    private ShortcutAppearance? _appearance;
    private TodoWindow? _todo;
    private CalendarWindow? _calendar;
    private TodoSession? _todoSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(false, @"Local\DesktopShortcuts", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("이미 실행 중입니다. 알림 영역에서 바로가기를 추가할 수 있습니다.", AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _entries = ShortcutStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show($"바로가기 설정을 불러올 수 없습니다.\n\n{ex.Message}", AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _appearance = new ShortcutAppearance(this, Path.Combine(AppContext.BaseDirectory, "appearance.json"));
        CreateTrayIcon();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        foreach (ShortcutEntry entry in _entries)
        {
            ShowShortcut(entry);
        }

        if (EnsureTodoSession())
        {
            ShowTodo(restoreVisibility: true);
            ShowCalendar(restoreVisibility: true);
        }

        if (_entries.Count == 0)
        {
            ShowEditor();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }

        _todoSession?.Dispose();
        _appearance?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && !PrepareWidgetsToClose())
        {
            e.Cancel = true;
        }
    }

    private bool EnsureTodoSession()
    {
        if (_todoSession is not null)
        {
            return true;
        }

        try
        {
            TodoStore store = TodoStore.Default;
            _todoSession = new TodoSession(store.Load(), store);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _trayIcon?.ShowBalloonTip(6000, "투두와 달력을 불러올 수 없습니다.", ex.Message, Tray.ToolTipIcon.Error);
            return false;
        }
    }

    internal void ShowTodo(bool restoreVisibility = false)
    {
        if (EnsureTodoSession() && (!restoreVisibility || _todoSession!.Board.IsVisible))
        {
            if (_todoSession!.Board.DockedToCalendar)
            {
                ShowCalendar(restoreVisibility);
                return;
            }

            _todo ??= new TodoWindow(this, _todoSession);
            _todo.Reveal(activate: !restoreVisibility);
        }
    }

    internal void ShowCalendar(bool restoreVisibility = false)
    {
        if (EnsureTodoSession() && (!restoreVisibility || _todoSession!.Board.Calendar.IsVisible))
        {
            TodoSession session = _todoSession!;
            _calendar ??= new CalendarWindow(this, session);
            if (session.Board.DockedToCalendar)
            {
                _todo ??= new TodoWindow(this, session);
                _calendar.AttachTodo(_todo);
                session.Board.AlwaysOnTop = _todo.Topmost = _calendar.Topmost;
            }

            _calendar.Reveal(activate: !restoreVisibility);
        }
    }

    internal bool TryDockTodo()
    {
        if (_todoSession is null || _todo is not { IsVisible: true } || _calendar is not { IsVisible: true })
        {
            return false;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(_calendar);
        if (!WidgetDockGeometry.IsBelow(_todo.ScreenBounds, _calendar.ScreenBounds, dpi.DpiScaleY))
        {
            return false;
        }

        _todoSession.Board.DockedToCalendar = true;
        _calendar.AttachTodo(_todo);
        SetWidgetTopmost(_calendar, _calendar.Topmost);
        return true;
    }

    internal bool DetachTodoForDrag()
    {
        if (_todoSession is null || _todo is null || _calendar is not { HasDockedTodo: true })
        {
            return false;
        }

        System.Drawing.Point position = _calendar.DetachTodo();
        _todoSession.Board.DockedToCalendar = false;
        _todo.RestoreSurface(position);
        _todo.Reveal(activate: true);
        _todoSession.QueueSave();
        return true;
    }

    internal void HideDockedWidgets() => _calendar?.HideCalendar();

    internal void SetWidgetTopmost(Window source, bool pinned)
    {
        if (_todoSession is null)
        {
            return;
        }

        TodoBoard board = _todoSession.Board;
        if (_todo is not null && (source == _todo || board.DockedToCalendar))
        {
            board.AlwaysOnTop = _todo.Topmost = pinned;
        }

        if (_calendar is not null && (source == _calendar || board.DockedToCalendar))
        {
            board.Calendar.AlwaysOnTop = _calendar.Topmost = pinned;
        }

        _todoSession.QueueSave();
    }

    private bool PrepareWidgetsToClose()
    {
        if (_todoSession is not null && !_todoSession.Flush())
        {
            return false;
        }

        _todo?.AllowClose();
        _calendar?.AllowClose();
        return true;
    }

    private void RequestExit()
    {
        if (PrepareWidgetsToClose())
        {
            Shutdown();
        }
    }

    internal void ShowEditor(ShortcutEntry? entry = null, ShortcutWindow? owner = null, bool sizeOnly = false)
    {
        if (_editor is not null)
        {
            if (_editor.WindowState == WindowState.Minimized)
            {
                _editor.WindowState = WindowState.Normal;
            }

            _editor.Activate();
            return;
        }

        var usedSizes = _entries.Select(item => item.Size).Distinct()
            .OrderBy(size => size.Width).ToArray();
        var editor = new ShortcutEditor(entry, usedSizes, owner?.CurrentSize, sizeOnly)
        {
            Topmost = owner?.Topmost == true
        };
        editor.SaveRequested = result => SaveEditorResult(editor, entry, result, sizeOnly);
        editor.Closed += (_, _) => _editor = null;
        _editor = editor;
        editor.Show();
    }

    private bool SaveEditorResult(ShortcutEditor editor, ShortcutEntry? original, ShortcutEntry edited, bool sizeOnly)
    {
        if (original is null)
        {
            if (!TrySave([.. _entries, edited], editor))
            {
                return false;
            }

            ShowShortcut(edited);
            return true;
        }

        ShortcutEntry? current = _entries.FirstOrDefault(item => item.Id == edited.Id);
        if (current is null)
        {
            editor.ShowSaveError("이 바로가기 버튼은 이미 제거되었습니다.");
            return false;
        }

        return UpdateShortcut(ApplyEditorChanges(current, original, edited, sizeOnly), editor);
    }

    private static ShortcutEntry ApplyEditorChanges(
        ShortcutEntry current, ShortcutEntry original, ShortcutEntry edited, bool sizeOnly)
    {
        if (sizeOnly)
        {
            return current with { Size = edited.Size };
        }

        return current with
        {
            Name = edited.Name,
            TargetPath = edited.TargetPath,
            Kind = edited.Kind,
            Size = edited.Size != original.Size ? edited.Size : current.Size
        };
    }

    internal Rectangle[] GetNeighborBounds(Window moving, bool skipCalendar = false)
    {
        List<Rectangle> bounds = _windows.Values.Where(window => window != moving && window.IsVisible)
            .Select(window => window.ScreenBounds).ToList();
        if (_todo is { IsVisible: true } && moving != _todo)
        {
            bounds.Add(_todo.ScreenBounds);
        }

        if (!skipCalendar && _calendar is { IsVisible: true } && moving != _calendar)
        {
            bounds.Add(_calendar.ScreenBounds);
        }

        return bounds.ToArray();
    }

    internal void SnapWidget(Window widget, bool skipCalendar = false)
    {
        nint handle = new WindowInteropHelper(widget).Handle;
        Rectangle bounds = NativeWindow.Bounds(handle);
        DpiScale dpi = VisualTreeHelper.GetDpi(widget);
        System.Drawing.Point snapped = ShortcutSnap.FindPosition(bounds, GetNeighborBounds(widget, skipCalendar),
            NativeWindow.WorkingArea(bounds), dpi.DpiScaleX, dpi.DpiScaleY);
        NativeWindow.Move(handle, NativeWindow.KeepOnScreen(snapped, bounds.Size));
    }

    internal bool UpdateShortcut(ShortcutEntry entry, Window? errorOwner = null)
    {
        ShortcutWindow window = _windows[entry.Id];
        List<ShortcutEntry> updated = _entries.Select(item => item.Id == entry.Id ? entry : item).ToList();
        if (!TrySave(updated, errorOwner ?? window))
        {
            return false;
        }

        window.Apply(entry);
        return true;
    }

    internal void RemoveShortcut(Guid id, ShortcutWindow owner)
    {
        List<ShortcutEntry> updated = _entries.Where(entry => entry.Id != id).ToList();
        if (TrySave(updated, owner))
        {
            _windows.Remove(id);
            owner.Close();
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Tray.ContextMenuStrip();
        menu.Items.Add("새 버튼 추가…", null, (_, _) => ShowEditor());
        menu.Items.Add("투두 목록 열기", null, (_, _) => ShowTodo());
        menu.Items.Add("달력 열기", null, (_, _) => ShowCalendar());
        menu.Items.Add(new Tray.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => RequestExit());
        _trayIcon = new Tray.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Tray.MouseButtons.Left)
            {
                ShowEditor();
            }
        };
    }

    private void ShowShortcut(ShortcutEntry entry)
    {
        Rectangle area = Tray.Screen.FromPoint(NativeWindow.CursorPosition).WorkingArea;
        int offset = 24 + (_windows.Count % 8) * 32;
        var window = new ShortcutWindow(this, entry);
        _windows.Add(entry.Id, window);
        window.ShowAt(new System.Drawing.Point(area.Left + offset, area.Top + offset));
    }

    private bool TrySave(List<ShortcutEntry> entries, Window? owner)
    {
        try
        {
            ShortcutStore.Save(entries);
            _entries = entries;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string message = $"바로가기 설정을 저장할 수 없습니다.\n\n{ex.Message}";
            if (owner is ShortcutEditor editor)
            {
                editor.ShowSaveError(message);
            }
            else if (owner is null)
            {
                MessageBox.Show(message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(owner, message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (ShortcutWindow window in _windows.Values)
            {
                window.FitOnScreen();
            }

            _todo?.FitOnScreen();
            _calendar?.FitOnScreen();
        });
    }
}
