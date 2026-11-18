using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace DesktopShortcuts;

internal sealed class TodoDay : INotifyPropertyChanged
{
    private string _note = "";

    public ObservableCollection<TodoItem> Items { get; init; } = [];

    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class TodoSession : INotifyPropertyChanged, IDisposable
{
    private readonly TodoStore _store;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;
    private bool _saveFailed;

    public TodoBoard Board { get; }
    public DateOnly Date { get; private set; }
    public TodoDay Day { get; private set; }
    public string DateCaption => $"{Date.Month}월 {Date.Day}일";
    public string MemoDateCaption => $"{Date.Year}년 {Date.Month}월 {Date.Day}일";
    public bool SaveFailed => _saveFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? NoteChanged;

    public TodoSession(TodoBoard board, TodoStore store)
    {
        Board = board;
        _store = store;
        Date = board.SelectedDate ?? DateOnly.FromDateTime(DateTime.Today);
        Day = GetDay(Date);
        Day.PropertyChanged += OnNoteChanged;
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
        Date = date;
        Board.SelectedDate = date;
        Day = GetDay(date);
        Day.PropertyChanged += OnNoteChanged;
        Notify(nameof(Date));
        Notify(nameof(Day));
        Notify(nameof(DateCaption));
        Notify(nameof(MemoDateCaption));
        QueueSave();
    }

    public string Preview(DateOnly date)
    {
        if (!Board.Days.TryGetValue(date, out TodoDay? day))
        {
            return "";
        }

        var preview = new StringBuilder();
        int count = 0;
        TextElementEnumerator letters = StringInfo.GetTextElementEnumerator(day.Note.Trim());
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
    }

    private TodoDay GetDay(DateOnly date) =>
        Board.Days.TryGetValue(date, out TodoDay? day) ? day : Board.Days[date] = new TodoDay();

    private void OnNoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueSave();
        NoteChanged?.Invoke(this, EventArgs.Empty);
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
