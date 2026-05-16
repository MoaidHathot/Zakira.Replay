namespace Zakira.Replay.Core;

/// <summary>
/// Splits an audio file into time ranges and labels each range with a speaker cluster
/// identifier. Out-of-process diarization is performed against pre-recorded audio only —
/// streaming diarization is intentionally out of scope.
/// </summary>
/// <remarks>
/// Diarization is opt-in via <c>--diarize</c> (or <c>request.UseDiarization=true</c>). Results
/// are merged onto the transcript segments produced by the captions / STT step by
/// <see cref="DiarizationMerger"/>; downstream consumers see populated
/// <c>TranscriptSegment.SpeakerId</c> / <c>TranscriptSegment.SpeakerDisplayName</c> fields and
/// the <c>speakers[]</c> registry in <c>evidence.json</c>.
/// </remarks>
public interface IDiarizationProvider
{
    /// <summary>
    /// Diarize a 16 kHz mono PCM WAV file. The file is the same artifact <c>AudioChunker</c>
    /// produces, which the pipeline already extracts via ffmpeg.
    /// </summary>
    /// <returns>
    /// A list of <see cref="DiarizationSegment"/>s, ordered by <see cref="DiarizationSegment.Start"/>.
    /// May contain overlapping segments when the model detects co-occurring speakers.
    /// </returns>
    Task<IReadOnlyList<DiarizationSegment>> DiarizeAsync(
        string audioPath,
        DiarizationOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// A single diarization output: the speaker cluster active during a time range. Cluster
/// identifiers are anonymous (<c>SPEAKER_00</c>, <c>SPEAKER_01</c>, …) — they identify a
/// speaker within a single run and have no cross-run meaning.
/// </summary>
public sealed record DiarizationSegment(TimeSpan Start, TimeSpan End, string SpeakerId)
{
    /// <summary>Convenience: produce a stable cluster ID from sherpa-onnx's integer cluster index.</summary>
    public static string FormatSpeakerId(int clusterIndex) => $"SPEAKER_{clusterIndex:00}";
}

/// <summary>
/// Inputs for <see cref="IDiarizationProvider.DiarizeAsync"/>. All defaults match the
/// pyannote-segmentation 3.0 + 3D-Speaker pipeline that <see cref="SherpaOnnxDiarizationProvider"/>
/// uses.
/// </summary>
public sealed record DiarizationOptions(
    string? SegmentationModelPath,
    string? EmbeddingModelPath,
    int? NumSpeakers = null,
    float? Threshold = null,
    float MinDurationOn = 0.3f,
    float MinDurationOff = 0.5f,
    int Threads = 1)
{
    /// <summary>
    /// Resolve options from environment variables, persistent config, and the installer
    /// layout's diarization directory. Mirrors <see cref="LocalWhisperOptions.Resolve(ReplayConfig?)"/>
    /// and <see cref="LocalOcrModelPaths.Resolve(ReplayConfig?)"/>.
    /// </summary>
    public static DiarizationOptions Resolve(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        var installer = new PortableDependencyInstaller(config);
        var directory = installer.Layout.DiarizationModelDirectory;

        var segmentation = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_SEGMENTATION_MODEL_PATH"),
            config.Diarization.SegmentationModelPath,
            Path.Combine(directory, PortableDependencyInstaller.DiarizationSegmentationFile));

        var embedding = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_EMBEDDING_MODEL_PATH"),
            config.Diarization.EmbeddingModelPath,
            Path.Combine(directory, PortableDependencyInstaller.DiarizationEmbeddingFile));

        int? num = ParsePositiveInt(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_NUM_SPEAKERS"))
            ?? config.Diarization.NumSpeakers;

        float? threshold = ParseFloat(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_THRESHOLD"))
            ?? config.Diarization.Threshold;

        var minOn = ParseFloat(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_ON"))
            ?? config.Diarization.MinDurationOnSeconds ?? 0.3f;

        var minOff = ParseFloat(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_MIN_DURATION_OFF"))
            ?? config.Diarization.MinDurationOffSeconds ?? 0.5f;

        var threads = ParsePositiveInt(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_THREADS"))
            ?? config.Diarization.Threads ?? 1;

        return new DiarizationOptions(segmentation, embedding, num, threshold, minOn, minOff, threads);
    }

    /// <summary>
    /// Validate that both model files exist. Returns the missing path list (empty when OK).
    /// </summary>
    public IReadOnlyList<string> MissingFiles()
    {
        var missing = new List<string>(2);
        if (string.IsNullOrWhiteSpace(SegmentationModelPath) || !File.Exists(SegmentationModelPath))
        {
            missing.Add(SegmentationModelPath ?? "<segmentation model unset>");
        }

        if (string.IsNullOrWhiteSpace(EmbeddingModelPath) || !File.Exists(EmbeddingModelPath))
        {
            missing.Add(EmbeddingModelPath ?? "<embedding model unset>");
        }

        return missing;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static int? ParsePositiveInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static float? ParseFloat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// Stable identifiers for diarization providers. Persisted in
/// <c>manifest.diarizationProvider</c> when diarization runs.
/// </summary>
public static class DiarizationProviders
{
    /// <summary>
    /// Local sherpa-onnx (pyannote segmentation + 3D-Speaker embedding + clustering). No
    /// network at run-time after models are installed.
    /// </summary>
    public const string SherpaOnnx = "sherpa-onnx";
}

/// <summary>
/// Resolves an <see cref="IDiarizationProvider"/> implementation. Mirrors
/// <see cref="OcrProviderFactory"/>.
/// </summary>
public static class DiarizationProviderFactory
{
    public static string GetConfiguredProvider(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return Normalize(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_PROVIDER") ?? config.Diarization.Provider);
    }

    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return DiarizationProviders.SherpaOnnx;
        }

        return provider.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "sherpa" or "sherpa-onnx" or "sherpaonnx" or "local" or "pyannote" => DiarizationProviders.SherpaOnnx,
            var value => value
        };
    }
}
