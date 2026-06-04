namespace Zakira.Replay.Core;

public sealed class ClipExtractionService
{
    private readonly ArtifactStore artifactStore;
    private readonly IYtDlpClient ytDlp;
    private readonly IFfmpegClient ffmpeg;

    public ClipExtractionService(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
    }

    public async Task<ClipExtractionResult> ExtractAsync(ClipExtractionRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        SourceLocator.ThrowIfMissingLocalPathLikeSource(request.Source);
        var isLocalFile = SourceLocator.TryGetLocalFilePath(request.Source, out var localPath);
        var run = artifactStore.CreateRun(request.Source, request.RunId);
        var warnings = new List<ReplayWarning>();

        progress?.Report($"Run directory: {run.Directory}");
        var mediaSource = isLocalFile ? localPath : (string?)null;
        if (!isLocalFile)
        {
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
            mediaSource = await ytDlp.GetBestMediaUrlAsync(analyzeRequest, cancellationToken).ConfigureAwait(false);
            if (mediaSource is null)
            {
                // Clip extraction inherently needs to re-encode a byte range — no inline
                // sidestep helps here, so a download is the only path. Gate it on the
                // request's opt-in (or the global config default); when declined, fail with
                // a clear actionable error instead of silently pulling a multi-GB video.
                var allowMediaDownload = request.AllowMediaDownload || new ConfigStore().Load().Capture.AllowMediaDownload;
                if (!allowMediaDownload)
                {
                    throw new ReplayException(
                        "Clip extraction needs to download the source media locally because no direct URL was reachable, but --allow-media-download is not set (and capture.allowMediaDownload is false in config). Pass --allow-media-download to opt in.");
                }
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.ClipMediaUrlUnresolved,
                    "Could not resolve a direct media URL; downloading media locally for clipping (--allow-media-download was set).",
                    Source: "yt-dlp",
                    Severity: ReplayWarningSeverities.Info));
                mediaSource = await ytDlp.DownloadMediaForProcessingAsync(analyzeRequest, run, cancellationToken).ConfigureAwait(false);
            }
        }

        if (mediaSource is null)
        {
            throw new ReplayException("Could not resolve media for clip extraction.");
        }

        progress?.Report($"Extracting clip {request.Start} to {request.End}...");
        var clipPath = await ffmpeg.ExtractClipAsync(mediaSource, run, request.Start, request.End, request.OutputName, cancellationToken).ConfigureAwait(false);
        var manifest = new ClipManifest("0.2", request.Source, run.Id, request.Start, request.End, clipPath, warnings);
        await artifactStore.WriteJsonAsync(run, "clip.json", manifest, cancellationToken).ConfigureAwait(false);
        return new ClipExtractionResult(run, manifest);
    }
}

public sealed record ClipExtractionRequest(
    string Source,
    TimeSpan Start,
    TimeSpan End,
    string? RunId = null,
    string? OutputName = null,
    string? CookiesPath = null,
    string? CookiesFromBrowser = null,
    // Opt-in to downloading the source video locally when no direct URL is reachable.
    // Clip extraction has no inline-media sidestep (it must re-encode a byte range), so a
    // download is the only path when this is true. Default false: extraction throws a
    // clear ReplayException so the caller can prompt the user.
    bool AllowMediaDownload = false);

public sealed record ClipManifest(
    string SchemaVersion,
    string Source,
    string RunId,
    TimeSpan Start,
    TimeSpan End,
    string ClipPath,
    IReadOnlyList<ReplayWarning> Warnings);

public sealed record ClipExtractionResult(VideoRun Run, ClipManifest Manifest);
