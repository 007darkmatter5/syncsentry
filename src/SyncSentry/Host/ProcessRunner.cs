using System.Diagnostics;
using System.Text;

namespace SyncSentry.Host;

public sealed record RunResult(
    bool TimedOut,
    bool Exited,
    int? ExitCode,
    string Stdout,
    string Stderr,
    Process? LiveProcess)
{
    public bool TruncatedOutput { get; init; }
}

/// <summary>
/// Runs a process and always returns when the timeout elapses.
/// A child stuck in uninterruptible sleep (a frozen CIFS stat) is left running and returned
/// as <see cref="RunResult.LiveProcess"/> so the caller can avoid starting a second one.
/// This method does not wait for that child.
/// </summary>
public static class ProcessRunner
{
    public const int MaxCapturedChars = 32_000_000;

    public static async Task<RunResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        SecretEnvironmentRemove(start);

        var proc = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var overflow = 0;
        proc.OutputDataReceived += (_, e) => Append(stdout, e.Data, ref overflow);
        proc.ErrorDataReceived += (_, e) => Append(stderr, e.Data, ref overflow);

        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                return new RunResult(false, true, null, "", "could not start process", null);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            proc.Dispose();
            return new RunResult(false, true, null, "", ex.GetType().Name, null);
        }

        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FinishTimeout(proc, stdout, stderr, overflow > 0);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            proc.WaitForExit(200);
            var exited = HasExited(proc);
            if (exited)
                proc.Dispose();
            return new RunResult(false, exited, exited ? SafeExit(proc) : null, Take(stdout), Take(stderr), exited ? null : proc);
        }

        proc.WaitForExit(2_000);
        var textOut = Take(stdout);
        var textErr = Take(stderr);
        var code = SafeExit(proc);
        proc.Dispose();
        return new RunResult(false, true, code, textOut, textErr, null) { TruncatedOutput = overflow > 0 };
    }

    private static RunResult FinishTimeout(Process proc, StringBuilder stdout, StringBuilder stderr, bool truncated)
    {
        TryKill(proc);
        var reaped = proc.WaitForExit(200);
        if (reaped)
        {
            var code = SafeExit(proc);
            proc.Dispose();
            return new RunResult(true, true, code, Take(stdout), Take(stderr), null) { TruncatedOutput = truncated };
        }

        return new RunResult(true, false, null, Take(stdout), Take(stderr), proc) { TruncatedOutput = truncated };
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool HasExited(Process proc)
    {
        try
        {
            return proc.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? SafeExit(Process proc)
    {
        try
        {
            return proc.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Append(StringBuilder buffer, string? line, ref int overflow)
    {
        if (line is null)
            return;
        lock (buffer)
        {
            if (buffer.Length > MaxCapturedChars)
            {
                overflow++;
                return;
            }

            buffer.AppendLine(line);
        }
    }

    private static string Take(StringBuilder buffer)
    {
        lock (buffer)
            return buffer.ToString();
    }

    private static void SecretEnvironmentRemove(ProcessStartInfo start)
    {
        foreach (var key in SyncSentry.Security.SecretEnvironment.Keys)
            start.Environment.Remove(key);
    }
}

public static class AppSpawn
{
    public static ProcessStartInfo Create(params string[] args)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var processPath = Environment.ProcessPath ?? "dotnet";
        var file = Path.GetFileName(processPath);
        var command = Environment.GetCommandLineArgs();
        if (file.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || file.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            if (command.Length > 0)
                start.ArgumentList.Add(command[0]);
        }
        else
        {
            start.FileName = processPath;
        }

        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }
}

/// <summary>Remembers helper processes that did not die, one per key, so we never pile them up.</summary>
public sealed class StuckProcesses
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _items = new(StringComparer.Ordinal);

    public bool IsStuck(string key)
    {
        if (!_items.TryGetValue(key, out var proc))
            return false;
        try
        {
            if (!proc.HasExited)
                return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        if (_items.TryRemove(key, out var removed))
        {
            try
            {
                removed.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    public void Remember(string key, Process proc) => _items[key] = proc;

    public int Count => _items.Count;
}
