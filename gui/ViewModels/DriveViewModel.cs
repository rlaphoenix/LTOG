using System.Collections.ObjectModel;
using System.ComponentModel;
using LTOG.Gui.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LTOG.Gui;

public enum MountPhase { Idle, Mounting, Mounted, Unmounting }

/// <summary>One label/value cell on the drive dashboard.</summary>
public sealed record Stat(string Label, string Value);

/// <summary>One row of the raw MAM attribute table.</summary>
public sealed record MamRow(string Id, string Name, string Partition, string Size, string Value, string Hex);

/// <summary>A titled card of stats on the drive dashboard.</summary>
/// <param name="MaxColumns">cells per row; -1 = as many as fit</param>
/// <param name="Tail">more cells after Items, four to a row</param>
public sealed record StatGroup(string Title, IReadOnlyList<Stat> Items, int MaxColumns = -1,
    IReadOnlyList<Stat>? Tail = null)
{
    public IReadOnlyList<Stat> TailItems => Tail ?? [];
}

/// <summary>
/// The view model behind one drive's page: the published drive state, mount phase
/// and options, and the display values and dashboard derived from them.
/// </summary>
public sealed class DriveViewModel : INotifyPropertyChanged
{
    private DriveState _state = null!;
    /// <summary>The drive's latest published state, from <see cref="TapeMonitor"/>.</summary>
    public DriveState State
    {
        get => _state;
        set
        {
            _state = value;
            _driveParams = value.Cartridge?.DriveParams ?? _driveParams;
            _encryption = value.Cartridge?.Encryption ?? _encryption;
            RebuildDashboard();
            Changed();
        }
    }
    public TapeDrive Drive => _state.Drive;
    public string HeaderTitle => $"{Drive.Vendor} {Drive.Product}";

    public Mapping? Mapping { get; set; }          // active mount, if any

    private DriveParams? _driveParams;             // sticky: unreadable while mounted
    private EncryptionInfo? _encryption;           // likewise
    public CartridgeInfo? LastCart => _state.Cartridge;

    public bool MediaAbsent => LastCart?.State == CartridgeState.NoMedia;

    private bool _mediaOpRunning;   // eject/load in progress
    public bool MediaOpRunning
    {
        get => _mediaOpRunning;
        set { _mediaOpRunning = value; Changed(); }
    }

    public string EjectLoadText => (_mediaOpRunning, MediaAbsent) switch
    {
        (true, true) => "Loading...",
        (true, false) => "Ejecting...",
        (false, true) => "Load",
        _ => "Eject",
    };

    private MountPhase _phase = MountPhase.Idle;
    public MountPhase Phase
    {
        get => _phase;
        set { _phase = value; RebuildDashboard(); Changed(); }
    }

    /// <summary>Shown only without a cartridge; the dashboard covers a loaded one.</summary>
    public string CartridgeText => LastCart?.State switch
    {
        CartridgeState.NoMedia => "No Cartridge Inserted",
        CartridgeState.NotReady => "Drive not ready...",
        CartridgeState.DriveInUse => "Drive in use by another program",
        _ => "Checking cartridge...",
    };

    /// <summary>Muted drive-status line under the drive name (the cartridge has its own section).</summary>
    public string StatusText => _mediaOpRunning ? EjectLoadText : _phase switch
    {
        MountPhase.Mounting => $"Mounting at {Mapping?.Letter}...",
        MountPhase.Mounted => $"Mounted at {Mapping?.Letter}" + (Mapping?.Options.ReadOnly == true ? ", read-only" : ""),
        MountPhase.Unmounting => "Unmounting...",
        _ => LastCart?.State switch
        {
            CartridgeState.NoMedia => "Ready, no cartridge",
            CartridgeState.Ltfs or CartridgeState.NotLtfs => "Ready, cartridge loaded",
            CartridgeState.NotReady => "Not ready",
            CartridgeState.DriveInUse => "In use by another program",
            _ => "Checking...",
        },
    };

    public ObservableCollection<string> Letters { get; } = new();

    private int _selectedLetterIndex = -1;
    public int SelectedLetterIndex
    {
        get => _selectedLetterIndex;
        set { _selectedLetterIndex = value; Raise(nameof(SelectedLetterIndex)); }
    }

    public string? SelectedLetter =>
        _selectedLetterIndex >= 0 && _selectedLetterIndex < Letters.Count
            ? Letters[_selectedLetterIndex] : null;

    private bool _readOnly;
    public bool ReadOnlyChecked
    {
        get => _readOnly;
        set { _readOnly = value; Raise(nameof(ReadOnlyChecked)); }
    }

    /// <summary>False when the drive has no usable cartridge; unknown (unreadable) is allowed.</summary>
    public bool CanMount => LastCart?.State is null or CartridgeState.Ltfs or CartridgeState.NotLtfs;

    private bool _globalEnabled = true; // env resolved, no utility running
    public bool GlobalEnabled
    {
        get => _globalEnabled;
        set { _globalEnabled = value; Changed(); }
    }

    // ---- derived UI state --------------------------------------------------

    public bool OptionsEnabled => _phase == MountPhase.Idle && _globalEnabled && !_mediaOpRunning;

    public bool ButtonEnabled => _globalEnabled && !_mediaOpRunning && _phase switch
    {
        MountPhase.Idle => CanMount,
        MountPhase.Mounting => true,    // acts as Cancel
        MountPhase.Mounted => true,
        _ => false,
    };

    public string ButtonText => _phase switch
    {
        MountPhase.Idle => "Mount",
        MountPhase.Mounting => "Mounting",
        MountPhase.Mounted => "Unmount",
        _ => "Unmounting...",
    };

    /// <summary>
    /// Cancel (mounting) is a neutral/grey button; Mount and Unmount use the
    /// accent (blue) style. A null Style falls back to the implicit default.
    /// </summary>
    public Style? ButtonStyle => _phase == MountPhase.Mounting
        ? null
        : (Style)Application.Current.Resources["AccentButtonStyle"];

    public bool IsBusy => _phase is MountPhase.Mounting or MountPhase.Unmounting;
    public Visibility SpinnerVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    // ---- dashboard: display data derived from the drive's state ------------

    private long _capacity, _free;   // bytes
    private string? _sig;

    public bool HasCartridge => _phase != MountPhase.Idle ||
                                LastCart?.State is CartridgeState.Ltfs or CartridgeState.NotLtfs;
    public bool NoCartridge => !HasCartridge;
    public bool HasUsage => _capacity > 0;
    public double UsagePercent => _capacity > 0 ? 100.0 * (_capacity - _free) / _capacity : 0;
    public bool UsageCritical => UsagePercent > 90;
    public string UsageText => $"{UsagePercent:0.0}%";
    public Brush UsageBrush => (Brush)Application.Current.Resources[
        UsageCritical ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"];
    public string UsedText => SchemaSnapshot.FormatSize(_capacity - _free);
    public string FreeText => SchemaSnapshot.FormatSize(_free);
    public string CapacityText => SchemaSnapshot.FormatSize(_capacity);
    public string UsageSource => _phase == MountPhase.Mounted && _state.Usage != null
        ? "Live, from the mounted volume" : "From the cartridge memory (all partitions)";

    public Stat? NameStat { get; private set; }           // full-width tile
    public Stat? ManufacturerStat { get; private set; }   // half-width tiles, under the others
    public Stat? ManufacturedStat { get; private set; }
    public IReadOnlyList<Stat> CartridgeStats { get; private set; } = [];
    public IReadOnlyList<StatGroup> CartridgeGroups { get; private set; } = [];
    public IReadOnlyList<MamRow> MamAttributes { get; private set; } = [];
    public bool HasMamAttributes => MamAttributes.Count > 0;
    public IReadOnlyList<Stat> DriveStats { get; private set; } = [];
    public IReadOnlyList<Stat> DriveIdStats { get; private set; } = [];   // serial + firmware, two columns
    public IReadOnlyList<StatGroup> DriveGroups { get; private set; } = [];


    private void RebuildDashboard()
    {
        var c = LastCart;
        var mam = c?.MamByPartition.FirstOrDefault() ?? new Dictionary<ushort, byte[]>();
        string? T(ushort id) => NativeTape.MamText(mam, id);
        ulong? N(ushort id) => NativeTape.MamNumber(mam, id);
        string? Count(ushort id) => N(id)?.ToString("N0");
        string? MiB(ushort id) => N(id) is { } v ? SchemaSnapshot.FormatSize((long)v << 20) : null;
        string? Bytes(ulong? v) => v is { } b ? SchemaSnapshot.FormatSize((long)b) : null;
        string? Hex(ushort id) => mam.TryGetValue(id, out var v) ? Convert.ToHexString(v) : null;
        string? Date(ushort id) => T(id) is { Length: >= 8 } d
            ? $"{d[..4]}-{d[4..6]}-{d[6..8]}" + (d.Length >= 12 ? $" {d[8..10]}:{d[10..12]}" : "") : T(id);
        string? Loaded(ushort id) => mam.TryGetValue(id, out var v) && v.Length >= 8   // vendor(8) + serial
            && $"{Ascii(v[..8])} {Ascii(v[8..])}".Trim() is { Length: > 0 } s ? s : null;
        static string Ascii(byte[] b) => System.Text.Encoding.ASCII.GetString(b).Trim('\0', ' ');
        static string OnOff(bool b) => b ? "On" : "Off";
        static List<Stat> L(params (string Label, string? Value)[] items) =>
            items.Where(i => !string.IsNullOrEmpty(i.Value)).Select(i => new Stat(i.Label, i.Value!)).ToList();
        Stat? Tile(string label, string? value) => HasCartridge ? L((label, value)).FirstOrDefault() : null;

        // per partition (MAM 0x0001 / 0x0000): maximum and remaining capacity, bytes
        var parts = (c?.MamByPartition ?? [])
            .Select(pm => (Cap: NativeTape.MamNumber(pm, 0x0001), Rem: NativeTape.MamNumber(pm, 0x0000) ?? 0))
            .Where(p => p.Cap != null)
            .Select(p => (Cap: (long)p.Cap!.Value << 20, Rem: (long)p.Rem << 20))
            .ToList();

        // capacity: live from a mounted volume, else the per-partition MAM figures
        (_capacity, _free) = _phase == MountPhase.Mounted && _state.Usage is { } live ? live : (0, 0);
        if (_capacity == 0)
            (_capacity, _free) = (parts.Sum(p => p.Cap), parts.Sum(p => p.Rem));

        string type = (c?.LtoGeneration ?? "LTO") + N(0x0408) switch
        {
            0x80 => " WORM",
            0x01 => " Cleaning",
            _ => "",
        };
        var name = Tile("Name", c?.VolumeName ?? Mapping?.VolumeName);
        var maker = Tile("Manufacturer", T(0x0400));
        var made = Tile("Manufactured", Date(0x0406));
        var cartStats = !HasCartridge ? new List<Stat>() : L(
            ("Type", type),
            ("Format", c?.State == CartridgeState.NotLtfs ? "Unformatted"
                : $"LTFS {c?.FormatVersion ?? Mapping?.FormatVersion}".Trim()),
            ("Write protect", c == null ? null : c.WriteProtected ? "Protected" : "Writable"),
            ("Serial", T(0x0401)));

        var groups = !HasCartridge ? new List<StatGroup>() : new List<StatGroup>
        {
            new("Identity", L(
                ("Barcode", T(0x0806)),
                ("Owning host", T(0x0807)),
                ("Media pool", T(0x0808)),
                ("Medium GUID", Hex(0x0820)))),
            new("Usage history", L(
                ("Loads", Count(0x0003)),
                ("Initializations", Count(0x0007)),
                ("Written (lifetime)", MiB(0x0220)),
                ("Read (lifetime)", MiB(0x0221)),
                ("Written (last load)", MiB(0x0222)),
                ("Read (last load)", MiB(0x0223)),
                ("Last written", Date(0x0804))), MaxColumns: 2,
                Tail: L(
                ("Last load", Loaded(0x020A)),
                ("Load before", Loaded(0x020B)),
                ("2 loads before", Loaded(0x020C)),
                ("3 loads before", Loaded(0x020D)))),
            new("Physical", L(
                ("Tape length", N(0x0402) is { } len ? $"{len:N0} m" : null),
                ("Tape width", N(0x0403) is { } w ? $"{w / 10.0:0.0} mm" : null),
                ("Medium density", N(0x0405) is { } md ? $"0x{md:X2}" : null),
                ("Formatted density", N(0x0006) is { } fd ? $"0x{fd:X2}" : null),
                ("Assigning org.", T(0x0404) ?? T(0x0005)),
                ("MAM size", Bytes(N(0x0407))),
                ("MAM free", Bytes(N(0x0004))),
                ("Head position", c?.Position is { } pos
                    ? (pos.Partition > 0 ? $"Partition {pos.Partition - 1}, " : "") + $"block {pos.Block:N0}" : null))),
            new("Application", L(
                ("Vendor", T(0x0800)),
                ("Name", T(0x0801)),
                ("Version", T(0x0802)),
                ("Format version", T(0x080B)),
                ("Media pool GUID", Hex(0x0821)))),
            new("Partitions", L(parts.Select((p, i) => ($"Partition {i}",
                (string?)$"{SchemaSnapshot.FormatSize(p.Rem)} free of {SchemaSnapshot.FormatSize(p.Cap)}")).ToArray()),
                MaxColumns: 2),
        };
        groups.RemoveAll(g => g.Items.Count == 0 && g.TailItems.Count == 0);

        // every attribute of every partition; ids below 0x0400 are per partition,
        // the rest (medium, host) are the same in each, so listed once
        var byPart = c?.MamByPartition ?? [];
        var raw = byPart.SelectMany((pm, part) => pm
                .Where(p => part == 0 || p.Key < 0x0400)
                .Select(p => (Part: part, p.Key, p.Value)))
            .OrderBy(a => a.Key).ThenBy(a => a.Part)
            .Select(a => new MamRow(
                $"0x{a.Key:X4}",
                MamNames.TryGetValue(a.Key, out var n) ? n : "Vendor/unknown",
                a.Key < 0x0400 ? a.Part.ToString() : "all",
                $"{a.Value.Length:N0} B",
                a.Value.All(b => b is 0 or >= 0x20 and < 0x7F) && a.Value.Any(b => b != 0)
                    ? Ascii(a.Value)
                    : a.Value.Length <= 8 ? $"{a.Value.Aggregate(0UL, (x, b) => x << 8 | b):N0}" : Convert.ToHexString(a.Value),
                Convert.ToHexString(a.Value)))
            .ToList();

        var d = Drive;
        var dp = _driveParams;
        var enc = _encryption;
        var driveStats = L(
            ("Vendor", d.Vendor),
            ("Model", d.Product),
            ("Generation", d.GenerationText),
            ("Interface", d.Bus));
        var driveIdStats = L(
            ("Serial", d.Serial),
            ("Firmware", d.Revision));
        var driveGroups = new List<StatGroup>
        {
            new("Device", L(
                ("Path", $@"\\.\{d.Device}"),
                ("Drive number", $"#{d.Number}"),
                ("Maximum partitions", dp?.MaximumPartitionCount.ToString()),
                ("EOT warning zone", dp == null ? null : $"{dp.EotWarningZoneSize:N0} B"))),
            new("Block size", L(
                ("Mode", c?.BlockSize is { } bs ? bs == 0 ? "Variable" : $"Fixed, {bs:N0} B" : null),
                ("Minimum", dp == null ? null : $"{dp.MinimumBlockSize:N0} B"),
                ("Maximum", dp == null ? null : $"{dp.MaximumBlockSize:N0} B"),
                ("Default", dp == null ? null : $"{dp.DefaultBlockSize:N0} B"))),
            new("Features", L(
                ("Hardware compression", dp == null ? null : OnOff(dp.Compression)),
                ("ECC", dp == null ? null : OnOff(dp.Ecc)),
                ("Encryption", enc == null ? null : enc.Algorithms.Count == 0 ? "Not supported"
                    : string.Join(", ", enc.Algorithms.Select(a => a == 0x00010014 ? "AES-256-GCM" : $"0x{a:X8}"))),
                ("Key management", enc?.ConfigPrevented switch { 1 => "Application", 2 => "Library / system", _ => null }))),
        };
        driveGroups.RemoveAll(g => g.Items.Count == 0);

        // only hand the repeaters new lists when something changed (keeps selection/scroll)
        string sig = string.Join("\n", new[] { name, maker, made }.OfType<Stat>().Concat(cartStats)
            .Concat(groups.SelectMany(g => g.Items.Concat(g.TailItems)))
            .Concat(driveStats).Concat(driveIdStats).Concat(driveGroups.SelectMany(g => g.Items)).Cast<object>().Concat(raw));
        if (sig == _sig) return;
        _sig = sig;
        (NameStat, ManufacturerStat, ManufacturedStat, CartridgeStats, CartridgeGroups, MamAttributes, DriveStats, DriveIdStats, DriveGroups) =
            (name, maker, made, cartStats, groups, raw, driveStats, driveIdStats, driveGroups);
    }

    /// <summary>SPC-4 / LTO MAM attribute names, for the raw attribute list.</summary>
    private static readonly Dictionary<ushort, string> MamNames = new()
    {
        [0x0000] = "Remaining capacity in partition (MiB)",
        [0x0001] = "Maximum capacity in partition (MiB)",
        [0x0002] = "TapeAlert flags",
        [0x0003] = "Load count",
        [0x0004] = "MAM space remaining (bytes)",
        [0x0005] = "Assigning organization",
        [0x0006] = "Formatted density code",
        [0x0007] = "Initialization count",
        [0x0008] = "Volume identifier",
        [0x0009] = "Volume change reference",
        [0x020A] = "Device at last load",
        [0x020B] = "Device at load -1",
        [0x020C] = "Device at load -2",
        [0x020D] = "Device at load -3",
        [0x0220] = "MiB written in medium life",
        [0x0221] = "MiB read in medium life",
        [0x0222] = "MiB written in current/last load",
        [0x0223] = "MiB read in current/last load",
        [0x0224] = "Logical position of first encrypted block",
        [0x0225] = "Logical position of first unencrypted block after first encrypted block",
        [0x0340] = "Medium usage history",
        [0x0341] = "Partition usage history",
        [0x0400] = "Medium manufacturer",
        [0x0401] = "Medium serial number",
        [0x0402] = "Medium length (m)",
        [0x0403] = "Medium width (0.1 mm)",
        [0x0404] = "Assigning organization",
        [0x0405] = "Medium density code",
        [0x0406] = "Medium manufacture date",
        [0x0407] = "MAM capacity (bytes)",
        [0x0408] = "Medium type",
        [0x0409] = "Medium type information",
        [0x040A] = "Numeric medium serial number",
        [0x0800] = "Application vendor",
        [0x0801] = "Application name",
        [0x0802] = "Application version",
        [0x0803] = "User medium text label",
        [0x0804] = "Date and time last written",
        [0x0805] = "Text localization identifier",
        [0x0806] = "Barcode",
        [0x0807] = "Owning host textual name",
        [0x0808] = "Media pool",
        [0x0809] = "Partition user text label",
        [0x080A] = "Load/unload at partition",
        [0x080B] = "Application format version",
        [0x080C] = "Volume coherency information",
        [0x0820] = "Medium globally unique identifier",
        [0x0821] = "Media pool globally unique identifier",
    };

    // ---- helpers -----------------------------------------------------------

    /// <summary>Replace the free-letter list, preserving the selection when possible.</summary>
    public void SetLetters(IReadOnlyList<string> letters, string? prefer = null)
    {
        if (prefer == null && letters.SequenceEqual(Letters)) return;   // don't disturb an open dropdown
        string? keep = prefer ?? SelectedLetter;
        Letters.Clear();
        foreach (var l in letters) Letters.Add(l);
        int idx = keep != null ? letters.ToList().IndexOf(keep) : -1;
        if (idx < 0) idx = letters.ToList().IndexOf("T:");
        if (idx < 0 && letters.Count > 0) idx = letters.Count - 1;
        SelectedLetterIndex = idx;
    }

    /// <summary>Lock the letter list to the mounted letter.</summary>
    public void SetMountedLetter(string letter)
    {
        Letters.Clear();
        Letters.Add(letter);
        SelectedLetterIndex = 0;
    }

    /// <summary>Refresh every binding (an empty name means "all").</summary>
    private void Changed() => Raise(string.Empty);

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
