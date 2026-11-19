using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tray = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;

namespace DesktopShortcuts;

public partial class App : Application
{
    private const string AppName = "바탕화면 바로가기";
    private readonly Dictionary<Guid, ShortcutWindow> _windows = [];
    private readonly DispatcherTimer _shortcutOrderTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private List<ShortcutEntry> _entries = [];
    private Mutex? _instance;
    private Tray.NotifyIcon? _trayIcon;
    private Icon? _trayImage;
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
            if (!e.Args.Contains(WindowsStartup.LaunchArgument, StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show("이미 실행 중입니다. 알림 영역에서 바로가기를 추가할 수 있습니다.", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
        RestoreLegacyShortcutGroups();
        FitShortcutWindows();

        if (EnsureTodoSession())
        {
            ShowTodo(restoreVisibility: true);
            ShowCalendar(restoreVisibility: true);
        }

        if (_entries.Count == 0)
        {
            ShowEditor();
        }

        RestoreShortcutTopmostOrder();
        _shortcutOrderTimer.Tick += (_, _) => RestoreShortcutTopmostOrder();
        _shortcutOrderTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _shortcutOrderTimer.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }

        _trayImage?.Dispose();
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
            _trayIcon?.ShowBalloonTip(6000, "할 일 목록과 달력을 불러올 수 없습니다.", ex.Message, Tray.ToolTipIcon.Error);
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

        ShortcutEntry changes = ApplyEditorChanges(current, original, edited, sizeOnly);
        if (sizeOnly || edited.Size != original.Size)
        {
            return UpdateShortcutGroup(current, item => item.Id == current.Id ? changes : item with { Size = changes.Size }, editor);
        }

        return UpdateShortcut(changes, editor);
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
        return SaveShortcutChanges([entry], errorOwner ?? _windows[entry.Id]);
    }

    internal bool UpdateShortcutGroup(ShortcutEntry anchor, Func<ShortcutEntry, ShortcutEntry> change, Window? errorOwner = null)
    {
        ShortcutWindow source = _windows[anchor.Id];
        ShortcutWindow[] windows = GroupWindows(anchor);
        ShortcutEntry[] changes = windows.Select(window => change(window.Entry)).ToArray();
        Rectangle[]? positions = null;
        if (windows.Length > 1 && changes.Any(entry => _windows[entry.Id].Entry.Size != entry.Size))
        {
            int anchorIndex = Array.IndexOf(windows, source);
            DpiScale dpi = VisualTreeHelper.GetDpi(source);
            var newSize = new System.Drawing.Size(DevicePixels(changes[anchorIndex].Size.Width, dpi.DpiScaleX),
                DevicePixels(changes[anchorIndex].Size.Height, dpi.DpiScaleY));
            positions = ShortcutGroupLayout.Resize(windows.Select(window => window.ScreenBounds).ToArray(),
                anchorIndex, newSize, ShortcutSnap.PixelGap(dpi.DpiScaleX), ShortcutSnap.PixelGap(dpi.DpiScaleY));
            FitGroup(positions, NativeWindow.WorkingArea(source.ScreenBounds));
            for (int index = 0; index < changes.Length; index++)
            {
                changes[index] = AtPosition(changes[index], positions[index].Location);
            }
        }

        if (!SaveShortcutChanges(changes, errorOwner ?? source))
        {
            return false;
        }

        if (positions is not null)
        {
            MoveGroup(windows, positions);
        }
        return true;
    }

    internal ShortcutDrag BeginShortcutDrag(ShortcutWindow source)
    {
        ShortcutWindow[] windows = GroupWindows(source.Entry);
        return new ShortcutDrag(source, windows, windows.Select(window => window.ScreenBounds).ToArray(),
            Array.IndexOf(windows, source), VisualTreeHelper.GetDpi(source));
    }

    internal void PreviewShortcutDrag(ShortcutDrag drag, int dx, int dy) => PlaceShortcutDrag(drag, dx, dy);

    internal void PreviewShortcutReorder(ShortcutDrag drag, DrawingPoint cursor) =>
        drag.HighlightReorderTarget(FindReorderTarget(drag, cursor));

    internal void CompleteShortcutReorder(ShortcutDrag drag, DrawingPoint cursor)
    {
        int target = FindReorderTarget(drag, cursor);
        drag.HighlightReorderTarget(-1);
        if (target < 0)
        {
            return;
        }

        Rectangle[] positions = ShortcutGroupLayout.Reorder(drag.OriginalBounds, drag.AnchorIndex, target,
            ShortcutSnap.PixelGap(drag.InitialDpi.DpiScaleX), ShortcutSnap.PixelGap(drag.InitialDpi.DpiScaleY));
        FitGroup(positions, NativeWindow.WorkingArea(ShortcutGroupLayout.Bounds(drag.OriginalBounds)));
        var changes = drag.Windows.Select((window, index) => AtPosition(window.Entry, positions[index].Location));
        if (SaveShortcutChanges(changes, drag.Source))
        {
            MoveGroup(drag.Windows, positions);
        }
    }

    private static int FindReorderTarget(ShortcutDrag drag, DrawingPoint cursor)
    {
        for (int index = 0; index < drag.Windows.Length; index++)
        {
            if (index != drag.AnchorIndex && drag.OriginalBounds[index].Contains(cursor))
            {
                return index;
            }
        }
        return -1;
    }

    internal void CompleteShortcutDrag(ShortcutDrag drag, int dx, int dy)
    {
        ShortcutWindow? target = PlaceShortcutDrag(drag, dx, dy);
        var changes = new List<ShortcutEntry>();
        Guid? groupId = target is null ? drag.Source.Entry.GroupId : target.Entry.GroupId ?? Guid.NewGuid();
        foreach (ShortcutWindow window in drag.Windows)
        {
            ShortcutEntry entry = AtPosition(window.Entry, window.ScreenBounds.Location);
            changes.Add(target is null ? entry : entry with
            {
                GroupId = groupId,
                BackgroundTransparency = target.Entry.BackgroundTransparency,
                AlwaysOnTop = target.Entry.AlwaysOnTop
            });
        }

        if (target is not null)
        {
            foreach (ShortcutWindow window in GroupWindows(target.Entry))
            {
                changes.Add(AtPosition(window.Entry with { GroupId = groupId }, window.ScreenBounds.Location));
            }
        }

        if (!SaveShortcutChanges(changes, drag.Source))
        {
            CancelShortcutDrag(drag);
        }
    }

    internal static void CancelShortcutDrag(ShortcutDrag drag)
    {
        drag.HighlightReorderTarget(-1);
        MoveGroup(drag.Windows, drag.OriginalBounds);
    }

    private ShortcutWindow? PlaceShortcutDrag(ShortcutDrag drag, int dx, int dy)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(drag.Source);
        Rectangle anchor = drag.OriginalBounds[drag.AnchorIndex];
        double scaleX = dpi.DpiScaleX / drag.InitialDpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY / drag.InitialDpi.DpiScaleY;
        var proposed = new Rectangle[drag.Windows.Length];
        for (int index = 0; index < proposed.Length; index++)
        {
            Rectangle original = drag.OriginalBounds[index];
            proposed[index] = new Rectangle(anchor.X + dx + DevicePixels(original.X - anchor.X, scaleX),
                anchor.Y + dy + DevicePixels(original.Y - anchor.Y, scaleY),
                DevicePixels(original.Width, scaleX), DevicePixels(original.Height, scaleY));
        }

        Rectangle area = NativeWindow.WorkingArea(ShortcutGroupLayout.Bounds(proposed));
        FitGroup(proposed, area);
        var moving = drag.Windows.ToHashSet();
        ShortcutWindow[] icons = _windows.Values.Where(window => window.IsVisible && !moving.Contains(window)).ToArray();
        List<Rectangle> neighbors = icons.Select(window => window.ScreenBounds).ToList();
        if (_todo is { IsVisible: true }) neighbors.Add(_todo.ScreenBounds);
        if (_calendar is { IsVisible: true }) neighbors.Add(_calendar.ScreenBounds);

        ShortcutSnapMatch? match = ShortcutSnap.FindMatch(proposed, neighbors, area, dpi.DpiScaleX, dpi.DpiScaleY);
        if (match is { } snap)
        {
            for (int index = 0; index < proposed.Length; index++)
            {
                proposed[index].Offset(snap.Offset);
            }
        }
        MoveGroup(drag.Windows, proposed);
        return match is { NeighborIndex: int target } && target < icons.Length ? icons[target] : null;
    }

    internal void DetachShortcut(ShortcutEntry entry)
    {
        if (entry.GroupId is not null)
        {
            SaveShortcutChanges([entry with { GroupId = null }], _windows[entry.Id]);
        }
    }

    private void RestoreLegacyShortcutGroups()
    {
        ShortcutEntry[] legacy = _entries.Where(entry => !entry.HasGroupSetting).ToArray();
        if (legacy.Length == 0)
        {
            return;
        }

        var updates = legacy.ToDictionary(entry => entry.Id, entry => entry with { GroupId = null });
        var remaining = legacy.Select(entry => _windows[entry.Id])
            .Where(window => window.Entry.X == window.ScreenBounds.X && window.Entry.Y == window.ScreenBounds.Y).ToList();
        while (remaining.Count > 0)
        {
            ShortcutWindow anchor = remaining[0];
            var group = new List<ShortcutWindow> { anchor };
            remaining.RemoveAt(0);
            for (int index = 0; index < group.Count; index++)
            {
                ShortcutWindow member = group[index];
                DpiScale dpi = VisualTreeHelper.GetDpi(member);
                Rectangle bounds = member.ScreenBounds;
                var neighbors = remaining.Where(window => ShortcutGroupLayout.AreAttached(bounds, window.ScreenBounds,
                    ShortcutSnap.PixelGap(dpi.DpiScaleX), ShortcutSnap.PixelGap(dpi.DpiScaleY))).ToArray();
                foreach (ShortcutWindow neighbor in neighbors)
                {
                    group.Add(neighbor);
                    remaining.Remove(neighbor);
                }
            }

            if (group.Count > 1)
            {
                Guid groupId = Guid.NewGuid();
                foreach (ShortcutWindow window in group)
                {
                    updates[window.Entry.Id] = window.Entry with
                    {
                        GroupId = groupId,
                        BackgroundTransparency = anchor.Entry.BackgroundTransparency,
                        AlwaysOnTop = anchor.Entry.AlwaysOnTop
                    };
                }
            }
        }

        SaveShortcutChanges(updates.Values, _windows[legacy[0].Id]);
    }

    private bool SaveShortcutChanges(IEnumerable<ShortcutEntry> changes, Window owner)
    {
        var byId = changes.ToDictionary(entry => entry.Id);
        List<ShortcutEntry> updated = _entries.Select(entry => byId.GetValueOrDefault(entry.Id, entry)).ToList();
        ShortcutStore.ClearSingletonGroups(updated);
        if (!TrySave(updated, owner))
        {
            return false;
        }

        ApplyShortcutChanges(updated);
        return true;
    }

    private void ApplyShortcutChanges(IEnumerable<ShortcutEntry> entries)
    {
        foreach (ShortcutEntry entry in entries)
        {
            if (_windows.TryGetValue(entry.Id, out ShortcutWindow? window) && window.Entry != entry)
            {
                window.Apply(entry);
            }
        }
    }

    private ShortcutWindow[] GroupWindows(ShortcutEntry entry) => _windows.Values.Where(window => window.Entry.Id == entry.Id ||
        (entry.GroupId is Guid groupId && window.Entry.GroupId == groupId)).ToArray();

    private static ShortcutEntry AtPosition(ShortcutEntry entry, DrawingPoint position) => entry with { X = position.X, Y = position.Y };

    private static int DevicePixels(int length, double scale) => (int)Math.Round(length * scale, MidpointRounding.AwayFromZero);

    private static void FitGroup(Rectangle[] positions, Rectangle area)
    {
        DrawingPoint offset = ShortcutGroupLayout.FitOffset(ShortcutGroupLayout.Bounds(positions), area);
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index].Offset(offset);
        }
    }

    private static void MoveGroup(ShortcutWindow[] windows, Rectangle[] positions)
    {
        for (int index = 0; index < windows.Length; index++)
        {
            windows[index].MoveTo(positions[index].Location);
        }
    }

    private void FitShortcutWindows()
    {
        foreach (ShortcutWindow window in _windows.Values.Where(window => window.Entry.GroupId is null))
        {
            window.FitOnScreen();
        }
        foreach (var group in _windows.Values.Where(window => window.Entry.GroupId is not null).GroupBy(window => window.Entry.GroupId))
        {
            ShortcutWindow[] windows = group.ToArray();
            Rectangle[] positions = windows.Select(window => window.ScreenBounds).ToArray();
            FitGroup(positions, NativeWindow.WorkingArea(ShortcutGroupLayout.Bounds(positions)));
            MoveGroup(windows, positions);
        }
    }

    internal sealed record ShortcutDrag(ShortcutWindow Source, ShortcutWindow[] Windows, Rectangle[] OriginalBounds,
        int AnchorIndex, DpiScale InitialDpi)
    {
        private int _reorderTarget = -1;

        internal void HighlightReorderTarget(int target)
        {
            if (_reorderTarget == target)
            {
                return;
            }

            if (_reorderTarget >= 0)
            {
                Windows[_reorderTarget].ShowReorderTarget(false);
            }
            _reorderTarget = target;
            if (target >= 0)
            {
                Windows[target].ShowReorderTarget(true);
            }
        }
    }

    internal void RemoveShortcut(Guid id, ShortcutWindow owner)
    {
        List<ShortcutEntry> updated = _entries.Where(entry => entry.Id != id).ToList();
        ShortcutStore.ClearSingletonGroups(updated);
        if (TrySave(updated, owner))
        {
            _windows.Remove(id);
            owner.Close();
            ApplyShortcutChanges(updated);
        }
    }

    internal static bool TryReadWindowsStartup(out bool enabled)
    {
        try
        {
            enabled = WindowsStartup.IsEnabled();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            enabled = false;
            return false;
        }
    }

    internal void ToggleWindowsStartup(Window? owner = null)
    {
        try
        {
            WindowsStartup.SetEnabled(!WindowsStartup.IsEnabled());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            string message = $"Windows 자동 실행 설정을 변경하지 못했습니다.\n\n{ex.Message}";
            if (owner is null)
            {
                MessageBox.Show(message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(owner, message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CreateTrayIcon()
    {
        using Stream iconStream = GetResourceStream(new Uri("Assets/Folder.ico", UriKind.Relative)).Stream;
        using var icon = new Icon(iconStream, Tray.SystemInformation.SmallIconSize);
        _trayImage = (Icon)icon.Clone();
        var menu = new Tray.ContextMenuStrip();
        menu.Items.Add("새 버튼 추가…", null, (_, _) => ShowEditor());
        menu.Items.Add("할 일 목록 열기", null, (_, _) => ShowTodo());
        menu.Items.Add("달력 열기", null, (_, _) => ShowCalendar());
        menu.Items.Add(new Tray.ToolStripSeparator());
        var startup = new Tray.ToolStripMenuItem(WindowsStartup.MenuLabel);
        startup.Click += (_, _) => ToggleWindowsStartup();
        menu.Items.Add(startup);
        menu.Opening += (_, _) =>
        {
            startup.Enabled = TryReadWindowsStartup(out bool enabled);
            startup.Checked = enabled;
        };
        menu.Items.Add(new Tray.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => RequestExit());
        _trayIcon = new Tray.NotifyIcon
        {
            Icon = _trayImage,
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
            FitShortcutWindows();
            _todo?.FitOnScreen();
            _calendar?.FitOnScreen();
            RestoreShortcutTopmostOrder();
        });
    }

    private void RestoreShortcutTopmostOrder() => NativeWindow.RestoreTopmostOrder(_windows.Values
        .Where(window => window.IsVisible && window.Entry.AlwaysOnTop).Select(window => window.Handle));
}
