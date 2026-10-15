namespace LTOG;

/// <summary>One captured index snapshot, parsed for display.</summary>
public sealed class SchemaItem
{
    public string Title { get; set; } = "";        // volume name
    public bool Unreadable { get; set; }           // the snapshot couldn't be parsed
    public int? FileCount { get; set; }            // null if the snapshot is unreadable
    public Core.TapeRecord? Tape { get; set; }     // from tapes.db; null until seen in a drive
    public string Uuid { get; set; } = "";         // volume UUID (ties snapshot to cartridge)
    public string Path { get; set; } = "";         // snapshot file on disk
    public DateTime? Written { get; set; }         // when LTFS wrote this index to tape, local time

    // the tape's space, as on the drive page's capacity tile
    public bool HasTape => Tape != null;
    public bool NoTape => Tape == null;
    public double UsedPercent => Tape is { Capacity: > 0 } t ? 100.0 * (t.Capacity - t.Free) / t.Capacity : 0;
    public bool UsageCritical => UsedPercent > 90;
    public bool UsageNormal => HasTape && !UsageCritical;
    public Microsoft.UI.Xaml.GridLength UsedLength => new(UsedPercent, Microsoft.UI.Xaml.GridUnitType.Star);
    public Microsoft.UI.Xaml.GridLength FreeLength => new(100 - UsedPercent, Microsoft.UI.Xaml.GridUnitType.Star);
    // with a tape, the usage bar is the card's bottom edge: no bottom border or corners
    public Microsoft.UI.Xaml.Thickness CardBorder => new(1, 1, 1, HasTape ? 0 : 1);
    public Microsoft.UI.Xaml.CornerRadius CardCorners => new(8, 8, HasTape ? 0 : 8, HasTape ? 0 : 8);
    public bool HasGeneration => Tape?.Generation != null;
    public string GenerationText => Tape?.Generation ?? "";
    public string FreeText => Tape == null ? "" : $"{SchemaSnapshot.FormatSize(Tape.Free)} free";
    public string UsedText => Tape == null ? "" : $"{SchemaSnapshot.FormatSize(Tape.Capacity - Tape.Free)} used";
    public string FilesText => FileCount is { } n ? $"{n:N0} files" : "";
    public bool HasSerial => Serial != "";
    public string Serial => Tape?.Barcode ?? Tape?.Serial ?? "";   // the LTFS barcode, else the cartridge's serial
    public string WrittenText => Unreadable ? "Unreadable index snapshot"
        : Written is { } w ? $"Last modified: {w:yyyy-MM-dd HH:mm}" : "";
}

/// <summary>Parsed LTFS index snapshot used by the visual browser.</summary>
public sealed class SchemaSnapshot
{
    public string Path { get; init; } = "";
    public string FormatVersion { get; set; } = "";
    public string Creator { get; set; } = "";
    public string VolumeUuid { get; set; } = "";
    public string GenerationNumber { get; set; } = "?";
    public string UpdatedText { get; set; } = "";
    public string HighestFileUid { get; set; } = "";
    public string PolicyOverrideText { get; set; } = "Not specified";
    public string VolumeLockState { get; set; } = "";
    public SchemaNode Root { get; set; } = null!;

    /// <summary>"1.36 TiB (1,498,000,000,000 bytes)"</summary>
    public static string FormatBytes(long bytes) =>
        bytes < 1024 ? $"{bytes:N0} B" : $"{FormatSize(bytes)} ({bytes:N0} bytes)";

    /// <summary>Binary units, labelled as such: "1.36 TiB".</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>One directory or file inside an LTFS index snapshot.</summary>
public sealed class SchemaNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool ReadOnly { get; set; }
    public string FileUid { get; set; } = "";
    public long? Length { get; set; }
    public string CreationTime { get; set; } = "";
    public string ModifyTime { get; set; } = "";
    public string AccessTime { get; set; } = "";
    public string BackupTime { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    /// <summary>Where the file's first extent starts on tape.</summary>
    public string FirstLocationText { get; set; } = "Not specified";
    public List<SchemaNode> Children { get; } = new();

    public string IconGlyph => IsDirectory ? "" : ""; // Folder / Document
}
