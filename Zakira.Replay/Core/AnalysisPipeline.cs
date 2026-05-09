namespace Zakira.Replay.Core;

public sealed class AnalysisPipeline
{
    private readonly ArtifactStore artifactStore;
    private readonly IYtDlpClient ytDlp;
    private readonly IFfmpegClient ffmpeg;
    private readonly Func<string?, ILlmProvider?> llmFactory;
    private readonly ILlmProvider? configuredLlm;

    public AnalysisPipeline(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg, ILlmProvider? llm = null)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
        configuredLlm = llm;
        llmFactory = _ => llm;
    }

    public AnalysisPipeline(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg, Func<string?, ILlmProvider?> llmFactory)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
        this.llmFactory = llmFactory;
    }

    public async Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        SourceLocator.ThrowIfMissingLocalPathLikeSource(request.Source);
        var isLocalFile = SourceLocator.TryGetLocalFilePath(request.Source, out var localPath);

        var cacheKey = request.UseCache ? AnalysisCache.CreateKey(request) : null;

        if (!request.Force && !string.IsNullOrWhiteSpace(request.RunId) && artifactStore.TryGetExistingRun(request.RunId, out var existingRun))
        {
            var existingManifest = await artifactStore.ReadJsonAsync<ArtifactManifest>(existingRun, "manifest.json", cancellationToken).ConfigureAwait(false);
            if (existingManifest is not null)
            {
                progress?.Report($"Reusing existing run: {existingRun.Directory}");
                return new AnalyzeResult(existingRun, existingManifest, Reused: true);
            }
        }

        if (!request.Force && request.UseCache && string.IsNullOrWhiteSpace(request.RunId) && cacheKey is not null && artifactStore.TryGetCachedRun(cacheKey, out var cachedRun))
        {
            var cachedManifest = await artifactStore.ReadJsonAsync<ArtifactManifest>(cachedRun, "manifest.json", cancellationToken).ConfigureAwait(false);
            if (cachedManifest is not null)
            {
                progress?.Report($"Reusing cached run: {cachedRun.Directory}");
                return new AnalyzeResult(cachedRun, cachedManifest, Reused: true);
            }
        }

        var run = artifactStore.CreateRun(request.Source, request.RunId);
        var warnings = new List<string>();
        ILlmProvider? llm = null;
        string? audioPath = null;
        string? ocrPath = null;
        string? visionPath = null;
        string? summaryPath = null;

        progress?.Report($"Run directory: {run.Directory}");
        await artifactStore.WriteJsonAsync(run, "request.json", request, cancellationToken).ConfigureAwait(false);

        var info = isLocalFile
            ? CreateLocalInfo(localPath)
            : await ResolveUrlMetadataAsync(request, progress, cancellationToken).ConfigureAwait(false);
        await artifactStore.WriteJsonAsync(run, "metadata.json", info, cancellationToken).ConfigureAwait(false);

        TranscriptArtifact? transcript = null;
        var missingTranscriptWarning = request.UseSpeechToText
            ? "No captions/subtitles were extracted; speech-to-text fallback will be attempted."
            : "No captions/subtitles were extracted. Use --stt to request audio transcription fallback.";
        if (request.IncludeTranscript)
        {
            progress?.Report(isLocalFile ? "Looking for sidecar subtitles..." : "Looking for existing subtitles/captions...");
            transcript = isLocalFile
                ? await SidecarSubtitleFinder.TryConvertAsync(localPath, run, cancellationToken).ConfigureAwait(false)
                : await ytDlp.DownloadBestSubtitleAsync(request, run, cancellationToken).ConfigureAwait(false);
            if (transcript is null)
            {
                warnings.Add(missingTranscriptWarning);
            }
        }

        var mediaSource = isLocalFile ? localPath : (string?)null;
        string? downloadedMediaSource = null;
        async Task<string?> GetDownloadedMediaSourceAsync()
        {
            if (isLocalFile)
            {
                return localPath;
            }

            if (downloadedMediaSource is not null)
            {
                return downloadedMediaSource;
            }

            progress?.Report("Downloading media locally with yt-dlp for ffmpeg fallback...");
            downloadedMediaSource = await ytDlp.DownloadMediaForProcessingAsync(request, run, cancellationToken).ConfigureAwait(false);
            return downloadedMediaSource;
        }

        if (!isLocalFile && (request.FrameCount > 0 || request.ExtractAudio || request.UseSpeechToText))
        {
            progress?.Report(isLocalFile ? "Using local media path for ffmpeg..." : "Resolving direct media URL for ffmpeg...");
            mediaSource = await ytDlp.GetBestMediaUrlAsync(request, cancellationToken).ConfigureAwait(false);
            if (mediaSource is null)
            {
                warnings.Add("Could not resolve a direct media URL for ffmpeg processing.");
            }
        }
        else if (isLocalFile)
        {
            progress?.Report("Using local media path for ffmpeg...");
        }

        if (mediaSource is not null && isLocalFile && info.DurationSeconds is null && (request.FrameCount > 0 || request.ExtractAudio || request.UseSpeechToText))
        {
            progress?.Report("Probing local media duration with ffprobe...");
            info.DurationSeconds = await ffmpeg.TryProbeDurationAsync(localPath, cancellationToken).ConfigureAwait(false);
            await artifactStore.WriteJsonAsync(run, "metadata.json", info, cancellationToken).ConfigureAwait(false);
        }

        if (mediaSource is not null && (request.ExtractAudio || (request.UseSpeechToText && transcript is null)))
        {
            progress?.Report("Extracting audio with ffmpeg...");
            try
            {
                audioPath = await ffmpeg.ExtractAudioAsync(mediaSource, run, cancellationToken).ConfigureAwait(false);
            }
            catch (ReplayException ex) when (!isLocalFile)
            {
                warnings.Add($"Direct remote audio extraction failed; falling back to local media download. Cause: {ex.Message}");
                var fallbackMedia = await GetDownloadedMediaSourceAsync().ConfigureAwait(false);
                if (fallbackMedia is null)
                {
                    warnings.Add("Could not download media for audio extraction fallback.");
                }
                else
                {
                    audioPath = await ffmpeg.ExtractAudioAsync(fallbackMedia, run, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (request.UseSpeechToText && transcript is null)
        {
            if (audioPath is null)
            {
                warnings.Add("Speech-to-text was requested but no audio artifact was available.");
            }
            else
            {
                llm ??= TryResolveLlm(request);
                if (llm is null)
                {
                    warnings.Add("Speech-to-text was requested but no LLM provider is configured.");
                }
                else
                {
                    progress?.Report($"Transcribing audio with {llm.Name}...");
                    var transcription = await new CopilotTranscriptionProvider(llm, request.Model).TranscribeAsync(run.GetPath(audioPath), cancellationToken).ConfigureAwait(false);
                    var markdownPath = run.GetPath("transcript.md");
                    await File.WriteAllTextAsync(markdownPath, transcription + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                    transcript = new TranscriptArtifact(run.GetPath(audioPath), markdownPath, $"{llm.Name}-audio-transcription");
                    warnings.Remove(missingTranscriptWarning);
                }
            }
        }

        IReadOnlyList<FrameArtifact> frames = [];
        if (request.FrameCount > 0)
        {
            if (mediaSource is null)
            {
                warnings.Add("Could not resolve media for frame extraction.");
            }
            else
            {
                progress?.Report($"Extracting {request.FrameCount} frame(s) with ffmpeg...");
                try
                {
                    frames = await ffmpeg.ExtractFramesAsync(mediaSource, run, request.FrameCount, info.DurationSeconds, request.FrameStrategy, cancellationToken).ConfigureAwait(false);
                }
                catch (ReplayException ex) when (!isLocalFile)
                {
                    warnings.Add($"Direct remote frame extraction failed; falling back to local media download. Cause: {ex.Message}");
                    var fallbackMedia = await GetDownloadedMediaSourceAsync().ConfigureAwait(false);
                    if (fallbackMedia is null)
                    {
                        warnings.Add("Could not download media for frame extraction fallback.");
                    }
                    else
                    {
                        frames = await ffmpeg.ExtractFramesAsync(fallbackMedia, run, request.FrameCount, info.DurationSeconds, request.FrameStrategy, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        var rawTranscriptSegments = transcript is null
            ? []
            : await TranscriptParser.FromMarkdownFileAsync(transcript.MarkdownPath, cancellationToken).ConfigureAwait(false);
        var transcriptNormalization = TranscriptNormalizer.NormalizeWithReport(rawTranscriptSegments);
        var transcriptSegments = transcriptNormalization.Segments;
        if (transcript is not null)
        {
            await artifactStore.WriteTextAsync(run, "transcript/raw.md", TranscriptNormalizer.ToMarkdown(rawTranscriptSegments), cancellationToken).ConfigureAwait(false);
            await artifactStore.WriteJsonAsync(run, "transcript/raw.json", rawTranscriptSegments, cancellationToken).ConfigureAwait(false);
            await artifactStore.WriteJsonAsync(run, "transcript/normalization.json", transcriptNormalization.Report, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(transcript.MarkdownPath, TranscriptNormalizer.ToMarkdown(transcriptSegments), cancellationToken).ConfigureAwait(false);
        }

        var ocrResults = new List<OcrFrameResult>();
        if (request.UseOcr && frames.Count > 0)
        {
            llm ??= TryResolveLlm(request);
            if (llm is null)
            {
                warnings.Add("OCR was requested but no LLM provider is configured.");
            }
            else
            {
                var ocrProvider = new CopilotOcrProvider(llm, request.Model);
                foreach (var frame in frames.Take(request.MaxAiFrames))
                {
                    progress?.Report($"Running OCR on {frame.Path}...");
                    var text = await ocrProvider.ExtractTextAsync(run.GetPath(frame.Path), cancellationToken).ConfigureAwait(false);
                    ocrResults.Add(new OcrFrameResult(frame.Path, frame.TimestampSeconds, frame.TimestampLabel, text));
                }

                ocrPath = "ocr/combined.md";
                await artifactStore.WriteTextAsync(run, ocrPath, FormatOcr(ocrResults), cancellationToken).ConfigureAwait(false);
            }
        }

        var visionResults = new List<VisionFrameResult>();
        if (request.UseVision && frames.Count > 0)
        {
            llm ??= TryResolveLlm(request);
            if (llm is null)
            {
                warnings.Add("Vision analysis was requested but no LLM provider is configured.");
            }
            else
            {
                var visionProvider = new CopilotVisionProvider(llm, request.Model);
                foreach (var frame in frames.Take(request.MaxAiFrames))
                {
                    progress?.Report($"Analyzing frame {frame.Path}...");
                    var description = await visionProvider.DescribeAsync(run.GetPath(frame.Path), request.Instruction, cancellationToken).ConfigureAwait(false);
                    visionResults.Add(new VisionFrameResult(frame.Path, frame.TimestampSeconds, frame.TimestampLabel, description));
                }

                visionPath = "vision/combined.md";
                await artifactStore.WriteTextAsync(run, visionPath, FormatVision(visionResults), cancellationToken).ConfigureAwait(false);
            }
        }

        var evidence = new EvidenceDocument(
            SchemaVersion: "0.1",
            Source: request.Source,
            Instruction: request.Instruction,
            RunId: run.Id,
            Title: info.Title,
            WebpageUrl: info.WebpageUrl,
            DurationSeconds: info.DurationSeconds,
            AudioPath: audioPath,
            Transcript: transcriptSegments,
            Frames: frames,
            Ocr: ocrResults,
            Vision: visionResults,
            Summary: null,
            Warnings: warnings);

        if (request.UseSummary)
        {
            llm ??= TryResolveLlm(request);
            if (llm is null)
            {
                warnings.Add("Summary was requested but no LLM provider is configured.");
            }
            else
            {
                progress?.Report($"Summarizing evidence with {llm.Name}...");
                var summary = await new VideoSummaryService(llm, request.Model).SummarizeAsync(evidence, run.Directory, cancellationToken).ConfigureAwait(false);
                summaryPath = "summary.md";
                await artifactStore.WriteTextAsync(run, summaryPath, summary + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                evidence = evidence with { Summary = summary, Warnings = warnings };
            }
        }

        var evidenceMarkdown = BuildEvidenceSummary(request, info, audioPath, transcript, frames, ocrPath, visionPath, summaryPath, warnings);
        await artifactStore.WriteTextAsync(run, "evidence.md", evidenceMarkdown, cancellationToken).ConfigureAwait(false);
        await artifactStore.WriteJsonAsync(run, "evidence.json", evidence, cancellationToken).ConfigureAwait(false);

        var manifest = new ArtifactManifest(
            SchemaVersion: "0.1",
            Source: request.Source,
            Instruction: request.Instruction,
            CreatedAt: DateTimeOffset.UtcNow,
            RunId: run.Id,
            Title: info.Title,
            WebpageUrl: info.WebpageUrl,
            Duration: info.DurationSeconds is null ? null : Timestamp.Format(info.DurationSeconds.Value),
            AudioPath: audioPath,
            TranscriptPath: transcript is null ? null : Path.GetRelativePath(run.Directory, transcript.MarkdownPath).Replace('\\', '/'),
            OcrPath: ocrPath,
            VisionPath: visionPath,
            SummaryPath: summaryPath,
            EvidencePath: "evidence.json",
            Frames: frames,
            Warnings: warnings);

        await artifactStore.WriteJsonAsync(run, "manifest.json", manifest, cancellationToken).ConfigureAwait(false);
        if (cacheKey is not null)
        {
            await artifactStore.WriteCacheEntryAsync(cacheKey, run, cancellationToken).ConfigureAwait(false);
        }

        return new AnalyzeResult(run, manifest);
    }

    private ILlmProvider? TryResolveLlm(AnalyzeRequest request)
    {
        if (configuredLlm is not null)
        {
            return configuredLlm;
        }

        return llmFactory(request.LlmProvider);
    }

    private async Task<YtDlpInfo> ResolveUrlMetadataAsync(AnalyzeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Resolving metadata with yt-dlp...");
        return await ytDlp.GetInfoAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static YtDlpInfo CreateLocalInfo(string path)
    {
        return new YtDlpInfo
        {
            Id = Path.GetFileNameWithoutExtension(path),
            Title = Path.GetFileNameWithoutExtension(path),
            WebpageUrl = new Uri(path).AbsoluteUri,
            Description = null,
            Uploader = null
        };
    }

    private static string BuildEvidenceSummary(
        AnalyzeRequest request,
        YtDlpInfo info,
        string? audioPath,
        TranscriptArtifact? transcript,
        IReadOnlyList<FrameArtifact> frames,
        string? ocrPath,
        string? visionPath,
        string? summaryPath,
        IReadOnlyList<string> warnings)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# Video Evidence");
        builder.AppendLine();
        builder.AppendLine($"Source: {request.Source}");
        if (!string.IsNullOrWhiteSpace(info.Title))
        {
            builder.AppendLine($"Title: {info.Title}");
        }
        if (!string.IsNullOrWhiteSpace(info.WebpageUrl))
        {
            builder.AppendLine($"Webpage: {info.WebpageUrl}");
        }
        if (info.DurationSeconds is not null)
        {
            builder.AppendLine($"Duration: {Timestamp.Format(info.DurationSeconds.Value)}");
        }
        builder.AppendLine($"Instruction: {request.Instruction}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(audioPath))
        {
            builder.AppendLine("## Audio");
            builder.AppendLine();
            builder.AppendLine($"Audio artifact: `{audioPath}`");
            builder.AppendLine();
        }

        if (transcript is not null)
        {
            builder.AppendLine("## Transcript");
            builder.AppendLine();
            builder.AppendLine($"Markdown transcript: `{Path.GetFileName(transcript.MarkdownPath)}`");
            builder.AppendLine($"Source caption file: `{Path.GetFileName(transcript.SourcePath)}`");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(ocrPath))
        {
            builder.AppendLine("## OCR");
            builder.AppendLine();
            builder.AppendLine($"Combined OCR: `{ocrPath}`");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(visionPath))
        {
            builder.AppendLine("## Vision");
            builder.AppendLine();
            builder.AppendLine($"Combined vision descriptions: `{visionPath}`");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(summaryPath))
        {
            builder.AppendLine("## Summary");
            builder.AppendLine();
            builder.AppendLine($"Summary: `{summaryPath}`");
            builder.AppendLine();
        }

        if (frames.Count > 0)
        {
            builder.AppendLine("## Frames");
            builder.AppendLine();
            foreach (var frame in frames)
            {
                builder.AppendLine($"- `{frame.Path}` at {frame.TimestampLabel}");
            }
            builder.AppendLine();
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static string FormatOcr(IReadOnlyList<OcrFrameResult> results)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var result in results)
        {
            builder.AppendLine($"## {result.TimestampLabel} - {result.FramePath}");
            builder.AppendLine();
            builder.AppendLine(result.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatVision(IReadOnlyList<VisionFrameResult> results)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var result in results)
        {
            builder.AppendLine($"## {result.TimestampLabel} - {result.FramePath}");
            builder.AppendLine();
            builder.AppendLine(result.Description.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

public sealed record AnalyzeRequest(
    string Source,
    string Instruction,
    bool IncludeTranscript,
    int FrameCount,
    string? RunId,
    bool ExtractAudio = false,
    bool UseSpeechToText = false,
    bool UseOcr = false,
    bool UseVision = false,
    bool UseSummary = false,
    int MaxAiFrames = 5,
    string Model = GitHubCopilotLlmProvider.DefaultModel,
    string LlmProvider = LlmProviders.GitHubCopilot,
    bool Force = false,
    bool UseCache = false,
    string FrameStrategy = FrameSelectionStrategies.Interval,
    string? CookiesPath = null,
    string? CookiesFromBrowser = null);

public static class FrameSelectionStrategies
{
    public const string Interval = "interval";

    public const string Scene = "scene";

    public const string EveryFrame = "every-frame";
}

public sealed record AnalyzeResult(VideoRun Run, ArtifactManifest Manifest, bool Reused = false);
