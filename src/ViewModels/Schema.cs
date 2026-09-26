namespace LTOG;

/// <summary>One captured index snapshot, parsed for display.</summary>
public sealed class SchemaItem
{
    public string Title { get; set; } = "";        // volume name
    public string GenLine { get; set; } = "";      // generation + on-tape update time
    public string CaptureLine { get; set; } = "";  // capture time, file count, size
    public string Uuid { get; set; } = "";         // volume UUID (ties snapshot to cartridge)
    public string Path { get; set; } = "";         // snapshot file on disk
    public DateTime Captured { get; set; }         // snapshot file write time (for sorting)
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
    /// <summary>Where the file's first extent starts on tape.</summary>
    public string FirstLocationText { get; set; } = "Not specified";
    public List<SchemaNode> Children { get; } = new();

    public string IconGlyph => IsDirectory ? "" : ""; // Folder / Document
}
