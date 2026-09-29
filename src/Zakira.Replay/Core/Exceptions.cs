namespace Zakira.Replay.Core;

public class ReplayException : Exception
{
    public ReplayException(string message)
        : base(message)
    {
    }

    public ReplayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MissingDependencyException : ReplayException
{
    public MissingDependencyException(string dependency, string requiredFor, string? envVarName = null)
        : base($"Missing dependency: {dependency}")
    {
        Dependency = dependency;
        RequiredFor = requiredFor;
        EnvVarName = envVarName;
    }

    public string Dependency { get; }

    public string RequiredFor { get; }

    public string? EnvVarName { get; }

    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"Missing dependency: {Dependency}",
            string.Empty,
            $"Required for: {RequiredFor}.",
            "Install it manually and ensure it is available on PATH, or run `zakira-replay deps install media` to install portable media tools."
        };

        if (!string.IsNullOrWhiteSpace(EnvVarName))
        {
            lines.Add($"Alternatively configure {EnvVarName}=<full path>.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ProcessFailedException : ReplayException
{
    public ProcessFailedException(string fileName, IReadOnlyList<string> arguments, int exitCode, string standardError)
        : base($"Process failed with exit code {exitCode}: {fileName}")
    {
        FileName = fileName;
        Arguments = arguments;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>
/// Thrown when the analysis pipeline failed part-way but a manifest describing the artifacts
/// produced so far was written (<c>manifest.status</c> is <c>partial</c> or <c>failed</c>).
/// Callers keep failure semantics (non-zero exit, failed/partial job) while still getting the
/// run directory and manifest - the transcript, frames and metadata that exist are reachable
/// without browsing the filesystem. <see cref="Exception.InnerException"/> is the original cause.
/// </summary>
public sealed class AnalysisPartialException : ReplayException
{
    public AnalysisPartialException(string message, Exception cause, VideoRun run, ArtifactManifest manifest)
        : base(message, cause)
    {
        Run = run;
        Manifest = manifest;
    }

    public VideoRun Run { get; }

    public ArtifactManifest Manifest { get; }

    /// <summary>True when something usable (transcript, frames or audio) survived.</summary>
    public bool HasUsableArtifacts => string.Equals(Manifest.Status, RunStatuses.Partial, StringComparison.OrdinalIgnoreCase);

    public AnalyzeResult ToResult() => new(Run, Manifest);
}

/// <summary>
/// Thrown when a subprocess exceeded its wall-clock budget and was killed. Unlike a plain
/// "timed out" message this carries everything the process printed before it was stopped, so
/// the caller can tell a YouTube bot-check / HTTP 429 / missing-JS-runtime stall apart from a
/// genuinely slow download. <see cref="Exception.Message"/> already embeds a bounded tail of
/// stderr (falling back to stdout) plus a hint when a known failure signature is recognised,
/// so every consumer that only logs the message still gets the diagnosis.
/// </summary>
public sealed class ProcessTimeoutException : ReplayException
{
    public const int DefaultTailLines = 20;
    public const int DefaultMaxLineLength = 400;

    public ProcessTimeoutException(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string standardOutput,
        string standardError)
        : base(BuildMessage(fileName, timeout, standardOutput, standardError))
    {
        FileName = fileName;
        Arguments = arguments;
        Timeout = timeout;
        StandardOutput = standardOutput;
        StandardError = standardError;
        OutputTail = Tail(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
        Hint = SubprocessDiagnostics.SuggestHint(standardError + Environment.NewLine + standardOutput);
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    /// <summary>Everything the process wrote to stdout before it was killed.</summary>
    public string StandardOutput { get; }

    /// <summary>Everything the process wrote to stderr before it was killed.</summary>
    public string StandardError { get; }

    /// <summary>Last <see cref="DefaultTailLines"/> lines of stderr (stdout when stderr is empty).</summary>
    public string OutputTail { get; }

    /// <summary>Actionable hint derived from known failure signatures in the output, if any.</summary>
    public string? Hint { get; }

    /// <summary>Last <paramref name="maxLines"/> non-empty lines of <paramref name="text"/>, each clipped to <paramref name="maxLineLength"/> characters.</summary>
    public static string Tail(string? text, int maxLines = DefaultTailLines, int maxLineLength = DefaultMaxLineLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToArray();
        var tail = lines.Skip(Math.Max(0, lines.Length - maxLines))
            .Select(line => line.Length > maxLineLength ? line[..maxLineLength] + "…" : line);
        return string.Join(Environment.NewLine, tail);
    }

    private static string BuildMessage(string fileName, TimeSpan timeout, string standardOutput, string standardError)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("Process timed out after ").Append(timeout).Append(": ").Append(fileName);
        builder.Append(" (see timeouts.* in `zakira-replay config` to raise the budget)");

        var source = string.IsNullOrWhiteSpace(standardError) ? "stdout" : "stderr";
        var tail = Tail(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
        if (tail.Length == 0)
        {
            builder.AppendLine().Append("No output was captured from the process before the timeout.");
        }
        else
        {
            var lineCount = tail.Split(Environment.NewLine).Length;
            builder.AppendLine().Append("Last ").Append(lineCount).Append(" line(s) of ").Append(source).Append(':');
            foreach (var line in tail.Split(Environment.NewLine))
            {
                builder.AppendLine().Append("  ").Append(line);
            }
        }

        var hint = SubprocessDiagnostics.SuggestHint(standardError + Environment.NewLine + standardOutput);
        if (hint is not null)
        {
            builder.AppendLine().Append("Hint: ").Append(hint);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Recognises well-known yt-dlp / ffmpeg failure signatures in captured output and turns them
/// into a one-line remediation hint. Kept deliberately small: only signatures that map to a
/// concrete user action are listed.
/// </summary>
public static class SubprocessDiagnostics
{
    private static readonly (string Needle, string Hint)[] Signatures =
    [
        ("Sign in to confirm you're not a bot", "YouTube served a bot check. Pass --cookies <netscape-file> or --cookies-from-browser <browser> so yt-dlp can authenticate, and make sure yt-dlp is current (`zakira-replay deps install yt-dlp --force`)."),
        ("Sign in to confirm your age", "The video is age-restricted. Pass --cookies or --cookies-from-browser for an account that can view it."),
        ("HTTP Error 429", "The source is rate-limiting this machine (HTTP 429). Wait before retrying, reduce concurrency, or authenticate with cookies."),
        ("Too Many Requests", "The source is rate-limiting this machine (HTTP 429). Wait before retrying, reduce concurrency, or authenticate with cookies."),
        ("n challenge", "yt-dlp could not solve YouTube's JavaScript challenge. Install a JavaScript runtime it can find on PATH (deno is recommended; node also works) and update yt-dlp."),
        ("nsig extraction failed", "yt-dlp could not solve YouTube's JavaScript challenge. Install a JavaScript runtime it can find on PATH (deno is recommended; node also works) and update yt-dlp."),
        ("No supported JavaScript runtime", "yt-dlp needs a JavaScript runtime for this site. Install deno (recommended) or node and make sure it is on PATH."),
        ("Requested format is not available", "None of the requested formats exist for this source. Check `yt-dlp -F <url>`; a stale yt-dlp or a missing JS runtime can hide formats."),
        ("Private video", "The video is private. Authenticate with --cookies / --cookies-from-browser for an account that can view it."),
        ("Video unavailable", "The source reports the video as unavailable (removed, region-locked, or private)."),
        ("HTTP Error 403", "The source refused the request (HTTP 403). Signed media URLs expire after a few hours; re-run the analysis to obtain fresh ones, or authenticate with cookies."),
        ("This video is unavailable", "The source reports the video as unavailable (removed, region-locked, or private)."),
        ("Unable to extract", "yt-dlp's extractor for this site is broken or outdated. Update yt-dlp (`zakira-replay deps install yt-dlp --force`) and retry."),
    ];

    public static string? SuggestHint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var (needle, hint) in Signatures)
        {
            if (output.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return hint;
            }
        }

        return null;
    }
}

/// <summary>
/// Thrown when a sqlite-onnx search index is queried with an embedding model whose identity
/// doesn't match what the index was built against. The vector spaces produced by different
/// embedding models are not interchangeable even when dimensions match, so cross-model
/// queries would return plausible-looking but meaningless results. Caller fix is either to
/// rebuild the index (`index build --force`) or to pin the original model for this query
/// (`--onnx-model &lt;id&gt;`). Maps to MCP error data {code: SEARCH_INDEX_EMBEDDING_MISMATCH}.
/// </summary>
public sealed class SearchIndexEmbeddingMismatchException : ReplayException
{
    public SearchIndexEmbeddingMismatchException(
        string indexedModelId,
        string indexedModelKind,
        string runtimeModelId,
        string runtimeModelKind,
        string? indexedDimensions)
        : base(BuildMessage(indexedModelId, indexedModelKind, runtimeModelId, runtimeModelKind, indexedDimensions))
    {
        IndexedModelId = indexedModelId;
        IndexedModelKind = indexedModelKind;
        RuntimeModelId = runtimeModelId;
        RuntimeModelKind = runtimeModelKind;
        IndexedDimensions = indexedDimensions;
    }

    public string IndexedModelId { get; }

    public string IndexedModelKind { get; }

    public string RuntimeModelId { get; }

    public string RuntimeModelKind { get; }

    public string? IndexedDimensions { get; }

    public const string Code = "SEARCH_INDEX_EMBEDDING_MISMATCH";

    private static string BuildMessage(string indexedModelId, string indexedModelKind, string runtimeModelId, string runtimeModelKind, string? indexedDimensions)
    {
        var dims = string.IsNullOrWhiteSpace(indexedDimensions) ? "?" : indexedDimensions;
        return $"{Code}: index was built with {indexedModelId} ({indexedModelKind}, {dims}d); runtime is {runtimeModelId} ({runtimeModelKind}). " +
            $"The vector spaces are not interchangeable. Fix one of two ways: " +
            $"(1) rebuild with the runtime model: `zakira-replay index build <run-dir> --force`; " +
            $"(2) pin the indexed model for this query: `--onnx-model {indexedModelId}`.";
    }
}
