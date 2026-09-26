using System.Diagnostics;

namespace LTOG.Core;

/// <summary>Outcome of a one-shot tool run: exit code plus every captured line.</summary>
public record RunResult(int ExitCode, IReadOnlyList<string> Output);

/// <summary>Runs a one-shot CLI tool (mkltfs/unltfs/ltfsck/ltfs -V), logging the
/// full command line, streaming its output to the activity log, and returning the
/// captured lines for callers that need to parse them.</summary>
public static class ToolRunner
{
    public static async Task<RunResult> RunAsync(string exe, IReadOnlyList<string> args,
        IActivityLog log, LogKind kind, string title, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = LtfsEnv.DistPath!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var scope = log.Begin(kind, title, CommandLine.Format(exe, args), LtfsEnv.DistPath);
        var output = new List<string>();

        void Capture(string line)
        {
            lock (output) output.Add(line);
            scope.Line(line);
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Capture(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Capture(e.Data); };
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            scope.Complete(p.ExitCode);
            return new RunResult(p.ExitCode, output);
        }
        catch (Exception ex)
        {
            scope.Complete(null, ex.Message);
            throw;
        }
    }
}
