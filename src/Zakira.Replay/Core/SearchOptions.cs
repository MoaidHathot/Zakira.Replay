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

/// <summary>
/// Selects the embedding scheme for the ONNX search backend. Each enum value drives both
/// the per-side text prefix and the pooling strategy applied to the model output:
/// <list type="bullet">
///   <item><description><see cref="Bert"/> — legacy / generic BERT WordPiece. No prefixes,
///     mean pooling over the attention mask. The fallback for any custom model the user
///     points at without setting an explicit kind.</description></item>
///   <item><description><see cref="Bge"/> — BAAI BGE family (<c>bge-small-en-v1.5</c>) and the
///     architecturally identical Snowflake arctic-embed-* family. Query-side prefix
///     <c>"Represent this sentence for searching relevant passages: "</c>; documents passed
///     as-is. CLS-token pooling, then L2 normalize.</description></item>
///   <item><description><see cref="E5"/> — Microsoft/intfloat E5 family
///     (<c>multilingual-e5-small</c>, <c>e5-small-v2</c>, …). Symmetric prefixes
///     <c>"query: "</c> at query time and <c>"passage: "</c> at index time. Mean pooling
///     over the attention mask, then L2 normalize.</description></item>
/// </list>
/// </summary>
public enum SearchEmbeddingModelKind
{
    Bert,
    Bge,
    E5
}

/// <summary>
/// Tells the embedding provider which side of the retrieval pair the call is for. Drives
/// the choice of prefix (and, for some asymmetric models, would drive the pooling head).
/// </summary>
public enum SearchEmbeddingSide
{
    Document,
    Query
}

public sealed record SearchIndexBuildOptions(
    string Backend = SearchBackends.Json,
    string? OnnxModelPath = null,
    string? OnnxVocabularyPath = null,
    int? OnnxMaxSequenceLength = null,
    int? EmbeddingDimensions = null,
    string? OnnxModel = null,
    string? OnnxModelKind = null,
    string? OnnxTokenizerPath = null);

public sealed record SearchIndexQueryOptions(
    string Backend = SearchBackends.Auto,
    string? OnnxModelPath = null,
    string? OnnxVocabularyPath = null,
    int? OnnxMaxSequenceLength = null,
    int? EmbeddingDimensions = null,
    string? OnnxModel = null,
    string? OnnxModelKind = null,
    string? OnnxTokenizerPath = null);

public sealed record SearchIndexBuildResult(
    string Backend,
    string RunId,
    int DocumentCount,
    string IndexPath,
    DateTimeOffset CreatedAt,
    SearchIndexManifest? Manifest = null,
    string? EmbeddingModel = null,
    string? EmbeddingModelKind = null,
    int? EmbeddingDimensions = null);

public interface ISearchEmbeddingProvider
{
    string Name { get; }

    /// <summary>
    /// Stable model identifier (e.g. <c>bge-small-en-v1.5</c>). Persisted into search index
    /// manifests so a later query can detect cross-model vector incompatibility.
    /// </summary>
    string ModelId => Name;

    /// <summary>
    /// Embedding scheme name (matches a value from <see cref="SearchEmbeddingModelKind"/>),
    /// lowercase. Lets index manifests record the prefix/pooling family so the same
    /// behaviour can be reproduced on query.
    /// </summary>
    string ModelKind => SearchEmbeddingModelKind.Bert.ToString().ToLowerInvariant();

    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Side-aware overload: lets the provider apply the asymmetric prefix (and, in theory,
    /// pooling head) appropriate for the side of the retrieval pair the call is for.
    /// The default implementation discards the side hint so any
    /// <see cref="ISearchEmbeddingProvider"/> written before 0.10.0 still works unmodified.
    /// </summary>
    Task<float[]> EmbedAsync(string text, SearchEmbeddingSide side, CancellationToken cancellationToken)
        => EmbedAsync(text, cancellationToken);
}
