using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopShortcuts;

internal sealed class TodoBoard
{
    public Dictionary<DateOnly, TodoDay> Days { get; init; } = [];
    public DateOnly? SelectedDate { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BackgroundTransparency { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SizeManuallySet { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MemoShare { get; set; }
    internal bool KeepsEmptySize => SizeManuallySet ?? (Width is > 0 && Height is > 0 || MemoShare is > 0 and < 1);
    public bool DockedToCalendar { get; set; }
    public CalendarPlacement Calendar { get; init; } = new();

    [JsonPropertyName("Items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollection<TodoItem>? LegacyItems { get; set; }

    [JsonPropertyName("Note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyNote { get; set; }

    [JsonIgnore]
    public bool MigrationPending { get; set; }
}

internal sealed class CalendarPlacement
{
    public bool IsVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BackgroundTransparency { get; set; }
    public bool WeeklyMemoCollapsed { get; set; }
    public double? WeeklyExpandedHeight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WeeklyMemoShare { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MonthlyMemoShare { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LargeMemoShare { get; set; }
    public bool IsLargeView { get; set; }
}

internal sealed class TodoItem : INotifyPropertyChanged
{
    private bool _isDone;
    private bool _areChildrenCollapsed;

    public string Text { get; set; } = "";
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone != value)
            {
                _isDone = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
            }
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AreChildrenCollapsed
    {
        get => _areChildrenCollapsed;
        set
        {
            if (_areChildrenCollapsed != value)
            {
                _areChildrenCollapsed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AreChildrenCollapsed)));
            }
        }
    }

    public ObservableCollection<TodoItem> Children { get; init; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public TodoItem Copy() => new()
    {
        Text = Text,
        IsDone = IsDone,
        AreChildrenCollapsed = AreChildrenCollapsed,
        Children = new ObservableCollection<TodoItem>(Children.Select(child => child.Copy()))
    };
}

internal sealed class TodoStore(string path, params string[] legacyPaths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static TodoStore Default { get; } = new(
        Path.Combine(LocalSettings.DirectoryPath, "todo-days-v2.json"),
        Path.Combine(LocalSettings.DirectoryPath, "todo-days.json"),
        Path.Combine(LocalSettings.DirectoryPath, "todo.json"));

    public TodoBoard Load()
    {
        try
        {
            using FileStream stream = OpenRead(out bool readLegacy);
            TodoBoard board = JsonSerializer.Deserialize<TodoBoard>(stream, Options)
                ?? throw new JsonException("할 일 목록이 비어 있습니다.");
            if (board.Days is null || board.Calendar is null)
            {
                throw new JsonException("날짜별 할 일 또는 달력 설정 형식을 확인하세요.");
            }

            board.MigrationPending = readLegacy;
            if (board.LegacyItems is not null || board.LegacyNote is not null)
            {
                DateOnly today = DateOnly.FromDateTime(DateTime.Today);
                if (!board.Days.TryGetValue(today, out TodoDay? day))
                {
                    board.Days[today] = new TodoDay
                    {
                        Items = board.LegacyItems ?? [],
                        SharedNote = board.LegacyNote
                    };
                }
                else
                {
                    if (day is null || day.Items is null)
                    {
                        throw new JsonException("할 일 목록 또는 메모 형식을 확인하세요.");
                    }

                    foreach (TodoItem item in board.LegacyItems ?? [])
                    {
                        day.Items.Add(item);
                    }

                    string previousNote = board.LegacyNote ?? "";
                    if (previousNote.Length > 0)
                    {
                        day.MigrateSharedNote();
                        day.TodoNote = AppendNote(day.TodoNote, previousNote);
                        day.CalendarNote = AppendNote(day.CalendarNote, previousNote);
                    }
                }

                board.LegacyItems = null;
                board.LegacyNote = null;
                board.MigrationPending = true;
            }

            foreach (TodoDay day in board.Days.Values)
            {
                if (day is null || day.Items is null)
                {
                    throw new JsonException("할 일 목록 또는 메모 형식을 확인하세요.");
                }

                board.MigrationPending |= day.MigrateSharedNote();
                foreach (TodoItem item in day.Items)
                {
                    if (item is null || item.Text is null || item.Children is null)
                    {
                        throw new JsonException("할 일 항목 형식을 확인하세요.");
                    }

                    foreach (TodoItem child in item.Children)
                    {
                        if (child is null || child.Text is null || child.Children is not { Count: 0 })
                        {
                            throw new JsonException("하위 할 일 항목 형식을 확인하세요.");
                        }
                    }
                }
            }

            return board;
        }
        catch (FileNotFoundException)
        {
            return new TodoBoard();
        }
        catch (DirectoryNotFoundException)
        {
            return new TodoBoard();
        }
    }

    private static string AppendNote(string current, string legacy) =>
        current.Length > 0 ? current + Environment.NewLine + legacy : legacy;

    private FileStream OpenRead(out bool readLegacy)
    {
        readLegacy = false;
        try
        {
            return File.OpenRead(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            foreach (string previous in legacyPaths)
            {
                try
                {
                    FileStream stream = File.OpenRead(previous);
                    readLegacy = true;
                    return stream;
                }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Try the next older format if this one was never created.
                }
            }

            throw;
        }
    }

    public void Save(TodoBoard board) => LocalSettings.Save(path, board, Options);
}
