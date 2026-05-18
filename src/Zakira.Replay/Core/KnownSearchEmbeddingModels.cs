namespace Zakira.Replay.Core;

/// <summary>
/// Registry of search-embedding models known to <c>zakira-replay deps install onnx</c>.
/// Each entry knows where to fetch the ONNX model and tokenizer files from Hugging Face,
/// which on-disk file name to expect, which embedding scheme (<see cref="SearchEmbeddingModelKind"/>)
/// it implements, and which extra files (if any) need to land alongside for the chosen
/// tokenizer to load correctly.
///
/// New entries can be added without changing the installer code; the installer iterates the
/// <see cref="KnownSearchEmbeddingModel.Files"/> list. Custom user models still work via the
/// <c>search.onnx.modelPath</c> + <c>search.onnx.vocabularyPath</c> path overrides without
/// being listed here.
/// </summary>
public static class KnownSearchEmbeddingModels
{
    /// <summary>Default model for the 0.10.0 cohort. English-only, 384-dim, ~33 MB ONNX.</summary>
    public const string BgeSmallEnV15 = "bge-small-en-v1.5";

    /// <summary>Snowflake's small English model, 384-dim, ~33 MB ONNX. CLS pooling.</summary>
    public const string SnowflakeArcticEmbedS = "snowflake-arctic-embed-s";

    /// <summary>Multilingual fallback. 384-dim, ~118 MB ONNX, XLM-R SentencePiece tokenizer.</summary>
    public const string MultilingualE5Small = "multilingual-e5-small";

    /// <summary>Default <see cref="OnnxEmbeddingConfig.Model"/> value when none is configured.</summary>
    public const string DefaultModel = BgeSmallEnV15;

    private static readonly IReadOnlyDictionary<string, KnownSearchEmbeddingModel> Registry = new Dictionary<string, KnownSearchEmbeddingModel>(StringComparer.OrdinalIgnoreCase)
    {
        [BgeSmallEnV15] = new KnownSearchEmbeddingModel(
            Id: BgeSmallEnV15,
            RepositoryBaseUrl: "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main",
            DirectoryName: BgeSmallEnV15,
            ModelKind: SearchEmbeddingModelKind.Bge,
            EmbeddingDimensions: 384,
            MaxSequenceLength: 512,
            TokenizerFileName: "vocab.txt",
            Files:
            [
                new KnownSearchModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new KnownSearchModelFile("vocab.txt", "vocab.txt"),
                new KnownSearchModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new KnownSearchModelFile("config.json", "config.json")
            ]),

        [SnowflakeArcticEmbedS] = new KnownSearchEmbeddingModel(
            Id: SnowflakeArcticEmbedS,
            // Snowflake doesn't publish ONNX themselves; the onnx-community/ mirror is the
            // canonical export for transformers.js consumers and what we re-distribute here.
            RepositoryBaseUrl: "https://huggingface.co/onnx-community/snowflake-arctic-embed-s/resolve/main",
            DirectoryName: SnowflakeArcticEmbedS,
            ModelKind: SearchEmbeddingModelKind.Bge,
            EmbeddingDimensions: 384,
            MaxSequenceLength: 512,
            TokenizerFileName: "vocab.txt",
            Files:
            [
                new KnownSearchModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new KnownSearchModelFile("vocab.txt", "vocab.txt"),
                new KnownSearchModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new KnownSearchModelFile("config.json", "config.json")
            ]),

        [MultilingualE5Small] = new KnownSearchEmbeddingModel(
            Id: MultilingualE5Small,
            RepositoryBaseUrl: "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main",
            DirectoryName: MultilingualE5Small,
            ModelKind: SearchEmbeddingModelKind.E5,
            EmbeddingDimensions: 384,
            MaxSequenceLength: 512,
            // XLM-RoBERTa SentencePiece binary model file. The tokenizer loader detects this
            // by the .model extension and switches to SentencePieceTokenizer automatically.
            TokenizerFileName: "sentencepiece.bpe.model",
            Files:
            [
                new KnownSearchModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new KnownSearchModelFile("sentencepiece.bpe.model", "sentencepiece.bpe.model"),
                new KnownSearchModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new KnownSearchModelFile("config.json", "config.json")
            ])
    };

    public static IReadOnlyCollection<string> Ids => Registry.Keys.ToArray();

    public static IReadOnlyCollection<KnownSearchEmbeddingModel> All => Registry.Values.ToArray();

    public static KnownSearchEmbeddingModel Get(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? DefaultModel : modelId.Trim();
        if (Registry.TryGetValue(id, out var entry))
        {
            return entry;
        }

        throw new ReplayException(
            $"Unknown search-embedding model id: '{modelId}'. Known ids: {string.Join(", ", Registry.Keys)}. " +
            "For custom models, set search.onnx.modelPath and search.onnx.tokenizerPath (or pass --onnx-model-path / --onnx-tokenizer-path).");
    }

    public static bool TryGet(string? modelId, out KnownSearchEmbeddingModel model)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && Registry.TryGetValue(modelId!.Trim(), out var entry))
        {
            model = entry;
            return true;
        }

        model = null!;
        return false;
    }
}

/// <summary>
/// Metadata for a known search-embedding model: where to download it from, what files it
/// requires on disk, and which embedding scheme it implements.
/// </summary>
/// <param name="Id">Stable identifier; persisted into sqlite-onnx index metadata so a later
///   query can detect cross-model vector incompatibility.</param>
/// <param name="RepositoryBaseUrl">Base URL all <see cref="Files"/> are downloaded under.
///   Typically a Hugging Face <c>/resolve/main</c> URL.</param>
/// <param name="DirectoryName">Sub-directory name under <c>&lt;portable&gt;/models/</c>;
///   multiple known models can coexist on disk without colliding.</param>
/// <param name="ModelKind">Embedding scheme (prefix + pooling) the provider uses for this model.</param>
/// <param name="EmbeddingDimensions">Vector width the model emits. Useful for the index manifest
///   sanity check.</param>
/// <param name="MaxSequenceLength">Tokenizer max sequence length the model was trained with.</param>
/// <param name="TokenizerFileName">Name of the tokenizer file inside <see cref="DirectoryName"/>
///   that <see cref="OnnxSearchEmbeddingProvider.LoadTokenizer"/> consumes — <c>vocab.txt</c>
///   for BERT-family models, <c>sentencepiece.bpe.model</c> for XLM-R-family models.</param>
/// <param name="Files">Files to download. The relative URL is appended to
///   <see cref="RepositoryBaseUrl"/>; the local name is written under <see cref="DirectoryName"/>.</param>
public sealed record KnownSearchEmbeddingModel(
    string Id,
    string RepositoryBaseUrl,
    string DirectoryName,
    SearchEmbeddingModelKind ModelKind,
    int EmbeddingDimensions,
    int MaxSequenceLength,
    string TokenizerFileName,
    IReadOnlyList<KnownSearchModelFile> Files);

/// <summary>Single artifact file inside a <see cref="KnownSearchEmbeddingModel"/>.</summary>
/// <param name="LocalName">File name written under the model's local directory.</param>
/// <param name="RemotePath">Path appended to the model's
///   <see cref="KnownSearchEmbeddingModel.RepositoryBaseUrl"/> to fetch the file.</param>
public sealed record KnownSearchModelFile(string LocalName, string RemotePath);
