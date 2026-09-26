namespace LTOG.Core;

/// <summary>Locates the WinLtfs engine directory (ltfs.exe + ltfs.conf + plugins),
/// which sits in a winltfs\ subfolder next to the GUI in a packaged install.</summary>
public static class LtfsEnv
{
    public static string? DistPath { get; private set; }

    public static string LtfsExe => Path.Combine(DistPath!, "ltfs.exe");
    public static string MkltfsExe => Path.Combine(DistPath!, "mkltfs.exe");
    public static string UnltfsExe => Path.Combine(DistPath!, "unltfs.exe");
    public static string LtfsckExe => Path.Combine(DistPath!, "ltfsck.exe");
    public static string LtfsConf => Path.Combine(DistPath!, "ltfs.conf");
    /// <summary>Forward-slash form for FUSE -o values (backslash is an escape char there).</summary>
    public static string LtfsConfFuse => LtfsConf.Replace('\\', '/');

    public static bool Resolve(string? settingsOverride)
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var candidates = new[]
        {
            settingsOverride,
            Path.Combine(exeDir, "winltfs"),   // packaged: GUI in dist\, engine in dist\winltfs\
            exeDir,                            // engine staged flat beside the GUI
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c)
                && File.Exists(Path.Combine(c, "ltfs.exe"))
                && File.Exists(Path.Combine(c, "ltfs.conf")))
            {
                DistPath = c;
                return true;
            }
        }
        return false;
    }
}
