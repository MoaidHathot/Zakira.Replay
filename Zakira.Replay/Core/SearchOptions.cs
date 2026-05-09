namespace Zakira.Replay.Core;

public static class SearchBackends
{
    public const string Auto = "auto";
    public const string Json = "json";
    public const string Sqlite = "sqlite";
    public const string SqliteOnnx = "sqlite-onnx";

    public static string Normalize(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return Json;
        }

        return backend.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            Auto => Auto,
            Json or "tfidf" or "tf-idf" => Json,
            Sqlite or "fts" or "sqlite-fts" => Sqlite,
            SqliteOnnx or "sqliteonnx" or "semantic" => SqliteOnnx,
            var value => value
        };
    }
}

public sealed record SearchIndexBuildOptions(
    string Backend = SearchBackends.Json,
    string? OnnxModelPath = null,
    string? OnnxVocabularyPath = null,
    int? OnnxMaxSequenceLength = null,
    int? EmbeddingDimensions = null);

public sealed record SearchIndexQueryOptions(
    string Backend = SearchBackends.Auto,
    string? OnnxModelPath = null,
    string? OnnxVocabularyPath = null,
    int? OnnxMaxSequenceLength = null,
    int? EmbeddingDimensions = null);

public sealed record SearchIndexBuildResult(
    string Backend,
    string RunId,
    int DocumentCount,
    string IndexPath,
    DateTimeOffset CreatedAt,
    SearchIndexManifest? Manifest = null);

public interface ISearchEmbeddingProvider
{
    string Name { get; }

    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
