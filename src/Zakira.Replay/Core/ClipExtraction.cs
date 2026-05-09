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
        var warnings = new List<string>();

        progress?.Report($"Run directory: {run.Directory}");
        var mediaSource = isLocalFile ? localPath : (string?)null;
        if (!isLocalFile)
        {
            var analyzeRequest = new AnalyzeRequest(
                Source: request.Source,
                Instruction: "Extract a timestamped video clip.",
                IncludeTranscript: false,
                FrameCount: 0,
                RunId: request.RunId,
                CookiesPath: request.CookiesPath,
                CookiesFromBrowser: request.CookiesFromBrowser);
            progress?.Report("Resolving direct media URL for ffmpeg...");
            mediaSource = await ytDlp.GetBestMediaUrlAsync(analyzeRequest, cancellationToken).ConfigureAwait(false);
            if (mediaSource is null)
            {
                warnings.Add("Could not resolve a direct media URL; downloading media locally for clipping.");
                mediaSource = await ytDlp.DownloadMediaForProcessingAsync(analyzeRequest, run, cancellationToken).ConfigureAwait(false);
            }
        }

        if (mediaSource is null)
        {
            throw new ReplayException("Could not resolve media for clip extraction.");
        }

        progress?.Report($"Extracting clip {request.Start} to {request.End}...");
        var clipPath = await ffmpeg.ExtractClipAsync(mediaSource, run, request.Start, request.End, request.OutputName, cancellationToken).ConfigureAwait(false);
        var manifest = new ClipManifest("0.1", request.Source, run.Id, request.Start, request.End, clipPath, warnings);
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
    string? CookiesFromBrowser = null);

public sealed record ClipManifest(
    string SchemaVersion,
    string Source,
    string RunId,
    TimeSpan Start,
    TimeSpan End,
    string ClipPath,
    IReadOnlyList<string> Warnings);

public sealed record ClipExtractionResult(VideoRun Run, ClipManifest Manifest);
