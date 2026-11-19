using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace DesktopShortcuts;

internal sealed class TodoDay : INotifyPropertyChanged
{
    private string? _todoNote;
    private string? _calendarNote;

    public ObservableCollection<TodoItem> Items { get; init; } = [];

    public string TodoNote
    {
        get => _todoNote ?? "";
        set => SetNote(ref _todoNote, value);
    }

    public string CalendarNote
    {
        get => _calendarNote ?? "";
        set => SetNote(ref _calendarNote, value);
    }

    [JsonPropertyName("Note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedNote { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ObservableCollection<TodoItem>? FindList(TodoItem item)
    {
        if (Items.Contains(item))
        {
            return Items;
        }

        return Items.FirstOrDefault(parent => parent.Children.Contains(item))?.Children;
    }

    internal bool MigrateSharedNote()
    {
        if (SharedNote is not string legacyNote)
        {
            return false;
        }

        _todoNote ??= legacyNote;
        _calendarNote ??= legacyNote;
        SharedNote = null;
        return true;
    }

    private void SetNote(ref string? field, string? value, [CallerMemberName] string property = "")
    {
        value ??= "";
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}

internal sealed record TodoDrag(TodoSession Session, DateOnly Date, TodoItem Item)
{
    internal const string Format = "DesktopShortcuts.TodoItem";
    internal string Token { get; } = Guid.NewGuid().ToString("N");
}

internal sealed class TodoSession : INotifyPropertyChanged, IDisposable
{
    private readonly TodoStore _store;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;
    private bool _saveFailed;
    private TodoItem? _copiedItem;
    private TodoDrag? _activeDrag;

    public TodoBoard Board { get; }
    public DateOnly Date { get; private set; }
    public TodoDay Day { get; private set; }
    public string DateCaption => $"{Date.Month}월 {Date.Day}일";
    public string MemoDateCaption => $"{Date.Year}년 {Date.Month}월 {Date.Day}일";
    public string TaskCountCaption => $"{Day.Items.Sum(item => item.Children.Count)}/{Day.Items.Count}";
    public bool SaveFailed => _saveFailed;
    public bool CanPasteItem => _copiedItem is not null;
    public bool HasTodoContent => Day.Items.Count > 0 || !string.IsNullOrWhiteSpace(Day.TodoNote);
    internal bool ShouldCompactEmptyTodo => !Board.KeepsEmptySize && !HasTodoContent;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CalendarNoteChanged;
    internal event EventHandler? DragEnded;

    public TodoSession(TodoBoard board, TodoStore store)
    {
        Board = board;
        _store = store;
        Date = board.SelectedDate ?? DateOnly.FromDateTime(DateTime.Today);
        Day = GetDay(Date);
        Day.PropertyChanged += OnNoteChanged;
        Day.Items.CollectionChanged += OnTodoItemsChanged;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => Flush();

        if (board.MigrationPending)
        {
            QueueSave();
        }
    }

    public void Select(DateOnly date)
    {
        if (Date == date)
        {
            return;
        }

        Day.PropertyChanged -= OnNoteChanged;
        Day.Items.CollectionChanged -= OnTodoItemsChanged;
        Date = date;
        Board.SelectedDate = date;
        Day = GetDay(date);
        Day.PropertyChanged += OnNoteChanged;
        Day.Items.CollectionChanged += OnTodoItemsChanged;
        Notify(nameof(Date));
        Notify(nameof(Day));
        Notify(nameof(DateCaption));
        Notify(nameof(MemoDateCaption));
        NotifyTaskCountsChanged();
        Notify(nameof(HasTodoContent));
        QueueSave();
    }

    public void CopyItem(TodoItem item) => _copiedItem = item.Copy();

    public void PasteItem()
    {
        if (_copiedItem is not null)
        {
            Day.Items.Add(_copiedItem.Copy());
            QueueSave();
        }
    }

    internal TodoDrag? StartDrag(TodoItem item) =>
        _activeDrag = Day.FindList(item) is null ? null : new TodoDrag(this, Date, item);

    internal TodoDrag? ReadDrag(IDataObject data) => _activeDrag is { } drag &&
        data.GetDataPresent(TodoDrag.Format, autoConvert: false) &&
        data.GetData(TodoDrag.Format, autoConvert: false) is string token && token == drag.Token ? drag : null;

    internal void EndDrag()
    {
        _activeDrag = null;
        DragEnded?.Invoke(this, EventArgs.Empty);
    }

    private ObservableCollection<TodoItem>? DragSource(TodoDrag drag) =>
        ReferenceEquals(drag.Session, this) && Board.Days.TryGetValue(drag.Date, out TodoDay? source)
            ? source.FindList(drag.Item) : null;

    internal bool CanReorder(TodoDrag drag, TodoItem anchor) => drag.Date == Date && !ReferenceEquals(drag.Item, anchor)
        && DragSource(drag) is { } list && list.Contains(anchor);

    internal bool Reorder(TodoDrag drag, TodoItem anchor, bool after)
    {
        if (!CanReorder(drag, anchor))
        {
            return false;
        }

        var items = Day.FindList(drag.Item)!;
        int from = items.IndexOf(drag.Item);
        int to = items.IndexOf(anchor) + (after ? 1 : 0);
        if (from < to)
        {
            to--;
        }

        if (from != to)
        {
            items.Move(from, to);
            QueueSave();
        }

        return true;
    }

    internal bool CanMoveToDate(TodoDrag drag, DateOnly destination) => drag.Date != destination && DragSource(drag) is not null;

    internal bool MoveToDate(TodoDrag drag, DateOnly destination)
    {
        if (drag.Date == destination || DragSource(drag) is not { } source)
        {
            return false;
        }

        TodoDay target = GetDay(destination);
        source.Remove(drag.Item);
        target.Items.Add(drag.Item);
        NotifyTaskCountsChanged();
        QueueSave();
        return true;
    }

    internal void NotifyTaskCountsChanged() => Notify(nameof(TaskCountCaption));

    public bool HasTodoItems(DateOnly date) => Board.Days.TryGetValue(date, out TodoDay? day) && day.Items.Count > 0;

    public string GetCalendarNote(DateOnly date) => Board.Days.TryGetValue(date, out TodoDay? day) ? day.CalendarNote : "";

    public string Preview(DateOnly date)
    {
        var preview = new StringBuilder();
        int count = 0;
        TextElementEnumerator letters = StringInfo.GetTextElementEnumerator(GetCalendarNote(date).Trim());
        while (letters.MoveNext())
        {
            string letter = letters.GetTextElement();
            if (string.IsNullOrWhiteSpace(letter))
            {
                continue;
            }

            preview.Append(letter);
            if (++count == 4)
            {
                break;
            }
        }

        return preview.ToString();
    }

    public void QueueSave()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public bool Flush()
    {
        _saveTimer.Stop();
        if (!_dirty)
        {
            return true;
        }

        try
        {
            _store.Save(Board);
            _dirty = false;
            SetSaveFailed(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetSaveFailed(true);
            _saveTimer.Start();
            return false;
        }
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        Day.PropertyChanged -= OnNoteChanged;
        Day.Items.CollectionChanged -= OnTodoItemsChanged;
    }

    private TodoDay GetDay(DateOnly date) =>
        Board.Days.TryGetValue(date, out TodoDay? day) ? day : Board.Days[date] = new TodoDay();

    private void OnNoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueSave();
        if (e.PropertyName == nameof(TodoDay.CalendarNote))
        {
            CalendarNoteChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(TodoDay.TodoNote))
        {
            Notify(nameof(HasTodoContent));
        }
    }

    private void OnTodoItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Notify(nameof(HasTodoContent));
        NotifyTaskCountsChanged();
    }

    private void SetSaveFailed(bool failed)
    {
        if (_saveFailed != failed)
        {
            _saveFailed = failed;
            Notify(nameof(SaveFailed));
        }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
