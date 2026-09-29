using System.Diagnostics;
using System.Text;

namespace Zakira.Replay.Core;

public class ProcessRunner
{
    public virtual async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new ReplayException($"Failed to start process: {fileName}");
            }
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayException($"Failed to start process: {fileName}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (timeoutCts?.IsCancellationRequested == true)
            {
                // Let the async stdout/stderr readers drain what the process printed before it
                // was killed - that output is the only evidence of *why* it stalled (bot check,
                // HTTP 429, missing JS runtime, ...). WaitForExit() without a timeout is the
                // documented way to guarantee the redirected-stream handlers have completed.
                TryDrain(process);
                throw new ProcessTimeoutException(fileName, argumentList, timeout!.Value, stdout.ToString(), stderr.ToString());
            }

            throw;
        }

        return new ProcessResult(fileName, argumentList, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup after cancellation.
        }
    }

    private static void TryDrain(Process process)
    {
        try
        {
            // The process was just killed, so this returns promptly; the bounded wait is a guard
            // against a child that ignores termination while holding the pipes open.
            if (process.WaitForExit(2000))
            {
                process.WaitForExit();
            }
        }
        catch
        {
            // Draining is best-effort; the exception below still carries whatever was captured.
        }
    }
}

public sealed record ProcessResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public void EnsureSuccess()
    {
        if (ExitCode != 0)
        {
            throw new ProcessFailedException(FileName, Arguments, ExitCode, StandardError);
        }
    }
}
