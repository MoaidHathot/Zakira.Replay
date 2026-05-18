namespace Zakira.Replay.Core;

/// <summary>
/// Resolves the ONNX embedding model configuration in a single place so the build/query
/// pipelines and `info` / `doctor` all see the same numbers.
///
/// Resolution precedence (highest wins) per field:
/// <list type="number">
///   <item><description>Explicit per-call option (CLI flag or MCP parameter).</description></item>
///   <item><description>Environment variable (<c>ZAKIRA_REPLAY_ONNX_*</c>).</description></item>
///   <item><description>User config (<c>search.onnx.*</c>).</description></item>
///   <item><description>Known-model registry default (when a known
///     <c>search.onnx.model</c> is resolved).</description></item>
///   <item><description>Hard-coded fallback constants.</description></item>
/// </list>
/// </summary>
internal sealed record SearchEmbeddingConfig(
    string? ModelPath,
    string? TokenizerPath,
    int MaxSequenceLength,
    int? EmbeddingDimensions,
    string ModelId,
    SearchEmbeddingModelKind ModelKind)
{
    public bool HasOnnxConfiguration => !string.IsNullOrWhiteSpace(ModelPath) && !string.IsNullOrWhiteSpace(TokenizerPath);

    public static SearchEmbeddingConfig From(SearchIndexBuildOptions options)
    {
        return Create(options.OnnxModelPath, options.OnnxVocabularyPath, options.OnnxTokenizerPath, options.OnnxMaxSequenceLength, options.EmbeddingDimensions, options.OnnxModel, options.OnnxModelKind);
    }

    public static SearchEmbeddingConfig From(SearchIndexQueryOptions options)
    {
        return Create(options.OnnxModelPath, options.OnnxVocabularyPath, options.OnnxTokenizerPath, options.OnnxMaxSequenceLength, options.EmbeddingDimensions, options.OnnxModel, options.OnnxModelKind);
    }

    private static SearchEmbeddingConfig Create(
        string? modelPath,
        string? vocabularyPath,
        string? tokenizerPath,
        int? maxSequenceLength,
        int? embeddingDimensions,
        string? modelId,
        string? modelKind)
    {
        var fullConfig = new ConfigStore().Load();
        var config = fullConfig.Search.Onnx;

        // 1. Decide which known-model entry (if any) applies. The id resolution itself follows
        // its own precedence: explicit > env var > config > registry default.
        var resolvedModelId = FirstNonEmpty(
            modelId,
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL"),
            config.Model,
            KnownSearchEmbeddingModels.DefaultModel)!;
        var entry = KnownSearchEmbeddingModels.TryGet(resolvedModelId, out var registryEntry) ? registryEntry : null;

        // 2. Resolve installation layout against that model id. The installer lays each
        // model out in its own sub-directory so multiple known models coexist on disk.
        var installer = new PortableDependencyInstaller(fullConfig);
        var portableModelPath = File.Exists(installer.GetOnnxModelPath()) ? installer.GetOnnxModelPath() : null;
        var portableTokenizerPath = File.Exists(installer.GetOnnxTokenizerPath()) ? installer.GetOnnxTokenizerPath() : null;

        // 3. Auto-download is opt-in via search.onnx.autoDownload (default false for the
        // legacy bundled model; intentionally still off for known models so new users see
        // the download cost explicitly via `deps install onnx`). When auto-download is on
        // and no portable copy exists, kick the installer here so the first build/query
        // doesn't fail.
        if (config.AutoDownload
            && !HasExplicitOnnxConfiguration(modelPath, vocabularyPath, tokenizerPath, config)
            && entry is not null
            && (string.IsNullOrWhiteSpace(portableModelPath) || string.IsNullOrWhiteSpace(portableTokenizerPath)))
        {
            installer.InstallAsync([PortableDependencyInstaller.Onnx], force: false, progress: null, CancellationToken.None).GetAwaiter().GetResult();
            portableModelPath = File.Exists(installer.GetOnnxModelPath()) ? installer.GetOnnxModelPath() : null;
            portableTokenizerPath = File.Exists(installer.GetOnnxTokenizerPath()) ? installer.GetOnnxTokenizerPath() : null;
        }

        var resolvedKind = OnnxSearchEmbeddingProvider.ResolveKind(
            resolvedModelId,
            FirstNonEmpty(modelKind, Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_KIND"), config.ModelKind));

        // The historical `--onnx-vocab` / search.onnx.vocabularyPath knobs are kept as
        // aliases for the new tokenizer-path slot so 0.9.x configs and CLI invocations keep
        // working unchanged for the BERT-family models.
        var resolvedTokenizerPath = FirstNonEmpty(
            tokenizerPath,
            vocabularyPath,
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH"),
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCAB_PATH"),
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCABULARY_PATH"),
            config.TokenizerPath,
            config.VocabularyPath,
            portableTokenizerPath);

        return new SearchEmbeddingConfig(
            ModelPath: FirstNonEmpty(modelPath, Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_PATH"), config.ModelPath, portableModelPath),
            TokenizerPath: resolvedTokenizerPath,
            MaxSequenceLength: maxSequenceLength ?? GetEnvInt("ZAKIRA_REPLAY_ONNX_MAX_SEQUENCE_LENGTH") ?? config.MaxSequenceLength ?? entry?.MaxSequenceLength ?? 256,
            EmbeddingDimensions: embeddingDimensions ?? GetEnvInt("ZAKIRA_REPLAY_ONNX_EMBEDDING_DIMENSIONS") ?? config.EmbeddingDimensions ?? entry?.EmbeddingDimensions,
            ModelId: resolvedModelId,
            ModelKind: resolvedKind);
    }

    private static bool HasExplicitOnnxConfiguration(string? modelPath, string? vocabularyPath, string? tokenizerPath, OnnxEmbeddingConfig config)
    {
        return !string.IsNullOrWhiteSpace(modelPath)
            || !string.IsNullOrWhiteSpace(vocabularyPath)
            || !string.IsNullOrWhiteSpace(tokenizerPath)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_PATH"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCAB_PATH"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCABULARY_PATH"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH"))
            || !string.IsNullOrWhiteSpace(config.ModelPath)
            || !string.IsNullOrWhiteSpace(config.VocabularyPath)
            || !string.IsNullOrWhiteSpace(config.TokenizerPath);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static int? GetEnvInt(string name)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : null;
    }
}
