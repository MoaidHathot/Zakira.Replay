namespace Zakira.Replay.Core;

/// <summary>
/// Splits an extracted audio file into bounded chunks at silence boundaries so that
/// downstream STT providers do not hit per-request size limits (for example OpenAI Whisper's
/// 25 MB cap). Audio shorter than the configured target duration is returned as a single chunk
/// pointing at the original file (no copy).
/// </summary>
public sealed class AudioChunker
{
    private readonly IFfmpegClient ffmpeg;

    public AudioChunker(IFfmpegClient ffmpeg)
    {
        this.ffmpeg = ffmpeg;
    }

    public async Task<AudioChunkingResult> ChunkAsync(string audioPath, VideoRun run, AudioChunkingOptions options, CancellationToken cancellationToken)
    {
        var duration = await ffmpeg.TryProbeDurationAsync(audioPath, cancellationToken).ConfigureAwait(false);
        if (duration is null || duration.Value <= options.TargetChunkDurationSeconds + options.OverflowToleranceSeconds)
        {
            var totalDuration = duration ?? 0;
            return new AudioChunkingResult(
                SchemaVersion: "0.7",
                SourceAudioPath: audioPath,
                TotalDurationSeconds: totalDuration,
                Chunks: [new AudioChunk("chunk-001", 0, totalDuration, audioPath)],
                SilenceWindows: []);
        }

        var silenceWindows = await ffmpeg.DetectSilenceAsync(audioPath, options.SilenceDetection, cancellationToken).ConfigureAwait(false);
        var splitPoints = ComputeSplitPoints(duration.Value, silenceWindows, options);
        var chunkDirectory = run.GetPath(Path.Combine("audio", "chunks"));
        Directory.CreateDirectory(chunkDirectory);

        var chunks = new List<AudioChunk>();
        for (var i = 0; i < splitPoints.Count - 1; i++)
        {
            var start = splitPoints[i];
            var end = splitPoints[i + 1];
            var chunkDuration = end - start;
            var chunkId = $"chunk-{i + 1:000}";
            var chunkPath = Path.Combine(chunkDirectory, $"{chunkId}.wav");
            await ffmpeg.ExtractAudioRangeAsync(audioPath, chunkPath, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(chunkDuration), cancellationToken).ConfigureAwait(false);
            chunks.Add(new AudioChunk(chunkId, start, chunkDuration, chunkPath));
        }

        return new AudioChunkingResult(
            SchemaVersion: "0.7",
            SourceAudioPath: audioPath,
            TotalDurationSeconds: duration.Value,
            Chunks: chunks,
            SilenceWindows: silenceWindows);
    }

    /// <summary>
    /// Computes split points for an audio of <paramref name="totalDuration"/> seconds, snapping each
    /// boundary to the silence window whose midpoint lies closest to the ideal step (<see cref="AudioChunkingOptions.TargetChunkDurationSeconds"/>),
    /// within the [<see cref="AudioChunkingOptions.MinChunkDurationSeconds"/>, <see cref="AudioChunkingOptions.MaxChunkDurationSeconds"/>] window.
    /// Falls back to a hard cut when no usable silence boundary is available.
    /// </summary>
    internal static IReadOnlyList<double> ComputeSplitPoints(double totalDuration, IReadOnlyList<SilenceWindow> silenceWindows, AudioChunkingOptions options)
    {
        var points = new List<double> { 0 };
        var current = 0d;
        var minChunk = Math.Max(1, options.MinChunkDurationSeconds);
        var target = Math.Max(minChunk, options.TargetChunkDurationSeconds);
        var maxChunk = Math.Max(target, options.MaxChunkDurationSeconds);

        while (totalDuration - current > maxChunk)
        {
            var minSplit = current + minChunk;
            var maxSplit = current + maxChunk;
            var idealSplit = current + target;
            var candidate = silenceWindows
                .Select(window => new
                {
                    Window = window,
                    Center = window.StartSeconds + (window.DurationSeconds / 2)
                })
                .Where(item => item.Center > minSplit && item.Center < maxSplit)
                .OrderBy(item => Math.Abs(item.Center - idealSplit))
                .ThenByDescending(item => item.Window.DurationSeconds)
                .Select(item => (double?)item.Center)
                .FirstOrDefault();

            var split = candidate ?? idealSplit;
            if (split <= current + (minChunk * 0.5))
            {
                split = idealSplit;
            }

            if (split >= totalDuration)
            {
                break;
            }

            points.Add(split);
            current = split;
        }

        if (totalDuration - current > 0)
        {
            points.Add(totalDuration);
        }

        return points;
    }
}

/// <summary>
/// Result of <see cref="AudioChunker.ChunkAsync"/>. Persisted as <c>audio/chunks/chunks.json</c> when chunking actually splits the audio.
/// </summary>
public sealed record AudioChunkingResult(
    string SchemaVersion,
    string SourceAudioPath,
    double TotalDurationSeconds,
    IReadOnlyList<AudioChunk> Chunks,
    IReadOnlyList<SilenceWindow> SilenceWindows);

/// <summary>
/// A single chunk produced by <see cref="AudioChunker"/>.
/// </summary>
public sealed record AudioChunk(string Id, double StartSeconds, double DurationSeconds, string Path);

/// <summary>
/// Tunables for <see cref="AudioChunker.ChunkAsync"/>.
/// </summary>
/// <param name="TargetChunkDurationSeconds">Preferred chunk duration. Default 600 s; 16 kHz mono PCM at 600 s is ~19 MB, safely below the 25 MB Whisper cap.</param>
/// <param name="MinChunkDurationSeconds">Lower bound for any chunk; prevents tiny tail chunks. Default 60 s.</param>
/// <param name="MaxChunkDurationSeconds">Upper bound before forcing a hard cut. Default 750 s.</param>
/// <param name="OverflowToleranceSeconds">Audio shorter than <see cref="TargetChunkDurationSeconds"/> + this tolerance is returned as a single chunk and not re-encoded. Default 30 s.</param>
/// <param name="SilenceDetection">ffmpeg silencedetect filter parameters.</param>
public sealed record AudioChunkingOptions(
    double TargetChunkDurationSeconds = 600,
    double MinChunkDurationSeconds = 60,
    double MaxChunkDurationSeconds = 750,
    double OverflowToleranceSeconds = 30,
    SilenceDetectionOptions? SilenceDetection = null)
{
    public SilenceDetectionOptions SilenceDetection { get; init; } = SilenceDetection ?? new SilenceDetectionOptions();
}
