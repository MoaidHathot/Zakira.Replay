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
