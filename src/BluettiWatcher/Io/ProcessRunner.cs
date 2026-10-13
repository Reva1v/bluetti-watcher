using System.Diagnostics;
using System.Text;

namespace BluettiWatcher.Io;

internal sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut);

internal interface IProcessRunner
{
    /// <summary>Runs <paramref name="exe"/>; <paramref name="env"/> adds to (or overrides) the child's environment.</summary>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null);
}

/// <summary>Runs one command line as given in the config: an executable and its arguments, no shell.</summary>
internal interface ICommandLineRunner
{
    Task<ProcessResult> RunLineAsync(string commandLine, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null);
}

/// <summary>Starts a program windowless, collects stdout and stderr lines, kills the whole tree on timeout.</summary>
internal sealed class ProcessRunner : IProcessRunner, ICommandLineRunner
{
    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = StartInfo(exe, env);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return RunAsync(psi, timeout, ct);
    }

    public Task<ProcessResult> RunLineAsync(string commandLine, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var (exe, arguments) = SplitCommandLine(commandLine);
        var psi = StartInfo(exe, env);
        psi.Arguments = arguments;
        return RunAsync(psi, timeout, ct);
    }

    /// <summary>The executable (a leading quoted path, or the first word) and the rest of the line, passed on as written.</summary>
    internal static (string Exe, string Arguments) SplitCommandLine(string line)
    {
        string s = line.Trim();
        if (s.Length == 0) throw new ArgumentException("the command line is empty", nameof(line));
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            if (close < 0) throw new ArgumentException("the command line has an unclosed quote", nameof(line));
            return (s[1..close], s[(close + 1)..].TrimStart());
        }
        int space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].TrimStart());
    }

    private static ProcessStartInfo StartInfo(string exe, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        var lines = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        lock (lines) return new ProcessResult(timedOut ? -1 : p.ExitCode, [.. lines], timedOut);
    }
}
