using Microsoft.Data.Sqlite;

namespace LTOG.Core;

/// <summary>
/// What the index snapshots can't tell: a tape's barcode, serial, generation and space, from its
/// cartridge memory (MAM) when last loaded. Partition sizes are raw MAM figures (MiB), so
/// a change to how space is worked out applies to old records too.
/// </summary>
/// <param name="Barcode">the LTFS barcode (MAM 0x0806), if one was set when formatting</param>
/// <param name="Serial">the cartridge's own serial number (MAM 0x0401)</param>
public record TapeRecord(string Uuid, string? Barcode, string Serial, string? Generation,
    long IndexMaxMiB, long IndexRemMiB, long DataMaxMiB, long DataRemMiB, DateTime SeenUtc)
{
    public long Capacity => LtfsSpace(IndexMaxMiB << 20, DataMaxMiB << 20, DataRemMiB << 20).Capacity;
    public long Free => LtfsSpace(IndexMaxMiB << 20, DataMaxMiB << 20, DataRemMiB << 20).Free;

    /// <summary>
    /// Space as LTFS reports it, in bytes: files go only in the data partition, and LTFS
    /// holds back half the index partition's size from it (ltfs_capacity_data_unlocked).
    /// </summary>
    public static (long Capacity, long Free) LtfsSpace(long indexMax, long dataMax, long dataRem) =>
        (dataMax - indexMax / 2, Math.Max(0, dataRem - indexMax / 2));
}

/// <summary>Every tape LTOG has seen, by LTFS volume UUID, in tapes.db next to settings.json.</summary>
public static class TapeDb
{
    public static string FilePath => Path.Combine(Settings.Dir, "tapes.db");

    private static Dictionary<string, TapeRecord>? _all;
    public static IReadOnlyDictionary<string, TapeRecord> All => _all ??= Load();

    public static TapeRecord? Get(string uuid) => All.GetValueOrDefault(uuid);

    public static void Save(TapeRecord r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO tape (uuid, barcode, serial, generation, index_max_mib, index_rem_mib,
                data_max_mib, data_rem_mib, seen_utc)
            VALUES ($uuid, $barcode, $serial, $gen, $imax, $irem, $dmax, $drem, $seen)
            """;
        cmd.Parameters.AddWithValue("$uuid", r.Uuid);
        cmd.Parameters.AddWithValue("$barcode", (object?)r.Barcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$serial", r.Serial);
        cmd.Parameters.AddWithValue("$gen", (object?)r.Generation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imax", r.IndexMaxMiB);
        cmd.Parameters.AddWithValue("$irem", r.IndexRemMiB);
        cmd.Parameters.AddWithValue("$dmax", r.DataMaxMiB);
        cmd.Parameters.AddWithValue("$drem", r.DataRemMiB);
        cmd.Parameters.AddWithValue("$seen", r.SeenUtc.ToString("O"));
        cmd.ExecuteNonQuery();
        (_all ??= Load())[r.Uuid] = r;
    }

    private static Dictionary<string, TapeRecord> Load()
    {
        var all = new Dictionary<string, TapeRecord>(StringComparer.OrdinalIgnoreCase);
        try { ReadAll(all); }
        catch { /* unreadable db -> no records; each tape is recorded again when next loaded */ }
        return all;
    }

    private static void ReadAll(Dictionary<string, TapeRecord> all)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT uuid, barcode, serial, generation, index_max_mib, index_rem_mib,
                data_max_mib, data_rem_mib, seen_utc FROM tape
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            all[r.GetString(0)] = new TapeRecord(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                DateTime.Parse(r.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static SqliteConnection Open()
    {
        Directory.CreateDirectory(Settings.Dir);
        var c = new SqliteConnection($"Data Source={FilePath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tape (
                uuid TEXT PRIMARY KEY COLLATE NOCASE,
                barcode TEXT,
                serial TEXT NOT NULL,
                generation TEXT,
                index_max_mib INTEGER NOT NULL,
                index_rem_mib INTEGER NOT NULL,
                data_max_mib INTEGER NOT NULL,
                data_rem_mib INTEGER NOT NULL,
                seen_utc TEXT NOT NULL)
            """;
        cmd.ExecuteNonQuery();
        return c;
    }
}
