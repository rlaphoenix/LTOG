using System.Text;

namespace LTOG.Core;

/// <summary>What kind of operation an activity-log entry represents (drives its icon/colour).</summary>
public enum LogKind
{
    /// <summary>An ltfs.exe mount (the WinFsp-backed filesystem).</summary>
    Mount,
    /// <summary>A one-shot LTFS binary: mkltfs / unltfs / ltfsck / ltfs -V.</summary>
    Tool,
    /// <summary>A native tape-device call (load/eject, SCSI pass-through).</summary>
    Native,
    /// <summary>A passive read from the drive/cartridge (enumeration, MAM/status).</summary>
    Read,
    /// <summary>A GUI-side event with no command body.</summary>
    System,
}

/// <summary>
/// Records every external invocation — the LTFS binaries, the WinFsp-backed mount
/// and native tape calls — as a structured entry holding the full command line,
/// the streamed output and a final result. Implemented by the GUI's
/// <c>ActivityLog</c>; passed into the Core runners so nothing is left unlogged.
/// </summary>
public interface IActivityLog
{
    /// <summary>Open an entry for one invocation. Feed it output, then <see cref="ILogScope.Complete"/> it.</summary>
    ILogScope Begin(LogKind kind, string title, string commandLine, string? workingDir = null);

    /// <summary>Record a one-line event with no command body, optionally flagged as an error.</summary>
    void Note(string message, bool isError = false);

    /// <summary>
    /// Log a value read from the hardware (drive enumeration, cartridge MAM/status).
    /// Deduplicated by <paramref name="key"/>: if the same value was logged last
    /// time for that key the entry is suppressed, so the periodic refresh poll does
    /// not spam the log with identical values — only changes (and the first read)
    /// appear.
    /// </summary>
    void Read(string title, string command, IReadOnlyList<string> resultLines, string key);
}

/// <summary>A live handle to one in-progress activity-log entry.</summary>
public interface ILogScope
{
    /// <summary>Append a line of output to the entry (severity is auto-detected for display).</summary>
    void Line(string text);

    /// <summary>Close the entry with a process exit code and/or an error message.</summary>
    void Complete(int? exitCode = null, string? error = null);
}

/// <summary>Renders an executable + argument list back into a copy-pasteable command line.</summary>
public static class CommandLine
{
    public static string Format(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in args)
            sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Length == 0 || s.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;
}
