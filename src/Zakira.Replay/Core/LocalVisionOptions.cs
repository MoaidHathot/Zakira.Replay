using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// Resolved configuration for <see cref="LocalOnnxVisionProvider"/>. Use
/// <see cref="Resolve(ReplayConfig?)"/> to populate from environment variables, config, and
/// derived defaults under the portable directory.
/// </summary>
public sealed record LocalVisionOptions(
    LocalVisionMode Mode,
    string Quantization,
    string? ClipImageEncoderPath,
    string? ClipTextEncoderPath,
    string? ClipKindEmbeddingsPath,
    string? FlorenceVisionEncoderPath,
    string? FlorenceEncoderPath,
    string? FlorenceDecoderPath,
    string? FlorenceEmbedTokensPath,
    string? FlorenceVocabPath,
    string? FlorenceMergesPath,
    string? FlorenceAddedTokensPath,
    int FlorenceMaxTokens,
    bool AutoDownload,
    string ModelDirectory)
{
    public const int DefaultFlorenceMaxTokens = 80;
    public const string DefaultQuantization = "quantized";

    public static IReadOnlyList<string> SupportedQuantizations { get; } =
    [
        "quantized", "int8", "uint8", "fp16", "q4", "q4f16", "bnb4", "full"
    ];

    public const string ClipImageEncoderFile = "clip-image-encoder.onnx";
    public const string ClipTextEncoderFile = "clip-text-encoder.onnx";
    public const string ClipKindEmbeddingsFile = "clip-kind-embeddings.bin";

    public const string FlorenceVisionEncoderFile = "florence-vision-encoder.onnx";
    public const string FlorenceEncoderFile = "florence-encoder.onnx";
    public const string FlorenceDecoderFile = "florence-decoder.onnx";
    public const string FlorenceEmbedTokensFile = "florence-embed-tokens.onnx";
    public const string FlorenceVocabFile = "florence-vocab.json";
    public const string FlorenceMergesFile = "florence-merges.txt";
    public const string FlorenceAddedTokensFile = "florence-added-tokens.json";

    public IReadOnlyList<string> RequiredFilesFor(LocalVisionMode mode)
    {
        var files = new List<string>();
        switch (mode)
        {
            case LocalVisionMode.Clip:
                if (!string.IsNullOrWhiteSpace(ClipImageEncoderPath)) files.Add(ClipImageEncoderPath);
                if (!string.IsNullOrWhiteSpace(ClipKindEmbeddingsPath)) files.Add(ClipKindEmbeddingsPath);
                break;
            case LocalVisionMode.ClipCaption:
                if (!string.IsNullOrWhiteSpace(ClipImageEncoderPath)) files.Add(ClipImageEncoderPath);
                if (!string.IsNullOrWhiteSpace(ClipKindEmbeddingsPath)) files.Add(ClipKindEmbeddingsPath);
                if (!string.IsNullOrWhiteSpace(FlorenceVisionEncoderPath)) files.Add(FlorenceVisionEncoderPath);
                if (!string.IsNullOrWhiteSpace(FlorenceEncoderPath)) files.Add(FlorenceEncoderPath);
                if (!string.IsNullOrWhiteSpace(FlorenceDecoderPath)) files.Add(FlorenceDecoderPath);
                if (!string.IsNullOrWhiteSpace(FlorenceEmbedTokensPath)) files.Add(FlorenceEmbedTokensPath);
                if (!string.IsNullOrWhiteSpace(FlorenceVocabPath)) files.Add(FlorenceVocabPath);
                if (!string.IsNullOrWhiteSpace(FlorenceMergesPath)) files.Add(FlorenceMergesPath);
                break;
        }

        return files;
    }

    public IReadOnlyList<string> MissingFilesFor(LocalVisionMode mode)
    {
        return RequiredFilesFor(mode).Where(p => !File.Exists(p)).ToArray();
    }

    public static string NormalizeQuantization(string? quantization)
    {
        if (string.IsNullOrWhiteSpace(quantization))
        {
            return DefaultQuantization;
        }

        var normalized = quantization.Trim().ToLowerInvariant();
        normalized = normalized switch
        {
            "default" or "q8" or "int-8" => "quantized",
            "f16" or "float16" or "half" => "fp16",
            _ => normalized
        };

        return SupportedQuantizations.Contains(normalized) ? normalized : DefaultQuantization;
    }

    public static string QuantizationSuffix(string quantization)
    {
        var normalized = NormalizeQuantization(quantization);
        return normalized == "full" ? string.Empty : "_" + normalized;
    }

    public static LocalVisionOptions Resolve(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        var mode = VisionProviderFactory.GetConfiguredLocalMode(config);
        var quantization = NormalizeQuantization(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_FLORENCE_QUANTIZATION"),
            config.Vision.Local.FlorenceQuantization));

        var modelDirectory = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_MODEL_DIRECTORY"),
            config.Vision.Local.ModelDirectory);

        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            var installer = new PortableDependencyInstaller(config);
            modelDirectory = Path.Combine(installer.Layout.PortableDirectory, "models", "vision");
        }

        return new LocalVisionOptions(
            Mode: mode,
            Quantization: quantization,
            ClipImageEncoderPath: ResolvePath(config.Vision.Local.ClipImageEncoderPath, "ZAKIRA_REPLAY_VISION_CLIP_IMAGE_ENCODER_PATH", modelDirectory!, ClipImageEncoderFile),
            ClipTextEncoderPath: ResolvePath(config.Vision.Local.ClipTextEncoderPath, "ZAKIRA_REPLAY_VISION_CLIP_TEXT_ENCODER_PATH", modelDirectory!, ClipTextEncoderFile),
            ClipKindEmbeddingsPath: ResolvePath(config.Vision.Local.ClipKindEmbeddingsPath, "ZAKIRA_REPLAY_VISION_CLIP_KIND_EMBEDDINGS_PATH", modelDirectory!, ClipKindEmbeddingsFile),
            FlorenceVisionEncoderPath: ResolvePath(config.Vision.Local.FlorenceVisionEncoderPath, "ZAKIRA_REPLAY_VISION_FLORENCE_VISION_ENCODER_PATH", modelDirectory!, FlorenceVisionEncoderFile),
            FlorenceEncoderPath: ResolvePath(config.Vision.Local.FlorenceEncoderPath, "ZAKIRA_REPLAY_VISION_FLORENCE_ENCODER_PATH", modelDirectory!, FlorenceEncoderFile),
            FlorenceDecoderPath: ResolvePath(config.Vision.Local.FlorenceDecoderPath, "ZAKIRA_REPLAY_VISION_FLORENCE_DECODER_PATH", modelDirectory!, FlorenceDecoderFile),
            FlorenceEmbedTokensPath: ResolvePath(config.Vision.Local.FlorenceEmbedTokensPath, "ZAKIRA_REPLAY_VISION_FLORENCE_EMBED_TOKENS_PATH", modelDirectory!, FlorenceEmbedTokensFile),
            FlorenceVocabPath: ResolvePath(config.Vision.Local.FlorenceVocabPath, "ZAKIRA_REPLAY_VISION_FLORENCE_VOCAB_PATH", modelDirectory!, FlorenceVocabFile),
            FlorenceMergesPath: ResolvePath(config.Vision.Local.FlorenceMergesPath, "ZAKIRA_REPLAY_VISION_FLORENCE_MERGES_PATH", modelDirectory!, FlorenceMergesFile),
            FlorenceAddedTokensPath: ResolvePath(config.Vision.Local.FlorenceAddedTokensPath, "ZAKIRA_REPLAY_VISION_FLORENCE_ADDED_TOKENS_PATH", modelDirectory!, FlorenceAddedTokensFile),
            FlorenceMaxTokens: config.Vision.Local.FlorenceMaxTokens
                ?? ParseEnvInt("ZAKIRA_REPLAY_VISION_FLORENCE_MAX_TOKENS")
                ?? DefaultFlorenceMaxTokens,
            AutoDownload: ParseEnvBool("ZAKIRA_REPLAY_VISION_AUTO_DOWNLOAD") ?? config.Vision.Local.AutoDownload,
            ModelDirectory: modelDirectory!);
    }

    private static string ResolvePath(string? configValue, string envVar, string modelDirectory, string canonicalFile)
    {
        return FirstNonEmpty(Environment.GetEnvironmentVariable(envVar), configValue)
            ?? Path.Combine(modelDirectory, canonicalFile);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static int? ParseEnvInt(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    private static bool? ParseEnvBool(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null
        };
    }
}

