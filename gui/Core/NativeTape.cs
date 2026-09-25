using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LTOG.Gui.Core;

public record TapeDrive(string Device, string Vendor, string Product, string Serial,
    string Revision = "", string Bus = "")
{
    /// <summary>1-based drive number: TAPE0 is #1.</summary>
    public int Number => int.Parse(Device[4..]) + 1;

    // ponytail: drive generation guessed from the product id (HP "Ultrium 5-SCSI",
    // IBM "ULT3580-TD5", Quantum "ULTRIUM-HH8"); REPORT DENSITY SUPPORT if a model slips through.
    public string GenerationText =>
        Regex.Match(Product, @"(?:ULTRIUM|TD|HH|LTO)[\s-]*(\d{1,2})", RegexOptions.IgnoreCase) is { Success: true } m
            ? $"LTO-{m.Groups[1].Value}" : "";

    /// <summary>"#1 HP LTO-5"</summary>
    public string TabTitle => $"#{Number} {Vendor} {GenerationText}".Trim();

    /// <summary>"TAPE0 — HP Ultrium 5-SCSI (HUJ5153GNF)"</summary>
    public string Display =>
        string.IsNullOrEmpty(Serial)
            ? $"{Device} — {Vendor} {Product}"
            : $"{Device} — {Vendor} {Product} ({Serial})";
    public override string ToString() => Display;
}

public enum CartridgeState { DriveInUse, NotReady, NoMedia, NotLtfs, Ltfs }

/// <param name="LtoGeneration">e.g. "LTO-5", from the medium's density code; null if unknown.</param>
/// <param name="WriteProtected">true when the cartridge's physical write-protect tab is engaged.</param>
public record CartridgeInfo(CartridgeState State, string? VolumeName, string? FormatVersion,
    string? LtoGeneration = null, bool WriteProtected = false)
{
    /// <summary>Every MAM attribute of every partition (index = partition number), by attribute id.</summary>
    public IReadOnlyList<IReadOnlyDictionary<ushort, byte[]>> MamByPartition { get; init; } = [];
    public DriveParams? DriveParams { get; init; }
    public EncryptionInfo? Encryption { get; init; }
    /// <summary>Current block length from the MODE SENSE block descriptor; 0 = variable.</summary>
    public uint? BlockSize { get; init; }
    /// <summary>Head position (GetTapePosition): partition is 1-based, 0 if the medium isn't partitioned.</summary>
    public (uint Partition, ulong Block)? Position { get; init; }
}

/// <summary>GetTapeParameters(GET_TAPE_DRIVE_INFORMATION).</summary>
public record DriveParams(bool Ecc, bool Compression,
    uint DefaultBlockSize, uint MaximumBlockSize, uint MinimumBlockSize, uint MaximumPartitionCount,
    uint EotWarningZoneSize);

/// <summary>SECURITY PROTOCOL IN, tape data encryption capabilities (SSC-4 page 0x0001).</summary>
/// <param name="Algorithms">security algorithm codes the drive can encrypt or decrypt with</param>
/// <param name="ConfigPrevented">CFG_P: 0 unknown, 1 application may configure, 2 prevented (library/system managed)</param>
public record EncryptionInfo(IReadOnlyList<uint> Algorithms, byte ConfigPrevented);

/// <summary>
/// Native tape device access: enumeration via IOCTL_STORAGE_QUERY_PROPERTY and
/// physical load/eject via the Win32 PrepareTape API (no SCSI pass-through needed).
/// </summary>
public static class NativeTape
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint TAPE_LOAD = 0;
    private const uint TAPE_UNLOAD = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint PrepareTape(SafeFileHandle handle, uint operation, bool immediate);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetTapeStatus(SafeFileHandle handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetTapePosition(SafeFileHandle handle, uint positionType,
        out uint partition, out uint offsetLow, out uint offsetHigh);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetTapeParameters(SafeFileHandle handle, uint operation, ref uint size, byte[] buf);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        ref SptdWithSense inBuf, int inLen, ref SptdWithSense outBuf, int outLen,
        out int returned, IntPtr overlapped);

    private static SafeFileHandle Open(string device) =>
        CreateFile($@"\\.\{device}", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Zero-access open: enough for IOCTL_STORAGE_QUERY_PROPERTY and succeeds
    /// even while a mount holds the device locked (FSCTL_LOCK_VOLUME).
    /// </summary>
    private static SafeFileHandle OpenQuery(string device) =>
        CreateFile($@"\\.\{device}", 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>Probe \\.\TAPE0 .. \\.\TAPE9 and read vendor/product/serial.</summary>
    public static List<TapeDrive> Enumerate(IActivityLog? log = null)
    {
        var drives = new List<TapeDrive>();
        for (int i = 0; i < 10; i++)
        {
            string dev = $"TAPE{i}";
            using var h = OpenQuery(dev);
            if (h.IsInvalid)
                continue;

            // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
            var query = new byte[12];
            var buf = new byte[1024];
            string vendor = "", product = "", serial = "", revision = "", bus = "";
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length,
                                buf, buf.Length, out _, IntPtr.Zero))
            {
                vendor = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 12));
                product = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 16));
                revision = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 20));
                serial = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 24));
                int b = BitConverter.ToInt32(buf, 28);   // STORAGE_BUS_TYPE
                bus = b >= 0 && b < BusTypes.Length ? BusTypes[b] : $"Bus type {b}";
            }
            drives.Add(new TapeDrive(dev, vendor, product, serial, revision, bus));
        }
        log?.Read("Enumerate tape drives",
            @"IOCTL_STORAGE_QUERY_PROPERTY  \\.\TAPE0 .. \\.\TAPE9",
            drives.Count == 0 ? new[] { "No tape drives detected." }
                              : drives.Select(d => d.Display).ToArray(),
            key: "tape-drives");
        return drives;
    }

    private static readonly string[] BusTypes =
    {
        "Unknown", "SCSI", "ATAPI", "ATA", "IEEE 1394", "SSA", "Fibre Channel", "USB", "RAID",
        "iSCSI", "SAS", "SATA", "SD", "MMC", "Virtual", "File-backed virtual", "Storage Spaces", "NVMe",
    };

    private static string ReadAnsiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    /// <summary>Load (thread) the cartridge currently in the drive.</summary>
    public static void Load(string device) => Prepare(device, TAPE_LOAD);

    /// <summary>Unload and eject the cartridge.</summary>
    public static void Eject(string device) => Prepare(device, TAPE_UNLOAD);

    private static void Prepare(string device, uint op)
    {
        using var h = Open(device);
        if (h.IsInvalid)
            throw new IOException($@"Cannot open \\.\{device} (is it in use by a mount?)");
        uint err = PrepareTape(h, op, false);
        if (err != 0)
            throw new IOException($"PrepareTape failed (win32 error {err})");
    }

    // ---------------------------------------------------------------- MAM

    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    private const byte SCSIOP_MODE_SENSE10 = 0x5A;         // physical WP + medium density code
    private const ushort MAM_APP_FORMAT_VERSION = 0x080B;  // present => LTFS-formatted
    private const ushort MAM_USR_MED_TXT_LABEL = 0x0803;   // LTFS volume name (160 bytes)
    private const ushort MAM_MAX_CAPACITY = 0x0001;        // per partition, MiB
    private const ushort MAM_MEDIUM_DENSITY = 0x0405;
    private const uint ERROR_NO_MEDIA_IN_DRIVE = 1112;
    private const uint ERROR_MEDIA_CHANGED = 1110;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SptdWithSense
    {
        public ScsiPassThroughDirect Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Sense;
    }

    /// <summary>One data-in SCSI command through pass-through; the response, or null on any failure.</summary>
    private static byte[]? ScsiIn(SafeFileHandle h, byte[] cdb, int bufLen)
    {
        IntPtr data = Marshal.AllocHGlobal(bufLen);
        try
        {
            var s = new SptdWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = (byte)cdb.Length,
                    SenseInfoLength = 32,
                    DataIn = 1, // SCSI_IOCTL_DATA_IN
                    DataTransferLength = (uint)bufLen,
                    TimeOutValue = 30,
                    DataBuffer = data,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[32],
            };
            cdb.CopyTo(s.Sptd.Cdb, 0);
            int size = Marshal.SizeOf<SptdWithSense>();
            if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref s, size, ref s, size,
                                 out _, IntPtr.Zero) || s.Sptd.ScsiStatus != 0)
                return null;
            var buf = new byte[bufLen];
            Marshal.Copy(data, buf, 0, bufLen);
            return buf;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>SECURITY PROTOCOL IN (0xA2), protocol 0x20 (tape data encryption), one page.</summary>
    private static byte[]? ReadEncryptionPage(SafeFileHandle h, ushort page)
    {
        const int bufLen = 1024;
        return ScsiIn(h, new byte[]
        {
            0xA2, 0x20, (byte)(page >> 8), (byte)page, 0, 0,
            0, 0, bufLen >> 8, bufLen & 0xFF, 0, 0,   // allocation length (BE, bytes 6..9)
        }, bufLen);
    }

    /// <summary>Drive encryption capabilities. No tape motion; null if unsupported.</summary>
    private static EncryptionInfo? ReadEncryption(SafeFileHandle h)
    {
        if (ReadEncryptionPage(h, 0x0001) is not { } cap) return null;
        // capabilities: [4] CFG_P (bits 1-0), algorithm descriptors from [20]; each is
        // index(1) rsvd(1) length(2), [4] DECRYPT_C (bits 3-2) / ENCRYPT_C (bits 1-0), code at [20..23]
        var algorithms = new List<uint>();
        int end = Math.Min(cap.Length, 4 + ((cap[2] << 8) | cap[3]));
        for (int i = 20; i + 24 <= end; i += 4 + ((cap[i + 2] << 8) | cap[i + 3]))
            if ((cap[i + 4] & 0x0F) != 0)
                algorithms.Add((uint)(cap[i + 20] << 24 | cap[i + 21] << 16 | cap[i + 22] << 8 | cap[i + 23]));
        return new EncryptionInfo(algorithms, (byte)(cap[4] & 0x03));
    }

    /// <summary>
    /// SCSI READ ATTRIBUTE from the cartridge memory (MAM): every attribute of
    /// one partition, by id. No tape motion. Empty if the partition doesn't exist.
    /// Pages on (from the next id) when the response doesn't fit the buffer.
    /// </summary>
    private static Dictionary<ushort, byte[]> ReadMam(SafeFileHandle h, byte partition)
    {
        const int bufLen = 0x4000;   // LTO MAM is 4-16 KiB in total
        var attrs = new Dictionary<ushort, byte[]>();
        IntPtr data = Marshal.AllocHGlobal(bufLen);
        try
        {
            for (int first = 0; first <= 0xFFFF;)
            {
                var s = new SptdWithSense
                {
                    Sptd = new ScsiPassThroughDirect
                    {
                        Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                        CdbLength = 16,
                        SenseInfoLength = 32,
                        DataIn = 1, // SCSI_IOCTL_DATA_IN
                        DataTransferLength = bufLen,
                        TimeOutValue = 30,
                        DataBuffer = data,
                        SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                        Cdb = new byte[16],
                    },
                    Sense = new byte[32],
                };
                s.Sptd.Cdb[0] = 0x8C;                        // READ ATTRIBUTE
                s.Sptd.Cdb[1] = 0x00;                        // service action: attribute values
                s.Sptd.Cdb[7] = partition;
                s.Sptd.Cdb[8] = (byte)(first >> 8);          // first attribute id (BE)
                s.Sptd.Cdb[9] = (byte)(first & 0xFF);
                s.Sptd.Cdb[12] = (byte)(bufLen >> 8);        // allocation length (BE, bytes 10..13)
                s.Sptd.Cdb[13] = (byte)(bufLen & 0xFF);

                int size = Marshal.SizeOf<SptdWithSense>();
                if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref s, size, ref s, size,
                                     out _, IntPtr.Zero) || s.Sptd.ScsiStatus != 0)
                    break;

                var buf = new byte[bufLen];
                Marshal.Copy(data, buf, 0, bufLen);
                // Response: 4-byte available length, then entries of id(2) fmt(1) len(2) value
                long avail = (long)buf[0] << 24 | (long)buf[1] << 16 | (long)buf[2] << 8 | buf[3];
                int end = (int)Math.Min(bufLen, 4 + avail);
                int last = -1;
                for (int i = 4; i + 5 <= end;)
                {
                    int len = (buf[i + 3] << 8) | buf[i + 4];
                    if (i + 5 + len > end) break;
                    last = (buf[i] << 8) | buf[i + 1];
                    attrs[(ushort)last] = buf[(i + 5)..(i + 5 + len)];
                    i += 5 + len;
                }
                if (4 + avail <= bufLen || last < first) break;   // all read, or no progress
                first = last + 1;
            }
            return attrs;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Big-endian unsigned MAM value, or null if absent.</summary>
    public static ulong? MamNumber(IReadOnlyDictionary<ushort, byte[]> mam, ushort id) =>
        mam.TryGetValue(id, out var v) && v.Length is > 0 and <= 8
            ? v.Aggregate(0UL, (a, b) => a << 8 | b) : null;

    /// <summary>Trimmed ASCII/UTF-8 MAM value, or null if absent or blank.</summary>
    public static string? MamText(IReadOnlyDictionary<ushort, byte[]> mam, ushort id) =>
        mam.TryGetValue(id, out var v) && System.Text.Encoding.UTF8.GetString(v).Trim('\0', ' ') is { Length: > 0 } t
            ? t : null;

    private static DriveParams? ReadDriveParams(SafeFileHandle h)
    {
        var b = new byte[32];
        uint size = (uint)b.Length;
        if (GetTapeParameters(h, 1, ref size, b) != 0) return null;   // GET_TAPE_DRIVE_INFORMATION
        return new DriveParams(b[0] != 0, b[1] != 0,
            BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12),
            BitConverter.ToUInt32(b, 16), BitConverter.ToUInt32(b, 28));
    }

    /// <summary>
    /// SCSI MODE SENSE(10) with the block descriptor. The mode-parameter header
    /// carries the physical Write-Protect bit (device-specific parameter, bit 7)
    /// and the 8-byte block descriptor that follows it carries the medium's
    /// density code, which identifies the LTO generation. No tape motion.
    /// Returns null if the command fails.
    /// </summary>
    private static (bool WriteProtected, byte DensityCode, uint? BlockLength)? ReadModeParams(SafeFileHandle h)
    {
        const int bufLen = 64;
        IntPtr data = Marshal.AllocHGlobal(bufLen);
        try
        {
            var s = new SptdWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = 10,
                    SenseInfoLength = 32,
                    DataIn = 1, // SCSI_IOCTL_DATA_IN
                    DataTransferLength = bufLen,
                    TimeOutValue = 30,
                    DataBuffer = data,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[32],
            };
            s.Sptd.Cdb[0] = SCSIOP_MODE_SENSE10;   // MODE SENSE(10)
            s.Sptd.Cdb[1] = 0x00;                  // DBD=0: include the block descriptor
            s.Sptd.Cdb[2] = 0x3F;                  // PC=current values, page 0x3F (all pages)
            s.Sptd.Cdb[7] = (byte)(bufLen >> 8);   // allocation length (BE)
            s.Sptd.Cdb[8] = (byte)(bufLen & 0xFF);

            int size = Marshal.SizeOf<SptdWithSense>();
            if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref s, size, ref s, size,
                                 out _, IntPtr.Zero) || s.Sptd.ScsiStatus != 0)
                return null;

            var buf = new byte[bufLen];
            Marshal.Copy(data, buf, 0, bufLen);
            // Header: [3] device-specific parameter (bit 7 = WP),
            //         [6..7] block descriptor length. The block descriptor follows the
            //         8-byte header; its first byte ([8]) is the density code and
            //         [13..15] the block length (0 = variable).
            bool wp = (buf[3] & 0x80) != 0;
            int bdLen = (buf[6] << 8) | buf[7];
            byte density = bdLen >= 8 ? buf[8] : (byte)0;
            uint? blockLen = bdLen >= 8 ? (uint)(buf[13] << 16 | buf[14] << 8 | buf[15]) : null;
            return (wp, density, blockLen);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Map an LTO medium density code to its generation name.</summary>
    public static string? LtoGenerationName(byte density) => density switch
    {
        0x40 => "LTO-1",
        0x42 => "LTO-2",
        0x44 => "LTO-3",
        0x46 => "LTO-4",
        0x58 => "LTO-5",
        0x5A => "LTO-6",
        0x5C => "LTO-7",
        0x5D => "LTO-8",   // Type M (M8): LTO-7 media formatted at LTO-8 density
        0x5E => "LTO-8",
        0x60 => "LTO-9",
        _ => null,
    };

    /// <summary>
    /// Identify the cartridge in a drive without mounting: media presence via
    /// GetTapeStatus, LTFS detection and volume name via MAM attributes.
    /// </summary>
    public static CartridgeInfo ReadCartridgeInfo(string device, IActivityLog? log = null)
    {
        var info = ReadCartridgeInfoCore(device);
        log?.Read($"Read cartridge — {device}",
            $@"GetTapeStatus + GetTapeParameters + MODE SENSE + SECURITY PROTOCOL IN + READ ATTRIBUTE (all MAM)  \\.\{device}",
            new[] { DescribeCartridge(info) },
            key: $"cartridge:{device}");
        return info;
    }

    private static string DescribeCartridge(CartridgeInfo info)
    {
        string extra = (info.LtoGeneration is { } g ? $" [{g}]" : "")
                     + (info.WriteProtected ? " [Write-Protected]" : "");
        return info.State switch
        {
            CartridgeState.DriveInUse => "Drive busy — cannot open (in use by another program or a mount).",
            CartridgeState.NotReady => "Drive not ready.",
            CartridgeState.NoMedia => "No cartridge present.",
            CartridgeState.NotLtfs => "Cartridge present — not LTFS formatted." + extra,
            CartridgeState.Ltfs => $"LTFS cartridge: {info.VolumeName ?? "(unlabelled)"}, format {info.FormatVersion ?? "?"}" + extra,
            _ => info.State.ToString(),
        };
    }

    private static CartridgeInfo ReadCartridgeInfoCore(string device)
    {
        using var h = Open(device);
        if (h.IsInvalid)
            return new CartridgeInfo(CartridgeState.DriveInUse, null, null);

        var dp = ReadDriveParams(h);   // works with or without media
        var enc = ReadEncryption(h);
        uint status = GetTapeStatus(h);
        if (status == ERROR_MEDIA_CHANGED)
            status = GetTapeStatus(h);          // first call after a change reports it once
        if (status == ERROR_NO_MEDIA_IN_DRIVE)
            return new CartridgeInfo(CartridgeState.NoMedia, null, null) { DriveParams = dp, Encryption = enc };
        if (status != 0)
            return new CartridgeInfo(CartridgeState.NotReady, null, null) { DriveParams = dp, Encryption = enc };

        // Physical write-protect and LTO generation come from one MODE SENSE(10).
        // They apply whether or not the cartridge carries an LTFS volume.
        var mp = ReadModeParams(h);
        bool wp = mp?.WriteProtected ?? false;
        string? gen = mp is { } p ? LtoGenerationName(p.DensityCode) : null;

        var mam = ReadMam(h, 0);
        gen ??= MamNumber(mam, MAM_MEDIUM_DENSITY) is { } d ? LtoGenerationName((byte)d) : null;
        var mams = new List<IReadOnlyDictionary<ushort, byte[]>>();
        for (byte i = 0; i < 4; i++)   // LTO: at most 4 partitions
        {
            var pm = i == 0 ? mam : ReadMam(h, i);
            if (pm.Count > 0) mams.Add(pm);
            if (MamNumber(pm, MAM_MAX_CAPACITY) == null) break;
        }

        // present => LTFS-formatted
        string? version = MamText(mam, MAM_APP_FORMAT_VERSION);
        return new CartridgeInfo(version == null ? CartridgeState.NotLtfs : CartridgeState.Ltfs,
            version == null ? null : MamText(mam, MAM_USR_MED_TXT_LABEL), version, gen, wp)
        {
            MamByPartition = mams, DriveParams = dp, Encryption = enc, BlockSize = mp?.BlockLength,
            Position = GetTapePosition(h, 1, out uint part, out uint lo, out uint hi) == 0   // TAPE_LOGICAL_POSITION, no motion
                ? (part, (ulong)hi << 32 | lo) : null,
        };
    }

    /// <summary>Unused drive letters D:..Z: (A/B/C never offered).</summary>
    public static List<string> UnusedLetters()
    {
        uint mask = GetLogicalDrives();
        var free = new List<string>();
        for (int i = 3; i < 26; i++)
            if ((mask & (1u << i)) == 0)
                free.Add($"{(char)('A' + i)}:");
        return free;
    }
}
