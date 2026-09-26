using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;

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
    public string FileName { get; init; } = "";
    public string VolumeName { get; set; } = "(unlabelled volume)";
    public string FormatVersion { get; set; } = "";
    public string Creator { get; set; } = "";
    public string VolumeUuid { get; set; } = "";
    public string GenerationNumber { get; set; } = "?";
    public string UpdatedText { get; set; } = "";
    public string LocationText { get; set; } = "";
    public string PreviousLocationText { get; set; } = "";
    public string HighestFileUid { get; set; } = "";
    public string PolicyOverrideText { get; set; } = "Not specified";
    public string VolumeLockState { get; set; } = "";
    public int DirectoryCount { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public SchemaNode? Root { get; set; }

    public string Title => string.IsNullOrWhiteSpace(VolumeName) ? FileName : VolumeName;
    public string CountText => $"{DirectoryCount:N0} director{(DirectoryCount == 1 ? "y" : "ies")}, " +
                               $"{FileCount:N0} file{(FileCount == 1 ? "" : "s")}";
    public string BytesText => FormatBytes(TotalBytes);

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
public sealed class SchemaNode : INotifyPropertyChanged
{
    private bool _expanded;

    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "Directory";
    public bool IsDirectory { get; set; }
    public bool ReadOnly { get; set; }
    public bool OpenForWrite { get; set; }
    public string FileUid { get; set; } = "";
    public long? Length { get; set; }
    public string CreationTime { get; set; } = "";
    public string ChangeTime { get; set; } = "";
    public string ModifyTime { get; set; } = "";
    public string AccessTime { get; set; } = "";
    public string BackupTime { get; set; } = "";
    public string FirstLocationText => Extents.Count == 0
        ? "Not specified"
        : $"Partition {Extents[0].PartitionDisplay} starting from block {Extents[0].StartBlockDisplay}";
    public int Depth { get; set; }
    public List<SchemaNode> Children { get; } = new();
    public ObservableCollection<SchemaExtent> Extents { get; } = new();

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Raise(nameof(Expanded));
            Raise(nameof(ExpandGlyph));
        }
    }

    public bool CanExpand => Children.Count > 0;
    public string ExpandGlyph => !CanExpand ? "" : Expanded ? "\uE70D" : "\uE76C"; // ChevronDown / ChevronRight
    public double ExpandButtonOpacity => CanExpand ? 1 : 0;
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5"; // Folder / Document
    public Thickness RowMargin => new(Math.Min(Depth, 12) * 18, 0, 0, 0);
    public string LengthText => IsDirectory
        ? $"{Children.Count:N0} item{(Children.Count == 1 ? "" : "s")}"
        : Length.HasValue ? SchemaSnapshot.FormatBytes(Length.Value) : "";
    public string FlagsText
    {
        get
        {
            var flags = new List<string>();
            if (ReadOnly) flags.Add("read-only");
            if (OpenForWrite) flags.Add("open for write");
            return flags.Count == 0 ? "none" : string.Join(", ", flags);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Tree row wrapper so WinUI TreeView can bind to lazy nodes safely.</summary>
public sealed class SchemaTreeRow
{
    public SchemaNode Node { get; }

    public SchemaTreeRow(SchemaNode node) => Node = node;

    public string Name => Node.Name;
    public string IconGlyph => Node.IconGlyph;
}

/// <summary>One tape extent for a file in an LTFS index snapshot.</summary>
public sealed class SchemaExtent
{
    public string FileOffset { get; set; } = "";
    public string Partition { get; set; } = "";
    public string StartBlock { get; set; } = "";
    public string ByteOffset { get; set; } = "";
    public string ByteCount { get; set; } = "";

    public string PartitionDisplay => Blank(Partition);
    public string StartBlockDisplay => Blank(StartBlock);
    public string LocationText => $"partition {PartitionDisplay}, block {StartBlockDisplay}";
    public string OffsetText => $"file offset {Blank(FileOffset)}, byte offset {Blank(ByteOffset)}";
    public string ByteCountText => long.TryParse(ByteCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
        ? SchemaSnapshot.FormatBytes(bytes)
        : Blank(ByteCount);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value;
}
