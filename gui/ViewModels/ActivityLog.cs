using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LTOG.Gui.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LTOG.Gui;

public enum LogStatus { Running, Success, Failure, Info }
public enum LineSeverity { Normal, Warning, Error }

/// <summary>
/// Theme brushes used by the activity-log view-models, resolved once on the UI
/// thread (so the models themselves stay free of resource look-ups).
/// </summary>
internal static class LogBrushes
{
    public static Brush Normal = null!, Muted = null!, Warning = null!, Error = null!,
        Accent = null!, Tool = null!, Native = null!,
        Running = null!, Success = null!, Failure = null!;
    private static bool _ready;

    public static void EnsureInit()
    {
        if (_ready) return;
        Brush R(string key) => (Brush)Application.Current.Resources[key];
        Normal  = R("TextFillColorPrimaryBrush");
        Muted   = R("TextFillColorSecondaryBrush");
        Warning = R("SystemFillColorCautionBrush");
        Error   = R("SystemFillColorCriticalBrush");
        Accent  = R("AccentFillColorDefaultBrush");
        Tool    = R("SystemFillColorAttentionBrush");
        Native  = R("SystemFillColorCautionBrush");
        Running = R("SystemFillColorAttentionBrush");
        Success = R("SystemFillColorSuccessBrush");
        Failure = R("SystemFillColorCriticalBrush");
        _ready = true;
    }
}

/// <summary>One line of captured output, colour-coded by detected LTFS severity.</summary>
public sealed class LogLine
{
    // LTFS message ids look like "LTFS14075E" / "LTFS17302W" — the trailing
    // letter is the severity (I info, W warning, E error, D debug).
    private static readonly Regex Code =
        new(@"\b[A-Z]{2,5}\d{4,5}([IWED])\b", RegexOptions.Compiled);

    public string Text { get; }
    public LineSeverity Severity { get; }

    public LogLine(string text)
    {
        Text = text;
        Severity = Classify(text);
    }

    public Brush Brush => Severity switch
    {
        LineSeverity.Error => LogBrushes.Error,
        LineSeverity.Warning => LogBrushes.Warning,
        _ => LogBrushes.Normal,
    };

    private static LineSeverity Classify(string s)
    {
        var m = Code.Match(s);
        if (!m.Success) return LineSeverity.Normal;
        return m.Groups[1].Value switch
        {
            "E" => LineSeverity.Error,
            "W" => LineSeverity.Warning,
            _ => LineSeverity.Normal,
        };
    }
}

/// <summary>
/// One activity-log entry: a header, an optional full command line, the streamed
/// output lines and a result footer. Bound directly by the Log page's template.
/// </summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    private const int LineCap = 1000;

    public LogKind Kind { get; }
    public string Title { get; }
    public string Timestamp { get; }
    public string CommandLine { get; }
    public string? WorkingDir { get; }
    public ObservableCollection<LogLine> Lines { get; } = new();

    private readonly bool _hasResult;
    private readonly bool _error;
    private LogStatus _status;
    private string _resultDetail = "";
    private int _lineCount;

    public LogEntry(LogKind kind, string title, string commandLine, string? workingDir,
        string timestamp, bool hasResult, bool error = false)
    {
        Kind = kind;
        Title = title;
        CommandLine = commandLine;
        WorkingDir = workingDir;
        Timestamp = timestamp;
        _hasResult = hasResult;
        _error = error;
        _status = hasResult ? LogStatus.Running : LogStatus.Info;
    }

    // ---- header --------------------------------------------------------------

    public string Glyph => Kind switch
    {
        LogKind.Mount => "",   // MapDrive
        LogKind.Tool => "",    // Settings (maintenance tool)
        LogKind.Native => "",  // tape / cartridge
        LogKind.Read => "",    // Search (passive probe/read)
        _ => "",               // Info
    };

    public Brush AccentBrush => Kind switch
    {
        LogKind.Mount => LogBrushes.Accent,
        LogKind.Tool => LogBrushes.Tool,
        LogKind.Native => LogBrushes.Native,
        LogKind.Read => LogBrushes.Muted,
        _ => _error ? LogBrushes.Failure : LogBrushes.Muted,
    };

    // ---- command / output ----------------------------------------------------

    public Visibility CommandVisibility =>
        CommandLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkingDirVisibility =>
        string.IsNullOrEmpty(WorkingDir) ? Visibility.Collapsed : Visibility.Visible;
    public string WorkingDirText => $"in {WorkingDir}";
    public Visibility OutputVisibility =>
        Lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- result footer -------------------------------------------------------

    public Visibility ResultVisibility =>
        _hasResult ? Visibility.Visible : Visibility.Collapsed;
    public bool Running => _status == LogStatus.Running;
    public Visibility SpinnerVisibility =>
        Running ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusBrush => _status switch
    {
        LogStatus.Running => LogBrushes.Running,
        LogStatus.Success => LogBrushes.Success,
        LogStatus.Failure => LogBrushes.Failure,
        _ => LogBrushes.Muted,
    };

    public string StatusText => _status switch
    {
        LogStatus.Running => "RUNNING",
        LogStatus.Success => "DONE",
        LogStatus.Failure => "FAILED",
        _ => "INFO",
    };

    public string ResultDetail => _resultDetail;

    // ---- mutation (always on the UI thread) ----------------------------------

    public void AddLine(string text)
    {
        if (_lineCount > LineCap) return;
        if (_lineCount == LineCap)
        {
            Lines.Add(new LogLine("… output truncated — see the full log file"));
            _lineCount++;
            return;
        }
        bool first = Lines.Count == 0;
        Lines.Add(new LogLine(text));
        _lineCount++;
        if (first) Raise(nameof(OutputVisibility));
    }

    public void CompleteWith(int? exitCode, string? error, double seconds)
    {
        _status = error == null && exitCode is null or 0 ? LogStatus.Success : LogStatus.Failure;
        _resultDetail = error
            ?? (exitCode is int c ? $"exit {c} · {seconds:0.0} s" : $"finished · {seconds:0.0} s");
        Raise(nameof(Running));
        Raise(nameof(SpinnerVisibility));
        Raise(nameof(StatusBrush));
        Raise(nameof(StatusText));
        Raise(nameof(ResultDetail));
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The GUI's structured logger. Owns the observable list bound to the Log page,
/// marshals all mutations onto the UI thread, and mirrors everything to a durable
/// plain-text transcript on disk.
/// </summary>
public sealed class ActivityLog : IActivityLog, INotifyPropertyChanged
{
    private const int MaxEntries = 500;

    private readonly DispatcherQueue _dq;
    private readonly object _fileLock = new();
    private readonly Dictionary<string, string> _lastRead = new();   // dedup of periodic reads

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public string FilePath { get; }
    public Visibility EmptyHint =>
        Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised (on the UI thread) after any change — used for auto-scroll.</summary>
    public event Action? Updated;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ActivityLog(DispatcherQueue dispatcher)
    {
        _dq = dispatcher;
        LogBrushes.EnsureInit();   // runs on the UI thread (MainWindow ctor)
        FilePath = Path.Combine(Settings.Dir, "activity.log");
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            if (!File.Exists(FilePath))
                File.WriteAllText(FilePath, $"LTOG activity log — opened {Now()}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public ILogScope Begin(LogKind kind, string title, string commandLine, string? workingDir = null)
    {
        string ts = Stamp();
        var entry = new LogEntry(kind, title, commandLine, workingDir, ts, hasResult: true);
        Post(() => Add(entry));

        var header = new List<string>
        {
            $"[{ts}] ┌─ {Tag(kind)} {title}",
            $"        $ {commandLine}",
        };
        if (!string.IsNullOrEmpty(workingDir))
            header.Add($"          (in {workingDir})");
        WriteFile(header);

        return new Scope(this, entry);
    }

    public void Note(string message, bool isError = false)
    {
        string ts = Stamp();
        var entry = new LogEntry(LogKind.System, message, "", null, ts, hasResult: false, error: isError);
        Post(() => Add(entry));
        WriteFile(new[] { $"[{ts}] {(isError ? "✗" : "•")} {message}" });
    }

    public void Read(string title, string command, IReadOnlyList<string> resultLines, string key)
    {
        string sig = string.Join("\n", resultLines);
        lock (_lastRead)
        {
            // Suppress an identical repeat of the same probe (periodic refresh):
            // only the first read and subsequent value *changes* are logged.
            if (_lastRead.TryGetValue(key, out var prev) && prev == sig) return;
            _lastRead[key] = sig;
        }
        string ts = Stamp();
        // hasResult: false → a passive read shows the command and values, with no
        // spinner or status pill (it is instantaneous).
        var entry = new LogEntry(LogKind.Read, title, command, null, ts, hasResult: false);
        Post(() =>
        {
            Add(entry);
            foreach (var l in resultLines) entry.AddLine(l);
        });
        var fileLines = new List<string> { $"[{ts}] {Tag(LogKind.Read)} {title}", $"        $ {command}" };
        foreach (var l in resultLines) fileLines.Add($"        │ {l}");
        WriteFile(fileLines);
    }

    public void Clear()
    {
        Entries.Clear();
        Raise(nameof(EmptyHint));
        WriteFile(new[] { "", "──────────────── cleared ────────────────", "" });
    }

    // ---- internals -----------------------------------------------------------

    private void Add(LogEntry e)
    {
        Entries.Add(e);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        Raise(nameof(EmptyHint));
        Updated?.Invoke();
    }

    private void Post(Action a)
    {
        if (!_dq.TryEnqueue(() => a())) a();
    }

    private void Ping() => _dq.TryEnqueue(() => Updated?.Invoke());

    private void WriteFile(IEnumerable<string> lines)
    {
        try { lock (_fileLock) File.AppendAllLines(FilePath, lines); }
        catch { /* best effort */ }
    }

    private static string Stamp() => Now();
    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    private static string Tag(LogKind k) => k switch
    {
        LogKind.Mount => "[MOUNT]",
        LogKind.Tool => "[TOOL] ",
        LogKind.Native => "[TAPE] ",
        LogKind.Read => "[READ] ",
        _ => "[SYS]  ",
    };

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class Scope : ILogScope
    {
        private readonly ActivityLog _log;
        private readonly LogEntry _entry;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _done;

        public Scope(ActivityLog log, LogEntry entry)
        {
            _log = log;
            _entry = entry;
        }

        public void Line(string text)
        {
            _log.WriteFile(new[] { $"        │ {text}" });
            _log.Post(() => _entry.AddLine(text));
            _log.Ping();
        }

        public void Complete(int? exitCode = null, string? error = null)
        {
            if (_done) return;
            _done = true;
            _sw.Stop();
            double secs = _sw.Elapsed.TotalSeconds;
            string outcome = error ?? (exitCode is int c ? $"exit {c}" : "finished");
            _log.WriteFile(new[] { $"        └─ {outcome}  ({secs:0.0}s)", "" });
            _log.Post(() => _entry.CompleteWith(exitCode, error, secs));
            _log.Ping();
        }
    }
}
