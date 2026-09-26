using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTOG.Gui.Core;

public class PersistedMapping
{
    public string Letter { get; set; } = "T:";
    public string Device { get; set; } = "TAPE0";
    public bool ReadOnly { get; set; }
}

public class Settings
{
    public bool CaptureIndex { get; set; } = true;
    public string WorkFolder { get; set; } = @"C:\tmp\ltfs";
    public bool OverrideSyncPolicy { get; set; } = false;
    /// <summary>0 = when volume is dismounted, 1 = periodically every N minutes</summary>
    public int SyncPolicyMode { get; set; } = 1;
    public int SyncPeriodMinutes { get; set; } = 5;

    // --- applies to every drive ---
    public bool EjectAfterUnmount { get; set; }
    public bool RemountAtStartup { get; set; }   // remount every persisted mapping at sign-in

    // --- advanced mount options ---
    public static string DefaultLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LTOG");
    public bool AppendOnly { get; set; }

    // Index-partition placement (mount-time override of the formatted policy).
    public bool OverrideIndexPlacement { get; set; }
    public int IndexMaxSize { get; set; } = 1;
    public int IndexSizeUnit { get; set; } = 1;
    public string IndexNamePatterns { get; set; } = "";
    public string LogDirectory { get; set; } = DefaultLogDirectory;
    /// <summary>0 = normal, 1 = verbose (trace), 2 = full trace (fulltrace)</summary>
    public int Verbosity { get; set; }

    public string? DistPath { get; set; }                  // optional override of auto-detection

    public string LastTab { get; set; } = "";   // last selected drive tab: "TAPE0"..
    public int IndexSort { get; set; } = 0;   // 0 name A-Z, 1 name Z-A, 2 newest, 3 oldest

    // last window geometry (null until first close)
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public List<PersistedMapping> Mappings { get; set; } = new();

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LTOG");
    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (string.IsNullOrWhiteSpace(s.LogDirectory))
                    s.LogDirectory = DefaultLogDirectory;
                return s;
            }
        }
        catch { /* corrupted settings -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>The autostart entry exists iff remount-at-startup is on and something is mounted.</summary>
    public void ApplyRunKey()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (RemountAtStartup && Mappings.Count > 0)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LTOG.exe");
            key.SetValue("LTOG", $"\"{exe}\" --remount");
        }
        else
        {
            key.DeleteValue("LTOG", false);
        }
    }

    /// <summary>The ltfs <c>-o rules=</c> value from the index-placement fields, or "" when off.</summary>
    public string ComposeIndexRules()
    {
        if (!OverrideIndexPlacement) return "";
        string unit = IndexSizeUnit switch { 0 => "K", 2 => "G", _ => "M" };
        string rules = $"size={Math.Max(1, IndexMaxSize)}{unit}";
        if (!string.IsNullOrWhiteSpace(IndexNamePatterns))
            rules += $"/name={IndexNamePatterns.Trim()}";
        return rules;
    }
}
