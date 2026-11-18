using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopShortcuts;

internal sealed class ShortcutAppearance : IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly DispatcherTimer _reloadTimer;
    private readonly FileSystemWatcher _watcher;
    private readonly string _path;
    private bool _disposed;

    public ShortcutAppearance(Application app, string path)
    {
        _path = path;
        _resources = app.Resources;
        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += QueueReload;
        _watcher.Created += QueueReload;
        _watcher.Renamed += QueueReload;
        _watcher.EnableRaisingEvents = true;
        Reload();
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.Dispose();
        _reloadTimer.Stop();
    }

    private void QueueReload(object sender, FileSystemEventArgs e)
    {
        _reloadTimer.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void Reload()
    {
        try
        {
            using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            AppearanceSettings? settings = JsonSerializer.Deserialize<AppearanceSettings>(stream);
            if (settings is null || settings.CornerRadius is < 0 or > 20 || settings.LabelFontSize is < 7 or > 14)
            {
                return;
            }

            Brush label = new SolidColorBrush(ParseColor(settings.LabelColor));
            Brush folder = new SolidColorBrush(ParseColor(settings.FolderColor));
            Brush background = new LinearGradientBrush(ParseColor(settings.BackgroundStart), ParseColor(settings.BackgroundEnd), new Point(0, 0), new Point(1, 1));
            Brush selection = new LinearGradientBrush(ParseColor(settings.SelectionStart), ParseColor(settings.SelectionEnd), new Point(0, 0), new Point(1, 1));
            label.Freeze();
            folder.Freeze();
            background.Freeze();
            selection.Freeze();

            _resources["ShortcutCornerRadius"] = new CornerRadius(settings.CornerRadius);
            _resources["ShortcutLabelFontSize"] = settings.LabelFontSize;
            _resources["ShortcutLabelBrush"] = label;
            _resources["ShortcutFolderBrush"] = folder;
            _resources["ShortcutBackground"] = background;
            _resources["ShortcutSelectionBackground"] = selection;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // Keep the last valid appearance while the file is missing or being saved.
        }
    }

    private static Color ParseColor(string? value)
    {
        if (value is null || value.Length != 9 || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            throw new FormatException();
        }

        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private sealed record AppearanceSettings(double CornerRadius, double LabelFontSize, string LabelColor,
        string FolderColor, string BackgroundStart, string BackgroundEnd, string SelectionStart, string SelectionEnd);
}
