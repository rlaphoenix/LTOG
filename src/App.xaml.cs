using System.Diagnostics;
using System.Runtime.InteropServices;
using LTOG.Core;
using Microsoft.UI.Xaml;

namespace LTOG;

public partial class App : Application
{
    public static bool AutoRemount { get; private set; }

    // ---- shared services (created once in OnLaunched, on the UI thread) ----
    public static Settings Settings { get; } = Settings.Load();
    /// <summary>Structured log of every LTFS/WinFsp/tape-drive invocation (Log page).</summary>
    public static ActivityLog Activity { get; private set; } = null!;
    /// <summary>False when ltfs.exe / ltfs.conf weren't found: drive actions stay disabled.</summary>
    public static bool EnvOk { get; private set; }
    public static DriveStore DriveStore { get; private set; } = null!;
    private MainWindow? _window;

    private static Mutex? _singleInstance;
    private const string InstanceMutexName = @"Global\LTOG-6E9D2B4A-1C3F-4E58-9A7D-2F5B8C1E4D0A";

    public App()
    {
        InitializeComponent();
        AutoRemount = Environment.GetCommandLineArgs().Contains("--remount");
        UnhandledException += (_, e) =>
        {
            LogCrash($"UnhandledException: {e.Exception}");
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash($"AppDomain: {e.ExceptionObject}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool isNew = true;
        try { _singleInstance = new Mutex(initiallyOwned: true, InstanceMutexName, out isNew); }
        catch (Exception ex) { LogCrash($"single-instance guard skipped: {ex.Message}"); }
        if (!isNew)
        {
            ActivateExistingWindow();   // bring the running LTOG forward, then bow out
            Exit();
            return;
        }

        try
        {
            // on the UI thread: the log resolves its theme brushes
            Activity = new ActivityLog(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            EnvOk = LtfsEnv.Resolve(Settings.DistPath);
            DriveStore = new DriveStore(Settings, Activity, EnvOk);
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash($"OnLaunched: {ex}");
            throw;
        }
    }

    /// <summary>Restore and foreground the already-running LTOG's main window.</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                break;
            }
        }
        catch { /* best-effort; the duplicate still exits either way */ }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static void LogCrash(string text)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "crash.log"),
                $"[{DateTime.Now:O}] {text}\n");
        }
        catch { }
    }
}
