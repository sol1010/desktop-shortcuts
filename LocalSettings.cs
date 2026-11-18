using System;
using System.IO;
using System.Text.Json;

namespace DesktopShortcuts;

internal static class LocalSettings
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopShortcuts");

    public static void Save<T>(string path, T value, JsonSerializerOptions options)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string pendingPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = File.Open(pendingPath, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(pendingPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(pendingPath);
        }
    }
}
