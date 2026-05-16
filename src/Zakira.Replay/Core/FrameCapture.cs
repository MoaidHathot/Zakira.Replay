using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// Ad-hoc frame capture: pull JPEGs at specific timestamps or inside a fixed time window,
/// without paying for the full <see cref="AnalysisPipeline"/> (no slide grouping, hashing,
/// OCR, vision, alignment, or chapter synthesis).
///
/// <para>
/// Intended consumers are external agents (LLM/MCP clients, scripts) that have already
/// "watched" a video via the full pipeline and now want specific stills - e.g. a recipe
/// agent grabbing photos at moments where new ingredients are mixed in - or that just want
/// a handful of spot frames without committing to a full analysis run.
/// </para>
/// </summary>
public interface IFrameCaptureService
{
    Task<FrameCaptureResult> CaptureAsync(FrameCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed class FrameCaptureService : IFrameCaptureService
{
    /// <summary>
    /// Hard upper bound on the number of timestamps a single capture request can ask for.
    /// Excess timestamps are dropped with <see cref="ReplayWarningCodes.FrameCaptureTooManyTimestamps"/>.
    /// Bounded so a misbehaving agent can't accidentally pin ffmpeg into producing
    /// thousands of files.
    /// </summary>
    public const int MaxTimestampsPerRequest = 64;

    private const int DefaultSceneSafetyCap = 200;

    private readonly ArtifactStore artifactStore;
    private readonly IYtDlpClient ytDlp;
    private readonly IFfmpegClient ffmpeg;

    public FrameCaptureService(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
    }

    public async Task<FrameCaptureResult> CaptureAsync(FrameCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ValidateInputShape(request);

        SourceLocator.ThrowIfMissingLocalPathLikeSource(request.Source);
        var isLocalFile = SourceLocator.TryGetLocalFilePath(request.Source, out var localPath);
        var run = artifactStore.CreateRun(request.Source, request.RunId);
        var warnings = new List<ReplayWarning>();

        progress?.Report($"Run directory: {run.Directory}");

        var mediaSource = isLocalFile ? localPath : await ResolveRemoteMediaAsync(request, run, warnings, progress, cancellationToken).ConfigureAwait(false);
        if (mediaSource is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.FrameCaptureMediaUrlUnresolved,
                "Could not resolve media for frame capture.",
                Source: "yt-dlp",
                Severity: ReplayWarningSeverities.Error));
            return await PersistAsync(request, run, frames: [], warnings, cancellationToken).ConfigureAwait(false);
        }

        var duration = await ffmpeg.TryProbeDurationAsync(mediaSource, cancellationToken).ConfigureAwait(false);
        var ffmpegOptions = new FrameCaptureOptions(request.MaxLongEdgePixels, request.JpegQuality);

        IReadOnlyList<FrameArtifact> frames;
        if (request.Timestamps is { Count: > 0 })
        {
            var (effectiveTimestamps, timestampWarnings) = ClampTimestamps(request.Timestamps, duration);
            warnings.AddRange(timestampWarnings);

            if (effectiveTimestamps.Count == 0)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FrameCaptureNoFrames,
                    "All requested timestamps were rejected; no frames were captured.",
                    Source: "frame-capture",
                    Severity: ReplayWarningSeverities.Error));
                return await PersistAsync(request, run, frames: [], warnings, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report($"Capturing {effectiveTimestamps.Count} frame(s) at requested timestamps...");
            frames = await ffmpeg.ExtractFramesAtAsync(mediaSource, run, effectiveTimestamps, ffmpegOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var rangeStart = request.RangeStart!.Value;
            var rangeEnd = request.RangeEnd!.Value;
            var (effectiveStart, effectiveEnd, rangeWarnings) = ClampRange(rangeStart, rangeEnd, duration);
            warnings.AddRange(rangeWarnings);

            if (effectiveEnd <= effectiveStart)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FrameCaptureRangeOutOfBounds,
                    $"Range [{rangeStart}, {rangeEnd}] resolves to an empty window after clamping to the source duration.",
                    Source: "frame-capture",
                    Severity: ReplayWarningSeverities.Error));
                return await PersistAsync(request, run, frames: [], warnings, cancellationToken).ConfigureAwait(false);
            }

            var strategy = NormalizeRangeStrategy(request.RangeStrategy);
            if (strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase))
            {
                var safetyCap = request.SceneSafetyCap ?? Math.Max(1, request.RangeCount ?? DefaultSceneSafetyCap);
                progress?.Report($"Detecting scene cuts in [{Timestamp.Format(effectiveStart.TotalSeconds)}, {Timestamp.Format(effectiveEnd.TotalSeconds)}] (cap {safetyCap})...");
                frames = await ffmpeg.ExtractSceneFramesInRangeAsync(mediaSource, run, effectiveStart, effectiveEnd, safetyCap, ffmpegOptions, cancellationToken).ConfigureAwait(false);
                if (request.RangeCount is int requestedCount && requestedCount > 0 && frames.Count > requestedCount)
                {
                    frames = frames.Take(requestedCount).ToList();
                }

                if (frames.Count >= safetyCap)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.FrameCaptureSceneCapReached,
                        $"Scene-detection safety cap ({safetyCap}) reached inside the requested window. Some scene cuts may be missing.",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Warning));
                }
            }
            else
            {
                var count = Math.Max(1, request.RangeCount ?? 1);
                var timestamps = BuildIntervalTimestamps(effectiveStart, effectiveEnd, count);
                progress?.Report($"Capturing {timestamps.Count} interval frame(s) across [{Timestamp.Format(effectiveStart.TotalSeconds)}, {Timestamp.Format(effectiveEnd.TotalSeconds)}]...");
                frames = await ffmpeg.ExtractFramesAtAsync(mediaSource, run, timestamps, ffmpegOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        if (frames.Count == 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.FrameCaptureNoFrames,
                "ffmpeg returned no frames for the requested capture.",
                Source: "ffmpeg",
                Severity: ReplayWarningSeverities.Warning));
        }

        if (request.ComputePerceptualHash)
        {
            frames = await EnrichWithPerceptualHashAsync(run, frames, cancellationToken).ConfigureAwait(false);
        }

        return await PersistAsync(request, run, frames, warnings, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateInputShape(FrameCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ReplayException("Frame capture requires a non-empty `source`.");
        }

        var hasTimestamps = request.Timestamps is { Count: > 0 };
        var hasRange = request.RangeStart is not null && request.RangeEnd is not null;
        if (hasTimestamps && hasRange)
        {
            throw new ReplayException("Frame capture accepts either `timestamps` or `rangeStart`/`rangeEnd`, not both.");
        }

        if (!hasTimestamps && !hasRange)
        {
            throw new ReplayException("Frame capture requires either `timestamps` or `rangeStart`/`rangeEnd`.");
        }

        if (hasRange)
        {
            if (request.RangeEnd!.Value <= request.RangeStart!.Value)
            {
                throw new ReplayException("Frame capture `rangeEnd` must be after `rangeStart`.");
            }

            if (request.RangeCount is int count && count < 1)
            {
                throw new ReplayException("Frame capture `rangeCount` must be at least 1.");
            }
        }

        if (request.JpegQuality is int quality && (quality < 1 || quality > 100))
        {
            throw new ReplayException("Frame capture `jpegQuality` must be in the range [1, 100].");
        }

        if (request.MaxLongEdgePixels is int edge && edge < 16)
        {
            throw new ReplayException("Frame capture `maxLongEdgePixels` must be at least 16.");
        }
    }

    private async Task<string?> ResolveRemoteMediaAsync(FrameCaptureRequest request, VideoRun run, List<ReplayWarning> warnings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // Mirror ClipExtractionService: try a direct media URL first, then fall back to a
        // local download. The pipeline's AnalyzeRequest is the data-transfer shape yt-dlp
        // expects, so we synthesise a minimal one.
        var analyzeRequest = new AnalyzeRequest(
            Source: request.Source,
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 0,
            RunId: request.RunId,
            OcrInstruction: string.Empty,
            CookiesPath: request.CookiesPath,
            CookiesFromBrowser: request.CookiesFromBrowser);

        progress?.Report("Resolving direct media URL for ffmpeg...");
        var mediaUrl = await ytDlp.GetBestMediaUrlAsync(analyzeRequest, cancellationToken).ConfigureAwait(false);
        if (mediaUrl is not null)
        {
            return mediaUrl;
        }

        warnings.Add(new ReplayWarning(
            ReplayWarningCodes.FrameCaptureMediaUrlUnresolved,
            "Could not resolve a direct media URL; downloading media locally for frame capture.",
            Source: "yt-dlp",
            Severity: ReplayWarningSeverities.Info));
        return await ytDlp.DownloadMediaForProcessingAsync(analyzeRequest, run, cancellationToken).ConfigureAwait(false);
    }

    private static (IReadOnlyList<TimeSpan> Effective, IReadOnlyList<ReplayWarning> Warnings) ClampTimestamps(IReadOnlyList<TimeSpan> requested, double? duration)
    {
        var warnings = new List<ReplayWarning>();
        var trimmed = requested;
        if (requested.Count > MaxTimestampsPerRequest)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.FrameCaptureTooManyTimestamps,
                $"{requested.Count} timestamps were requested; only the first {MaxTimestampsPerRequest} will be captured.",
                Source: "frame-capture"));
            trimmed = requested.Take(MaxTimestampsPerRequest).ToList();
        }

        var effective = new List<TimeSpan>(trimmed.Count);
        foreach (var t in trimmed)
        {
            if (t < TimeSpan.Zero)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FrameCaptureTimestampOutOfRange,
                    $"Timestamp {t} is negative and was dropped.",
                    Source: "frame-capture"));
                continue;
            }

            if (duration is double d && t.TotalSeconds > d + 0.001)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FrameCaptureTimestampOutOfRange,
                    $"Timestamp {Timestamp.Format(t.TotalSeconds)} exceeds source duration ({Timestamp.Format(d)}) and was dropped.",
                    Source: "frame-capture"));
                continue;
            }

            effective.Add(t);
        }

        return (effective, warnings);
    }

    private static (TimeSpan Start, TimeSpan End, IReadOnlyList<ReplayWarning> Warnings) ClampRange(TimeSpan start, TimeSpan end, double? duration)
    {
        var warnings = new List<ReplayWarning>();
        var clampedStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        var clampedEnd = end;
        if (duration is double d)
        {
            var max = TimeSpan.FromSeconds(d);
            if (clampedEnd > max)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FrameCaptureRangeOutOfBounds,
                    $"Range end {Timestamp.Format(end.TotalSeconds)} exceeds source duration ({Timestamp.Format(d)}); clamping to end of source.",
                    Source: "frame-capture"));
                clampedEnd = max;
            }

            if (clampedStart > max)
            {
                clampedStart = max;
            }
        }

        if (clampedStart != start || clampedEnd != end)
        {
            // Already added a single warning above for end clamp; start clamp without a warning
            // is intentional (negatives are caller error already covered by validation).
        }

        return (clampedStart, clampedEnd, warnings);
    }

    private static List<TimeSpan> BuildIntervalTimestamps(TimeSpan start, TimeSpan end, int count)
    {
        var timestamps = new List<TimeSpan>(count);
        if (count == 1)
        {
            timestamps.Add(TimeSpan.FromSeconds((start.TotalSeconds + end.TotalSeconds) / 2.0));
            return timestamps;
        }

        // Endpoints are inclusive: evenly space `count` timestamps from start to end.
        var spanSeconds = end.TotalSeconds - start.TotalSeconds;
        for (var i = 0; i < count; i++)
        {
            var t = start.TotalSeconds + (spanSeconds * i / (count - 1));
            timestamps.Add(TimeSpan.FromSeconds(t));
        }

        return timestamps;
    }

    private static string NormalizeRangeStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
        {
            return FrameSelectionStrategies.Interval;
        }

        if (strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase))
        {
            return FrameSelectionStrategies.Scene;
        }

        if (strategy.Equals(FrameSelectionStrategies.Interval, StringComparison.OrdinalIgnoreCase))
        {
            return FrameSelectionStrategies.Interval;
        }

        throw new ReplayException($"Unknown range strategy `{strategy}`. Use `interval` or `scene`.");
    }

    private async Task<IReadOnlyList<FrameArtifact>> EnrichWithPerceptualHashAsync(VideoRun run, IReadOnlyList<FrameArtifact> frames, CancellationToken cancellationToken)
    {
        var hashed = new List<FrameArtifact>(frames.Count);
        foreach (var frame in frames)
        {
            var hash = await ffmpeg.ComputePerceptualHashAsync(run.GetPath(frame.Path), cancellationToken).ConfigureAwait(false);
            hashed.Add(frame with { PerceptualHash = hash });
        }

        return hashed;
    }

    private async Task<FrameCaptureResult> PersistAsync(FrameCaptureRequest request, VideoRun run, IReadOnlyList<FrameArtifact> frames, IReadOnlyList<ReplayWarning> warnings, CancellationToken cancellationToken)
    {
        var manifest = new FrameCaptureManifest(
            SchemaVersion: "0.1",
            Kind: "frame-capture",
            Source: request.Source,
            RunId: run.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            Request: SummarizeRequest(request),
            Frames: frames,
            Warnings: warnings);

        await artifactStore.WriteJsonAsync(run, "frame-capture.json", manifest, cancellationToken).ConfigureAwait(false);
        return new FrameCaptureResult(run, manifest);
    }

    private static FrameCaptureRequestSummary SummarizeRequest(FrameCaptureRequest request)
    {
        var isTimestampsMode = request.Timestamps is { Count: > 0 };
        return new FrameCaptureRequestSummary(
            Mode: isTimestampsMode ? FrameSelectionStrategies.Timestamps : FrameSelectionStrategies.Range,
            Timestamps: isTimestampsMode
                ? request.Timestamps!.Select(t => Timestamp.Format(t.TotalSeconds)).ToArray()
                : null,
            RangeStart: request.RangeStart is { } start ? Timestamp.Format(start.TotalSeconds) : null,
            RangeEnd: request.RangeEnd is { } end ? Timestamp.Format(end.TotalSeconds) : null,
            RangeCount: request.RangeCount,
            RangeStrategy: isTimestampsMode ? null : NormalizeRangeStrategy(request.RangeStrategy),
            MaxLongEdgePixels: request.MaxLongEdgePixels,
            JpegQuality: request.JpegQuality,
            ComputePerceptualHash: request.ComputePerceptualHash);
    }
}

/// <summary>
/// A contiguous time window inside a media source. Helper value type shared by the various
/// range-aware operations. Use <see cref="Timestamp.ParseRequired"/> to parse human-supplied
/// <c>MM:SS</c>/<c>HH:MM:SS</c> strings into the constituent <see cref="TimeSpan"/> values.
/// </summary>
public sealed record TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public bool Contains(TimeSpan timestamp) => timestamp >= Start && timestamp <= End;
}

/// <summary>
/// Input for <see cref="IFrameCaptureService.CaptureAsync"/>. Either supply <see cref="Timestamps"/>
/// (exact picks) or <see cref="RangeStart"/> + <see cref="RangeEnd"/> (with <see cref="RangeCount"/>
/// and <see cref="RangeStrategy"/>), but not both.
/// </summary>
public sealed record FrameCaptureRequest(
    string Source,
    IReadOnlyList<TimeSpan>? Timestamps = null,
    TimeSpan? RangeStart = null,
    TimeSpan? RangeEnd = null,
    int? RangeCount = null,
    string RangeStrategy = FrameSelectionStrategies.Interval,
    string? RunId = null,
    int? MaxLongEdgePixels = null,
    int? JpegQuality = null,
    bool ComputePerceptualHash = false,
    int? SceneSafetyCap = null,
    string? CookiesPath = null,
    string? CookiesFromBrowser = null);

/// <summary>
/// Minimal manifest written to <c>runs/&lt;run-id&gt;/frame-capture.json</c> documenting an
/// ad-hoc capture. <see cref="Kind"/> is always <c>"frame-capture"</c> so consumers can tell
/// this apart from a full analyze run's <c>manifest.json</c> without inspecting other files.
/// </summary>
public sealed record FrameCaptureManifest(
    string SchemaVersion,
    string Kind,
    string Source,
    string RunId,
    DateTimeOffset CreatedAt,
    FrameCaptureRequestSummary Request,
    IReadOnlyList<FrameArtifact> Frames,
    IReadOnlyList<ReplayWarning> Warnings);

/// <summary>
/// Human-readable summary of the request inputs that produced a capture manifest. Timestamps
/// are rendered as <c>MM:SS</c>/<c>HH:MM:SS</c> for stable diffing across runs.
/// </summary>
public sealed record FrameCaptureRequestSummary(
    string Mode,
    IReadOnlyList<string>? Timestamps,
    string? RangeStart,
    string? RangeEnd,
    int? RangeCount,
    string? RangeStrategy,
    int? MaxLongEdgePixels,
    int? JpegQuality,
    bool ComputePerceptualHash);

public sealed record FrameCaptureResult(VideoRun Run, FrameCaptureManifest Manifest);

/// <summary>
/// Helper utilities for parsing collections of timestamps from CLI/MCP inputs. The CLI
/// accepts comma- or semicolon-separated lists; the MCP server accepts JSON arrays of either
/// strings or numbers.
/// </summary>
public static class FrameCaptureInput
{
    public static IReadOnlyList<TimeSpan> ParseTimestamps(string raw, string optionName = "timestamps")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var parts = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<TimeSpan>(parts.Length);
        foreach (var part in parts)
        {
            result.Add(Timestamp.ParseRequired(part, optionName));
        }

        return result;
    }

    public static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
