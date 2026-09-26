using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LTOG.Core;

public class Mapping
{
    public string Letter { get; set; } = "T:";
    public string Device { get; set; } = "TAPE0";
    public string Description { get; set; } = "";
    public bool ReadOnly { get; set; }
    public int Pid { get; set; }
    public string? VolumeName { get; set; } // cartridge volume name (from cartridge MAM)
    public string? FormatVersion { get; set; } // LTFS format version (from cartridge MAM)

    /// <summary>The live ltfs.exe process (anchored so its Exited event still fires).</summary>
    internal Process? Proc { get; set; }
    /// <summary>The activity-log entry for this mount, completed when ltfs.exe exits.</summary>
    internal ILogScope? Scope { get; set; }
}

public static class MountManager
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroup);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);

    private delegate bool CtrlHandler(uint ctrlType);
    private static readonly CtrlHandler DetachOnCtrlC = _ => FreeConsole();

    /// <summary>
    /// Start ltfs.exe for a mapping, with the mount options from <paramref name="s"/>,
    /// and wait for the volume to come up (<paramref name="volumeUp"/>).
    /// </summary>
    public static async Task MountAsync(Mapping m, Settings s, IActivityLog log, Task volumeUp)
    {
        // Build the argument list once, so the logged command line is exactly
        // what we hand to the process.
        var args = new List<string> { $@"\\.\{m.Letter}", "-f" };
        void Opt(string o) { args.Add("-o"); args.Add(o); }
        Opt($"config_file={LtfsEnv.LtfsConfFuse}");
        Opt(DevNameOpt(m.Device));
        Opt(!s.OverrideSyncPolicy ? "sync_type=close"
            : s.SyncPolicyMode == 0 ? "sync_type=unmount"
            : $"sync_type=time@{s.SyncPeriodMinutes}");
        if (s.CaptureIndex)
        {
            Opt("capture_index");
            Opt($"work_directory={s.WorkFolder.Replace('\\', '/')}");
        }
        if (m.ReadOnly) Opt("ro");
        if (s.EjectAfterUnmount) Opt("eject");
        // Advanced options. The engine validates each (e.g. append-only is rejected
        // on drives that don't support it) and logs the reason to the mount entry.
        if (s.AppendOnly) Opt("scsi_append_only_mode=on");
        if (s.ComposeIndexRules() is { Length: > 0 } rules) Opt($"rules={rules}");
        if (!string.IsNullOrWhiteSpace(s.LogDirectory))
        {
            var logDir = s.LogDirectory.Trim();
            try { Directory.CreateDirectory(logDir); } catch { /* engine will report if unwritable */ }
            Opt($"log_directory={logDir.Replace('\\', '/')}");
        }
        if (s.Verbosity == 1) Opt("trace");
        else if (s.Verbosity == 2) Opt("fulltrace");

        var psi = new ProcessStartInfo(LtfsEnv.LtfsExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,            // hidden console: needed to deliver Ctrl+C later
            RedirectStandardError = true,     // LTFS logs on stderr
            WorkingDirectory = LtfsEnv.DistPath!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        string title = $"Mount {m.Letter} → {m.Device}" +
                       (string.IsNullOrEmpty(m.Description) ? "" : $" ({m.Description})");
        var scope = log.Begin(LogKind.Mount, title,
            CommandLine.Format(LtfsEnv.LtfsExe, args), LtfsEnv.DistPath);
        m.Scope = scope;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // ltfs.exe runs in the foreground for the whole mount lifetime; stream its
        // log to the entry and close the entry when it finally exits (at unmount).
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) scope.Line(e.Data); };
        p.Exited += (_, _) => { try { scope.Complete(p.ExitCode); } catch { scope.Complete(); } };
        p.Start();
        p.BeginErrorReadLine();
        m.Pid = p.Id;
        m.Proc = p;

        // Wait for the volume (tape index reads can take minutes)
        await Task.WhenAny(volumeUp, p.WaitForExitAsync(), Task.Delay(TimeSpan.FromMinutes(10)));

        if (p.HasExited)
            throw new IOException($"ltfs.exe exited with code {p.ExitCode} - see log");
        if (!volumeUp.IsCompleted)
            throw new TimeoutException("Volume did not come up within 10 minutes");

        scope.Line($"Volume mounted on {m.Letter} — WinFsp now serving requests.");
    }

    /// <summary>
    /// Read the live Windows volume label for a mounted letter — the real label
    /// WinFsp set from -o volname, read straight off the volume. Used when
    /// adopting a mount already running from a previous session.
    /// </summary>
    public static string? GetVolumeLabel(string letter)
    {
        try
        {
            var di = new DriveInfo(letter.TrimEnd(':'));
            if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                return di.VolumeLabel;
        }
        catch { /* drive not ready / not a real volume */ }
        return null;
    }

    /// <summary>
    /// Graceful unmount: deliver Ctrl+C to ltfs.exe's hidden console so it writes
    /// the final index and releases the drive. Never force-kills.
    /// </summary>
    public static async Task UnmountAsync(Mapping m, IActivityLog log)
    {
        Process? p = m.Proc;
        if (p == null) { try { p = Process.GetProcessById(m.Pid); } catch { /* already gone */ } }

        if (p != null && !p.HasExited)
        {
            bool sent = SignalCtrlC(m.Pid);
            if (sent)
                await Task.Run(() => p.WaitForExit(120_000));
            if (!sent || !p.HasExited)
            {
                log.Note($"{m.Letter} could not be unmounted (drive may be busy).", isError: true);
                return;
            }
        }

        // The process Exited handler normally closes the mount entry; complete it
        // here too as a fallback (no-op if already done) so it never stays "RUNNING".
        m.Scope?.Complete(p?.HasExited == true ? p.ExitCode : null);
        log.Note($"{m.Letter} unmounted.");
    }

    /// <summary>Deliver Ctrl+C to one ltfs.exe's hidden console. Returns whether it was sent.</summary>
    private static bool SignalCtrlC(int pid)
    {
        if (!AttachConsole(pid)) return false;
        SetConsoleCtrlHandler(DetachOnCtrlC, true);   // only honoured once attached
        bool sent = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
        if (!sent) FreeConsole();
        return sent;
    }

    /// <summary>
    /// The <c>-o devname=</c> value handed to ltfs.exe at mount, and the exact
    /// needle <see cref="FindExternalMount"/> uses to re-find that process later.
    /// One definition so the two can never drift apart.
    /// </summary>
    private static string DevNameOpt(string device) => $"devname={device.Replace('\\', '/')}";

    /// <summary>
    /// Find the ltfs.exe process from a previous session still serving a drive.
    /// Matched on the <c>-o devname=</c> argument we pass at mount — the drive's
    /// physical identity (<c>TAPEn</c>), which is stable and unique. (The volume
    /// letter is on the command line too, but as <c>\\.\T:</c>, so matching a
    /// bare letter never fit.)
    /// </summary>
    public static int? FindExternalMount(string device)
    {
        var needle = DevNameOpt(device);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'ltfs.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmd = obj["CommandLine"] as string ?? "";
                if (cmd.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(obj["ProcessId"]);
            }
        }
        catch { }
        return null;
    }
}
