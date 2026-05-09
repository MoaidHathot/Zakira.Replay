namespace Zakira.Replay.Core;

internal sealed record SearchEmbeddingConfig(
    string? ModelPath,
    string? VocabularyPath,
    int MaxSequenceLength,
    int? EmbeddingDimensions)
{
    public bool HasOnnxConfiguration => !string.IsNullOrWhiteSpace(ModelPath) && !string.IsNullOrWhiteSpace(VocabularyPath);

    public static SearchEmbeddingConfig From(SearchIndexBuildOptions options)
    {
        return Create(options.OnnxModelPath, options.OnnxVocabularyPath, options.OnnxMaxSequenceLength, options.EmbeddingDimensions);
    }

    public static SearchEmbeddingConfig From(SearchIndexQueryOptions options)
    {
        return Create(options.OnnxModelPath, options.OnnxVocabularyPath, options.OnnxMaxSequenceLength, options.EmbeddingDimensions);
    }

    private static SearchEmbeddingConfig Create(string? modelPath, string? vocabularyPath, int? maxSequenceLength, int? embeddingDimensions)
    {
        var fullConfig = new ConfigStore().Load();
        var config = fullConfig.Search.Onnx;
        var installer = new PortableDependencyInstaller(fullConfig);
        var portableModelPath = File.Exists(installer.GetOnnxModelPath()) ? installer.GetOnnxModelPath() : null;
        var portableVocabularyPath = File.Exists(installer.GetOnnxVocabularyPath()) ? installer.GetOnnxVocabularyPath() : null;
        if (config.AutoDownload
            && !HasExplicitOnnxConfiguration(modelPath, vocabularyPath, config)
            && (string.IsNullOrWhiteSpace(portableModelPath) || string.IsNullOrWhiteSpace(portableVocabularyPath)))
        {
            installer.InstallAsync([PortableDependencyInstaller.Onnx], force: false, progress: null, CancellationToken.None).GetAwaiter().GetResult();
            portableModelPath = File.Exists(installer.GetOnnxModelPath()) ? installer.GetOnnxModelPath() : null;
            portableVocabularyPath = File.Exists(installer.GetOnnxVocabularyPath()) ? installer.GetOnnxVocabularyPath() : null;
        }

        return new SearchEmbeddingConfig(
            FirstNonEmpty(modelPath, Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_PATH"), config.ModelPath, portableModelPath),
            FirstNonEmpty(vocabularyPath, Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCAB_PATH"), Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCABULARY_PATH"), config.VocabularyPath, portableVocabularyPath),
            maxSequenceLength ?? GetEnvInt("ZAKIRA_REPLAY_ONNX_MAX_SEQUENCE_LENGTH") ?? config.MaxSequenceLength ?? 256,
            embeddingDimensions ?? GetEnvInt("ZAKIRA_REPLAY_ONNX_EMBEDDING_DIMENSIONS") ?? config.EmbeddingDimensions);
    }

    private static bool HasExplicitOnnxConfiguration(string? modelPath, string? vocabularyPath, OnnxEmbeddingConfig config)
    {
        return !string.IsNullOrWhiteSpace(modelPath)
            || !string.IsNullOrWhiteSpace(vocabularyPath)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_PATH"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCAB_PATH"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_VOCABULARY_PATH"))
            || !string.IsNullOrWhiteSpace(config.ModelPath)
            || !string.IsNullOrWhiteSpace(config.VocabularyPath);
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
