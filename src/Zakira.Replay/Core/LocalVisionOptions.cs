using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// Resolved configuration for <see cref="LocalOnnxVisionProvider"/>. Use
/// <see cref="Resolve(ReplayConfig?)"/> to populate from environment variables, config, and
/// derived defaults under the portable directory. Mirrors <see cref="LocalOcrModelPaths"/>
/// and <see cref="LocalWhisperOptions"/> in shape.
/// </summary>
public sealed record LocalVisionOptions(
    LocalVisionMode Mode,
    string? ClipImageEncoderPath,
    string? ClipTextEncoderPath,
    string? ClipKindEmbeddingsPath,
    string? BlipImageEncoderPath,
    string? BlipDecoderPath,
    string? BlipVocabPath,
    int BlipMaxTokens,
    bool AutoDownload,
    string ModelDirectory)
{
    /// <summary>Default cap on BLIP decoded caption length, in tokens.</summary>
    public const int DefaultBlipMaxTokens = 40;

    /// <summary>Canonical filenames the installer / runtime expect inside the vision model directory.</summary>
    public const string ClipImageEncoderFile = "clip-image-encoder.onnx";
    public const string ClipTextEncoderFile = "clip-text-encoder.onnx";
    public const string ClipKindEmbeddingsFile = "clip-kind-embeddings.bin";
    public const string BlipImageEncoderFile = "blip-image-encoder.onnx";
    public const string BlipDecoderFile = "blip-decoder.onnx";
    public const string BlipVocabFile = "blip-vocab.txt";

    /// <summary>
    /// Returns the set of files the configured mode actually needs to exist on disk. Used to
    /// emit <c>VISION_LOCAL_MODELS_MISSING</c> with a precise list of missing paths and to
    /// decide whether the mode can run.
    /// </summary>
    public IReadOnlyList<string> RequiredFilesFor(LocalVisionMode mode)
    {
        var files = new List<string>();
        switch (mode)
        {
            case LocalVisionMode.Clip:
                if (!string.IsNullOrWhiteSpace(ClipImageEncoderPath)) files.Add(ClipImageEncoderPath);
                // Either the precomputed embeddings OR the text encoder must exist.
                if (!string.IsNullOrWhiteSpace(ClipKindEmbeddingsPath)) files.Add(ClipKindEmbeddingsPath);
                break;
            case LocalVisionMode.ClipBlip:
                if (!string.IsNullOrWhiteSpace(ClipImageEncoderPath)) files.Add(ClipImageEncoderPath);
                if (!string.IsNullOrWhiteSpace(ClipKindEmbeddingsPath)) files.Add(ClipKindEmbeddingsPath);
                if (!string.IsNullOrWhiteSpace(BlipImageEncoderPath)) files.Add(BlipImageEncoderPath);
                if (!string.IsNullOrWhiteSpace(BlipDecoderPath)) files.Add(BlipDecoderPath);
                if (!string.IsNullOrWhiteSpace(BlipVocabPath)) files.Add(BlipVocabPath);
                break;
        }

        return files;
    }

    /// <summary>
    /// Returns the subset of <see cref="RequiredFilesFor"/> that are not present on disk. Empty
    /// means the mode is ready to run.
    /// </summary>
    public IReadOnlyList<string> MissingFilesFor(LocalVisionMode mode)
    {
        return RequiredFilesFor(mode).Where(p => !File.Exists(p)).ToArray();
    }

    /// <summary>
    /// Resolve all options for the local vision provider. Resolution order per field:
    /// <list type="number">
    ///   <item>Environment variable.</item>
    ///   <item>Explicit config value (<c>vision.local.*</c>).</item>
    ///   <item>Derived default under <c>&lt;ModelDirectory&gt;/&lt;CanonicalFile&gt;</c>.</item>
    /// </list>
    /// </summary>
    public static LocalVisionOptions Resolve(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();

        var mode = VisionProviderFactory.GetConfiguredLocalMode(config);

        var modelDirectory = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_MODEL_DIRECTORY"),
            config.Vision.Local.ModelDirectory);

        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            var installer = new PortableDependencyInstaller(config);
            modelDirectory = Path.Combine(installer.Layout.PortableDirectory, "models", "vision");
        }

        var clipImage = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_CLIP_IMAGE_ENCODER_PATH"),
            config.Vision.Local.ClipImageEncoderPath)
            ?? Path.Combine(modelDirectory!, ClipImageEncoderFile);

        var clipText = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_CLIP_TEXT_ENCODER_PATH"),
            config.Vision.Local.ClipTextEncoderPath)
            ?? Path.Combine(modelDirectory!, ClipTextEncoderFile);

        var clipKindEmbeddings = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_CLIP_KIND_EMBEDDINGS_PATH"),
            config.Vision.Local.ClipKindEmbeddingsPath)
            ?? Path.Combine(modelDirectory!, ClipKindEmbeddingsFile);

        var blipImage = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_BLIP_IMAGE_ENCODER_PATH"),
            config.Vision.Local.BlipImageEncoderPath)
            ?? Path.Combine(modelDirectory!, BlipImageEncoderFile);

        var blipDecoder = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_BLIP_DECODER_PATH"),
            config.Vision.Local.BlipDecoderPath)
            ?? Path.Combine(modelDirectory!, BlipDecoderFile);

        var blipVocab = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_BLIP_VOCAB_PATH"),
            config.Vision.Local.BlipVocabPath)
            ?? Path.Combine(modelDirectory!, BlipVocabFile);

        var blipMaxTokens = config.Vision.Local.BlipMaxTokens
            ?? ParseEnvInt("ZAKIRA_REPLAY_VISION_BLIP_MAX_TOKENS")
            ?? DefaultBlipMaxTokens;

        var autoDownload = ParseEnvBool("ZAKIRA_REPLAY_VISION_AUTO_DOWNLOAD") ?? config.Vision.Local.AutoDownload;

        return new LocalVisionOptions(
            Mode: mode,
            ClipImageEncoderPath: clipImage,
            ClipTextEncoderPath: clipText,
            ClipKindEmbeddingsPath: clipKindEmbeddings,
            BlipImageEncoderPath: blipImage,
            BlipDecoderPath: blipDecoder,
            BlipVocabPath: blipVocab,
            BlipMaxTokens: blipMaxTokens,
            AutoDownload: autoDownload,
            ModelDirectory: modelDirectory!);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
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
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null
        };
    }
}
