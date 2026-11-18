using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopShortcuts;

internal enum TargetKind { Folder, Program, Calendar }
internal enum ShortcutActivation { DoubleClick, SingleClick }
internal enum LegacyShortcutSize { Small, Medium, Large }

internal readonly record struct ShortcutSize(int Width, int Height)
{
    public const int MinSide = 18;
    public const int MaxSide = 300;

    public static readonly ShortcutSize Small = new(48, 48);
    public static readonly ShortcutSize Medium = new(60, 60);
    public static readonly ShortcutSize Large = new(76, 76);

    [JsonIgnore]
    public bool IsValid => Width is >= MinSide and <= 400 && Height is >= MinSide and <= MaxSide;

    public static ShortcutSize Square(int side) => new(side, side);

    public ShortcutSize AsSquare() => Square(Math.Clamp(Math.Min(Width, Height), MinSide, MaxSide));

    public override string ToString() => $"{Width} × {Height}";
}

internal sealed record ShortcutEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public TargetKind Kind { get; init; }

    [JsonPropertyName("Dimensions")]
    public ShortcutSize Size { get; init; } = ShortcutSize.Medium;

    [JsonPropertyName("Size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyShortcutSize? PreviousSize { get; init; }

    public ShortcutActivation Activation { get; init; } = ShortcutActivation.DoubleClick;
    public bool AlwaysOnTop { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
}

internal static class ShortcutStore
{
    private static readonly string FilePath = Path.Combine(LocalSettings.DirectoryPath, "shortcuts.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static List<ShortcutEntry> Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            return Read(stream);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static List<ShortcutEntry> Read(Stream stream)
    {
        List<ShortcutEntry> entries = JsonSerializer.Deserialize<List<ShortcutEntry>>(stream, JsonOptions)
            ?? throw new JsonException("바로가기 목록이 비어 있습니다.");

        var ids = new HashSet<Guid>();
        for (int index = 0; index < entries.Count; index++)
        {
            ShortcutEntry entry = entries[index];
            if (entry is null || !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) ||
                !Enum.IsDefined(entry.Kind) ||
                (entry.Kind != TargetKind.Calendar &&
                    (string.IsNullOrWhiteSpace(entry.TargetPath) || !Path.IsPathFullyQualified(entry.TargetPath))) ||
                !Enum.IsDefined(entry.Activation) || !entry.Size.IsValid)
            {
                throw new JsonException("설정 파일에 올바르지 않은 바로가기가 있습니다.");
            }

            if (entry.PreviousSize is LegacyShortcutSize previous)
            {
                ShortcutSize size = previous switch
                {
                    LegacyShortcutSize.Small => ShortcutSize.Small,
                    LegacyShortcutSize.Medium => ShortcutSize.Medium,
                    LegacyShortcutSize.Large => ShortcutSize.Large,
                    _ => throw new JsonException("설정 파일에 올바르지 않은 크기가 있습니다.")
                };
                entries[index] = entry with { Size = size, PreviousSize = null };
            }
            else if (entry.Size.Width != entry.Size.Height)
            {
                entries[index] = entry with { Size = entry.Size.AsSquare() };
            }
        }

        return entries;
    }

    public static void Save(IReadOnlyList<ShortcutEntry> entries) => LocalSettings.Save(FilePath, entries, JsonOptions);
}
