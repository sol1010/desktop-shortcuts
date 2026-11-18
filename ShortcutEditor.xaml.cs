using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace DesktopShortcuts;

public partial class ShortcutEditor : Window
{
    private static readonly string[] ProgramExtensions = [".exe", ".lnk", ".bat", ".cmd", ".com", ".appref-ms"];
    private readonly ShortcutEntry _original;
    private readonly bool _sizeOnly;
    private bool _settingSize;

    internal Func<ShortcutEntry, bool>? SaveRequested { private get; set; }

    internal ShortcutEditor(ShortcutEntry? entry, IReadOnlyList<ShortcutSize> usedSizes,
        ShortcutSize? suggestedSize, bool sizeOnly)
    {
        _original = entry ?? new ShortcutEntry { Size = suggestedSize ?? ShortcutSize.Medium };
        _sizeOnly = sizeOnly;
        InitializeComponent();

        UsedSizeSettings.Visibility = usedSizes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedSizeInput.ItemsSource = usedSizes;
        UsedSizeInput.SelectedItem = _original.Size;
        SetSizeInputs(_original.Size);
        UsedSizeInput.SelectionChanged += (_, _) =>
        {
            if (UsedSizeInput.SelectedItem is ShortcutSize size)
            {
                SetSizeInputs(size);
            }
        };
        SideInput.TextChanged += ClearSizeSelection;

        if (sizeOnly)
        {
            Title = "버튼 크기";
            TargetSettings.Visibility = Visibility.Collapsed;
        }
        else if (entry is not null)
        {
            Title = "이름 및 연결 변경";
            NameInput.Text = entry.Name;
            TargetInput.Text = entry.TargetPath;
            ProgramOption.IsChecked = entry.Kind == TargetKind.Program;
            FolderOption.IsChecked = entry.Kind == TargetKind.Folder;
            CalendarOption.IsChecked = entry.Kind == TargetKind.Calendar;
        }

        FolderOption.Checked += (_, _) => UpdateTargetFields();
        ProgramOption.Checked += (_, _) => UpdateTargetFields();
        CalendarOption.Checked += (_, _) => UpdateTargetFields();
        UpdateTargetFields();

        Loaded += (_, _) =>
        {
            if (sizeOnly)
            {
                SideInput.Focus();
                SideInput.SelectAll();
            }
            else if (entry is null)
            {
                TargetInput.Focus();
            }
            else
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }
        };
    }

    private void SetSizeInputs(ShortcutSize size)
    {
        _settingSize = true;
        SideInput.Text = size.Width.ToString();
        _settingSize = false;
    }

    private void ClearSizeSelection(object sender, TextChangedEventArgs e)
    {
        if (!_settingSize)
        {
            UsedSizeInput.SelectedIndex = -1;
        }
    }

    private TargetKind SelectedKind => CalendarOption.IsChecked == true ? TargetKind.Calendar :
        ProgramOption.IsChecked == true ? TargetKind.Program : TargetKind.Folder;

    private void UpdateTargetFields()
    {
        PathSettings.Visibility = SelectedKind == TargetKind.Calendar ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        string? selectedPath;
        if (SelectedKind == TargetKind.Folder)
        {
            var picker = new OpenFolderDialog { Title = "연결할 폴더 선택" };
            selectedPath = picker.ShowDialog(this) == true ? picker.FolderName : null;
        }
        else
        {
            var picker = new OpenFileDialog
            {
                Title = "연결할 프로그램 선택",
                Filter = "프로그램 및 Windows 바로가기|*.exe;*.lnk;*.bat;*.cmd;*.com;*.appref-ms",
                CheckFileExists = true
            };
            selectedPath = picker.ShowDialog(this) == true ? picker.FileName : null;
        }

        if (selectedPath is null)
        {
            return;
        }

        TargetInput.Text = selectedPath;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            NameInput.Text = DefaultName(selectedPath, SelectedKind);
            NameInput.SelectAll();
        }

        ErrorText.Visibility = Visibility.Collapsed;
        NameInput.Focus();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SideInput.Text, out int side) || side is < ShortcutSize.MinSide or > ShortcutSize.MaxSide)
        {
            ShowError($"한 변의 길이는 {ShortcutSize.MinSide}~{ShortcutSize.MaxSide} 사이의 정수로 입력하세요.", SideInput);
            return;
        }

        ShortcutSize size = ShortcutSize.Square(side);
        if (_sizeOnly)
        {
            Commit(_original with { Size = size });
            return;
        }

        TargetKind kind = SelectedKind;
        string name = NameInput.Text.Trim();
        if (kind == TargetKind.Calendar)
        {
            Commit(_original with
            {
                Name = name.Length == 0 ? "달력" : name,
                TargetPath = "",
                Kind = kind,
                Size = size
            });
            return;
        }

        string path = Environment.ExpandEnvironmentVariables(TargetInput.Text.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(path))
        {
            ShowError("연결할 폴더 또는 프로그램의 전체 경로를 입력하세요.");
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            ShowError("경로 형식을 확인하세요.");
            return;
        }

        bool targetExists = kind == TargetKind.Folder ? Directory.Exists(path) : File.Exists(path);
        if (!targetExists)
        {
            ShowError(kind == TargetKind.Folder ? "지정한 폴더를 찾을 수 없습니다." : "지정한 프로그램을 찾을 수 없습니다.");
            return;
        }

        if (kind == TargetKind.Program &&
            !Array.Exists(ProgramExtensions, extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("실행 파일, 배치 파일 또는 Windows 바로가기를 선택하세요.");
            return;
        }

        Commit(_original with
        {
            Name = name.Length == 0 ? DefaultName(path, kind) : name,
            TargetPath = path,
            Kind = kind,
            Size = size
        });
    }

    private void Commit(ShortcutEntry entry)
    {
        if (SaveRequested?.Invoke(entry) == true)
        {
            Close();
        }
    }

    private void Cancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && !UsedSizeInput.IsDropDownOpen)
        {
            e.Handled = true;
            Close();
        }
    }

    internal void ShowSaveError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message, Control? focus = null)
    {
        ShowSaveError(message);
        (focus ?? TargetInput).Focus();
    }

    private static string DefaultName(string path, TargetKind kind)
    {
        string name = kind == TargetKind.Folder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
