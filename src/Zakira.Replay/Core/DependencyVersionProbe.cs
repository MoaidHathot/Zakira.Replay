namespace Zakira.Replay.Core;

internal static class DependencyVersionProbe
{
    public static async Task<(string? Error, string? Version)> RunAsync(
        string name, string path, ProcessRunner runner, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var argument = name is "ffmpeg" or "ffprobe" ? "-version" : "--version";
        try
        {
            var result = await runner.RunAsync(path, [argument], timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                // A blocked Python DLL and the policy that blocked it are often on separate lines.
                var lines = Lines(result.StandardError + "\n" + result.StandardOutput);
                var detail = lines.Length == 0 ? "" : ": " + string.Join(" | ", lines.Take(3));
                return ($"exit code {result.ExitCode}{detail}", null);
            }

            var version = DoctorAdvice.ParseVersionLine(name, Lines(result.StandardOutput + "\n" + result.StandardError).FirstOrDefault());
            return string.IsNullOrWhiteSpace(version) ? ($"`{argument}` returned no version", null) : (null, version);
        }
        catch (ProcessTimeoutException)
        {
            return ($"did not answer `{argument}` within {timeout.TotalSeconds:0}s (see timeouts.dependencyCheckSeconds)", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ex.GetBaseException().Message, null);
        }
    }

    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
