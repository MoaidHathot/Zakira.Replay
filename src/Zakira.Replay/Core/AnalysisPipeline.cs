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
        var warnings = new List<ReplayWarning>();
        ILlmProvider? llm = null;
        string? audioPath = null;
        string? ocrPath = null;
        string? visionPath = null;

        progress?.Report($"Run directory: {run.Directory}");
        await artifactStore.WriteJsonAsync(run, "request.json", request, cancellationToken).ConfigureAwait(false);

        var info = isLocalFile
            ? CreateLocalInfo(localPath)
            : await ResolveUrlMetadataAsync(request, progress, cancellationToken).ConfigureAwait(false);
        info.AvailableSubtitleLanguages = BuildAvailableSubtitleLanguages(info);
        await artifactStore.WriteJsonAsync(run, "metadata.json", info, cancellationToken).ConfigureAwait(false);

        TranscriptArtifact? transcript = null;
        ReplayWarning? missingTranscriptWarning = null;
        if (request.IncludeTranscript)
        {
            progress?.Report(isLocalFile ? "Looking for sidecar subtitles..." : "Looking for existing subtitles/captions...");
            if (isLocalFile)
            {
                transcript = await SidecarSubtitleFinder.TryConvertAsync(localPath, run, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var resolvedLanguages = ResolveSubtitleLanguages(request.CaptionLanguages, info);
                transcript = await ytDlp.DownloadBestSubtitleAsync(request, run, resolvedLanguages, cancellationToken).ConfigureAwait(false);
            }
            if (transcript is null)
            {
                missingTranscriptWarning = request.UseSpeechToText
                    ? new ReplayWarning(
                        ReplayWarningCodes.TranscriptNotFound,
                        "No captions/subtitles were extracted; speech-to-text fallback will be attempted.",
                        Source: isLocalFile ? "sidecar" : "yt-dlp",
                        Severity: ReplayWarningSeverities.Info)
                    : new ReplayWarning(
                        ReplayWarningCodes.TranscriptNotFoundNoStt,
                        "No captions/subtitles were extracted. Use --stt to request audio transcription fallback.",
                        Source: isLocalFile ? "sidecar" : "yt-dlp");
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
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.MediaUrlUnresolved,
                    "Could not resolve a direct media URL for ffmpeg processing.",
                    Source: "yt-dlp"));
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
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.AudioRemoteFallback,
                    $"Direct remote audio extraction failed; falling back to local media download. Cause: {ex.Message}",
                    Source: "ffmpeg",
                    Severity: ReplayWarningSeverities.Info));
                var fallbackMedia = await GetDownloadedMediaSourceAsync().ConfigureAwait(false);
                if (fallbackMedia is null)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.AudioDownloadFailed,
                        "Could not download media for audio extraction fallback.",
                        Source: "yt-dlp",
                        Severity: ReplayWarningSeverities.Error));
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
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.SttNoAudio,
                    "Speech-to-text was requested but no audio artifact was available.",
                    Source: "ffmpeg",
                    Severity: ReplayWarningSeverities.Error));
            }
            else
            {
                llm ??= TryResolveLlm(request);
                if (llm is null)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.SttNoLlmProvider,
                        "Speech-to-text was requested but no LLM provider is configured.",
                        Source: "llm",
                        Severity: ReplayWarningSeverities.Error));
                }
                else
                {
                    progress?.Report($"Transcribing audio with {llm.Name} (chunked)...");
                    var transcriber = new CopilotTranscriptionProvider(llm, request.Model);
                    var chunkedService = new ChunkedTranscriptionService(transcriber, new AudioChunker(ffmpeg));
                    var chunkedResult = await chunkedService.TranscribeAsync(run.GetPath(audioPath), run, options: null, progress, cancellationToken).ConfigureAwait(false);
                    var markdownPath = run.GetPath("transcript.md");
                    await File.WriteAllTextAsync(markdownPath, chunkedResult.MarkdownTranscript + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                    transcript = new TranscriptArtifact(run.GetPath(audioPath), markdownPath, $"{llm.Name}-audio-transcription");
                    if (chunkedResult.Chunks.Chunks.Count > 1)
                    {
                        await artifactStore.WriteJsonAsync(run, "audio/chunks/chunks.json", chunkedResult.Chunks, cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var chunkWarning in chunkedResult.ChunkedTranscriptionWarnings)
                    {
                        warnings.Add(chunkWarning);
                    }

                    if (missingTranscriptWarning is not null)
                    {
                        warnings.Remove(missingTranscriptWarning);
                    }
                }
            }
        }

        IReadOnlyList<FrameArtifact> frames = [];
        var sceneSafetyCap = ResolveSceneSafetyCap(request);
        var requestedFrameCount = ResolveEffectiveFrameCount(request, info.DurationSeconds);
        var isSceneStrategy = request.FrameStrategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase);
        if (requestedFrameCount > 0 || isSceneStrategy)
        {
            if (mediaSource is null)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.FramesNoMedia,
                    "Could not resolve media for frame extraction.",
                    Source: "ffmpeg",
                    Severity: ReplayWarningSeverities.Error));
            }
            else
            {
                progress?.Report(isSceneStrategy
                    ? $"Extracting scene-cut frames with ffmpeg (safety cap {sceneSafetyCap})..."
                    : $"Extracting {requestedFrameCount} frame(s) with ffmpeg...");
                try
                {
                    frames = await ffmpeg.ExtractFramesAsync(mediaSource, run, requestedFrameCount, info.DurationSeconds, request.FrameStrategy, sceneSafetyCap, cancellationToken).ConfigureAwait(false);
                }
                catch (ReplayException ex) when (!isLocalFile)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.FramesRemoteFallback,
                        $"Direct remote frame extraction failed; falling back to local media download. Cause: {ex.Message}",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Info));
                    var fallbackMedia = await GetDownloadedMediaSourceAsync().ConfigureAwait(false);
                    if (fallbackMedia is null)
                    {
                        warnings.Add(new ReplayWarning(
                            ReplayWarningCodes.FramesDownloadFailed,
                            "Could not download media for frame extraction fallback.",
                            Source: "yt-dlp",
                            Severity: ReplayWarningSeverities.Error));
                    }
                    else
                    {
                        frames = await ffmpeg.ExtractFramesAsync(fallbackMedia, run, requestedFrameCount, info.DurationSeconds, request.FrameStrategy, sceneSafetyCap, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (isSceneStrategy && frames.Count >= sceneSafetyCap)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.FramesSceneCapReached,
                        $"Scene extraction stopped at the safety cap of {sceneSafetyCap} frames; later scene cuts in the video were not captured. Raise frames.sceneSafetyCap or use --frame-strategy interval with --frames-per-minute.",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Warning));
                }

                if (!isSceneStrategy && request.FramesPerMinute is null && frames.Count > 0 && info.DurationSeconds is { } durationSecs && durationSecs > 0)
                {
                    var durationMinutes = durationSecs / 60.0;
                    var minutesPerFrame = durationMinutes / frames.Count;
                    if (minutesPerFrame > 5)
                    {
                        warnings.Add(new ReplayWarning(
                            ReplayWarningCodes.FramesLikelyUndersampled,
                            $"Extracted {frames.Count} frame(s) over {durationMinutes:F1} minutes (1 frame per {minutesPerFrame:F1} minutes). Pass --frames-per-minute or increase --frames to sample the video more densely.",
                            Source: "ffmpeg",
                            Severity: ReplayWarningSeverities.Warning));
                    }
                }
            }
        }

        if (frames.Count > 0)
        {
            progress?.Report("Computing perceptual hashes for frames...");
            frames = await ComputeFrameHashesAsync(frames, run, warnings, cancellationToken).ConfigureAwait(false);
        }

        var slideOptions = ResolveSlideGroupingOptions(request);
        var slides = SlideGrouper.Group(frames, slideOptions);

        var rawTranscriptSegments = transcript is null
            ? []
            : transcript.Segments is { Count: > 0 } captionSegments
                ? captionSegments
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

        var speakers = BuildSpeakerRegistry(transcriptSegments);

        var primarySlides = slides.Take(request.MaxAiFrames).ToArray();

        var ocrResults = new List<OcrFrameResult>();
        if (request.UseOcr && primarySlides.Length > 0)
        {
            llm ??= TryResolveLlm(request);
            if (llm is null)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.OcrNoLlmProvider,
                    "OCR was requested but no LLM provider is configured.",
                    Source: "llm",
                    Severity: ReplayWarningSeverities.Error));
            }
            else
            {
                var ocrProvider = new CopilotOcrProvider(llm, request.Model);
                foreach (var slide in primarySlides)
                {
                    var primaryFrame = frames.First(frame => frame.Id == slide.PrimaryFrameId);
                    progress?.Report($"Running OCR on {primaryFrame.Path} (slide {slide.Id})...");
                    var raw = await ocrProvider.ExtractTextAsync(run.GetPath(primaryFrame.Path), request.OcrInstruction, cancellationToken).ConfigureAwait(false);
                    var structured = StructuredResponseParser.ParseOcr(raw);
                    if (StructuredResponseParser.IsTolerantFallback(structured))
                    {
                        warnings.Add(new ReplayWarning(
                            ReplayWarningCodes.OcrParseFallback,
                            $"OCR response for {slide.Id} was not strict JSON; stored as freeText only.",
                            Source: "ocr",
                            Severity: ReplayWarningSeverities.Warning));
                    }

                    var result = new OcrFrameResult(
                        FrameId: primaryFrame.Id,
                        FramePath: primaryFrame.Path,
                        TimestampSeconds: primaryFrame.TimestampSeconds,
                        TimestampLabel: primaryFrame.TimestampLabel,
                        Text: structured.FreeText,
                        SlideId: slide.Id,
                        Structured: structured);
                    ocrResults.Add(result);
                    await artifactStore.WriteJsonAsync(run, $"ocr/{primaryFrame.Id}.json", result, cancellationToken).ConfigureAwait(false);
                }

                ocrPath = "ocr/combined.md";
                await artifactStore.WriteTextAsync(run, ocrPath, FormatOcr(ocrResults), cancellationToken).ConfigureAwait(false);
            }
        }

        var visionResults = new List<VisionFrameResult>();
        if (request.UseVision && primarySlides.Length > 0)
        {
            llm ??= TryResolveLlm(request);
            if (llm is null)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.VisionNoLlmProvider,
                    "Vision analysis was requested but no LLM provider is configured.",
                    Source: "llm",
                    Severity: ReplayWarningSeverities.Error));
            }
            else
            {
                var visionProvider = new CopilotVisionProvider(llm, request.Model);
                foreach (var slide in primarySlides)
                {
                    var primaryFrame = frames.First(frame => frame.Id == slide.PrimaryFrameId);
                    progress?.Report($"Analyzing slide {slide.Id} ({primaryFrame.Path})...");
                    var raw = await visionProvider.DescribeAsync(run.GetPath(primaryFrame.Path), request.VisionInstruction, cancellationToken).ConfigureAwait(false);
                    var structured = StructuredResponseParser.ParseVision(raw);
                    if (StructuredResponseParser.IsTolerantFallback(structured))
                    {
                        warnings.Add(new ReplayWarning(
                            ReplayWarningCodes.VisionParseFallback,
                            $"Vision response for {slide.Id} was not strict JSON; stored as freeText only.",
                            Source: "vision",
                            Severity: ReplayWarningSeverities.Warning));
                    }

                    var result = new VisionFrameResult(
                        FrameId: primaryFrame.Id,
                        FramePath: primaryFrame.Path,
                        TimestampSeconds: primaryFrame.TimestampSeconds,
                        TimestampLabel: primaryFrame.TimestampLabel,
                        Description: structured.FreeText,
                        SlideId: slide.Id,
                        Structured: structured);
                    visionResults.Add(result);
                    await artifactStore.WriteJsonAsync(run, $"vision/{primaryFrame.Id}.json", result, cancellationToken).ConfigureAwait(false);
                }

                visionPath = "vision/combined.md";
                await artifactStore.WriteTextAsync(run, visionPath, FormatVision(visionResults), cancellationToken).ConfigureAwait(false);
            }
        }

        var evidence = new EvidenceDocument(
            SchemaVersion: "0.7",
            Source: request.Source,
            VisionInstruction: request.VisionInstruction,
            OcrInstruction: request.OcrInstruction,
            RunId: run.Id,
            Title: info.Title,
            WebpageUrl: info.WebpageUrl,
            DurationSeconds: info.DurationSeconds,
            AudioPath: audioPath,
            Transcript: transcriptSegments,
            Frames: frames,
            Slides: slides,
            Ocr: ocrResults,
            Vision: visionResults,
            Speakers: speakers,
            Warnings: warnings);

        var evidenceMarkdown = BuildEvidenceIndexMarkdown(request, info, audioPath, transcript, frames, ocrPath, visionPath, warnings);
        await artifactStore.WriteTextAsync(run, "evidence.md", evidenceMarkdown, cancellationToken).ConfigureAwait(false);
        await artifactStore.WriteJsonAsync(run, "evidence.json", evidence, cancellationToken).ConfigureAwait(false);

        if (slides.Count > 0)
        {
            await artifactStore.WriteJsonAsync(run, "slides/slides.json", slides, cancellationToken).ConfigureAwait(false);
        }

        var manifest = new ArtifactManifest(
            SchemaVersion: "0.7",
            Source: request.Source,
            VisionInstruction: request.VisionInstruction,
            OcrInstruction: request.OcrInstruction,
            CreatedAt: DateTimeOffset.UtcNow,
            RunId: run.Id,
            Title: info.Title,
            WebpageUrl: info.WebpageUrl,
            Duration: info.DurationSeconds is null ? null : Timestamp.Format(info.DurationSeconds.Value),
            AudioPath: audioPath,
            TranscriptPath: transcript is null ? null : Path.GetRelativePath(run.Directory, transcript.MarkdownPath).Replace('\\', '/'),
            OcrPath: ocrPath,
            VisionPath: visionPath,
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

    private async Task<IReadOnlyList<FrameArtifact>> ComputeFrameHashesAsync(
        IReadOnlyList<FrameArtifact> frames,
        VideoRun run,
        List<ReplayWarning> warnings,
        CancellationToken cancellationToken)
    {
        var hashed = new List<FrameArtifact>(frames.Count);
        var failureSeen = false;
        foreach (var frame in frames)
        {
            try
            {
                var hash = await ffmpeg.ComputePerceptualHashAsync(run.GetPath(frame.Path), cancellationToken).ConfigureAwait(false);
                hashed.Add(frame with { PerceptualHash = hash });
                if (hash is null && !failureSeen)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.PerceptualHashFailed,
                        "Perceptual hash computation failed for at least one frame; slide grouping may be coarse.",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Warning));
                    failureSeen = true;
                }
            }
            catch (ReplayException ex)
            {
                hashed.Add(frame);
                if (!failureSeen)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.PerceptualHashFailed,
                        $"Perceptual hash computation failed: {ex.Message}",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Warning));
                    failureSeen = true;
                }
            }
        }

        return hashed;
    }

    private static SlideGroupingOptions ResolveSlideGroupingOptions(AnalyzeRequest request)
    {
        var config = new ConfigStore().Load();
        var enabled = request.SlideGrouping ?? config.Slides.Enabled;
        var hashDistance = request.SlideHashDistance ?? config.Slides.HashDistance;
        return new SlideGroupingOptions(enabled, hashDistance);
    }

    /// <summary>
    /// Resolves the effective number of frames to extract for interval / every-frame strategies.
    /// When <see cref="AnalyzeRequest.FramesPerMinute"/> is set, scales by duration with
    /// <see cref="AnalyzeRequest.FrameCount"/> as a floor. Returns <see cref="AnalyzeRequest.FrameCount"/>
    /// verbatim otherwise. The scene strategy ignores this value entirely (it is bounded by the
    /// scene safety cap).
    /// </summary>
    internal static int ResolveEffectiveFrameCount(AnalyzeRequest request, double? durationSeconds)
    {
        if (request.FramesPerMinute is not { } framesPerMinute || framesPerMinute <= 0 || durationSeconds is not { } durationSecs || durationSecs <= 0)
        {
            return request.FrameCount;
        }

        var durationMinutes = durationSecs / 60.0;
        var scaled = (int)Math.Ceiling(framesPerMinute * durationMinutes);
        return Math.Max(scaled, request.FrameCount);
    }

    private static int ResolveSceneSafetyCap(AnalyzeRequest request)
    {
        if (request.SceneSafetyCap is { } perRequest && perRequest > 0)
        {
            return perRequest;
        }

        return new ConfigStore().Load().Frames.SceneSafetyCap;
    }

    private static IReadOnlyList<SpeakerSummary> BuildSpeakerRegistry(IReadOnlyList<TranscriptSegment> segments)
    {
        var groups = segments
            .Where(segment => !string.IsNullOrEmpty(segment.SpeakerId))
            .GroupBy(segment => segment.SpeakerId!, StringComparer.OrdinalIgnoreCase);

        return groups
            .Select(group =>
            {
                var groupSegments = group.ToArray();
                double total = 0;
                double? firstSeen = null;
                double? lastSeen = null;
                foreach (var segment in groupSegments)
                {
                    if (segment.StartSeconds is not null)
                    {
                        firstSeen = firstSeen is null ? segment.StartSeconds : Math.Min(firstSeen.Value, segment.StartSeconds.Value);
                    }

                    var endSeconds = segment.EndSeconds ?? segment.StartSeconds;
                    if (endSeconds is not null)
                    {
                        lastSeen = lastSeen is null ? endSeconds : Math.Max(lastSeen.Value, endSeconds.Value);
                    }

                    if (segment.StartSeconds is not null && segment.EndSeconds is not null)
                    {
                        total += Math.Max(0, segment.EndSeconds.Value - segment.StartSeconds.Value);
                    }
                }

                var displayName = groupSegments
                    .Select(segment => segment.SpeakerDisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                return new SpeakerSummary(
                    Id: group.Key,
                    DisplayName: displayName,
                    SegmentCount: groupSegments.Length,
                    TotalSeconds: total,
                    FirstSeenSeconds: firstSeen,
                    LastSeenSeconds: lastSeen);
            })
            .OrderBy(speaker => speaker.FirstSeenSeconds ?? double.MaxValue)
            .ThenBy(speaker => speaker.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<YtDlpInfo> ResolveUrlMetadataAsync(AnalyzeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Resolving metadata with yt-dlp...");
        return await ytDlp.GetInfoAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, AvailableSubtitleLanguage>? BuildAvailableSubtitleLanguages(YtDlpInfo info)
    {
        var manualLanguages = (IEnumerable<string>?)info.Subtitles?.Keys ?? [];
        var autoLanguages = (IEnumerable<string>?)info.AutomaticCaptions?.Keys ?? [];
        var combined = manualLanguages.Concat(autoLanguages)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (combined.Length == 0)
        {
            return null;
        }

        var result = new Dictionary<string, AvailableSubtitleLanguage>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in combined)
        {
            result[language] = new AvailableSubtitleLanguage
            {
                HasManual = info.Subtitles?.ContainsKey(language) == true,
                HasAuto = info.AutomaticCaptions?.ContainsKey(language) == true
            };
        }

        return result;
    }

    /// <summary>
    /// Resolves the effective subtitle-language preference list for a single run.
    /// </summary>
    /// <remarks>
    /// Precedence: explicit per-request <paramref name="requestedLanguages"/> override the config.
    /// When the resolved list contains <c>"auto"</c> (or is empty), Zakira.Replay unions the
    /// languages advertised by the source's metadata with the source's primary language and
    /// English/live-chat defaults so an existing transcript is found whenever yt-dlp knows of one.
    /// </remarks>
    internal static IReadOnlyList<string> ResolveSubtitleLanguages(IReadOnlyList<string>? requestedLanguages, YtDlpInfo info)
    {
        var preferences = requestedLanguages is { Count: > 0 }
            ? requestedLanguages
            : new ConfigStore().Load().Captions.Languages;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPreference in preferences)
        {
            if (string.IsNullOrWhiteSpace(rawPreference))
            {
                continue;
            }

            var preference = rawPreference.Trim();
            if (preference.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var language in EnumerateAutoLanguages(info))
                {
                    if (seen.Add(language))
                    {
                        result.Add(language);
                    }
                }

                continue;
            }

            if (seen.Add(preference))
            {
                result.Add(preference);
            }
        }

        if (result.Count == 0)
        {
            foreach (var language in EnumerateAutoLanguages(info))
            {
                if (seen.Add(language))
                {
                    result.Add(language);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Enumerates the language codes that <c>auto</c> expands to. Manual subtitles are intentional
    /// uploads, so all of them are included. Auto-captions, on the other hand, are typically a long
    /// list of YouTube auto-translations (≈ 150 languages); only the source language is included
    /// because the rest are inferences from the source. English (<c>en</c>, <c>en.*</c>) and
    /// <c>live_chat</c> are appended as fallbacks.
    /// </summary>
    private static IEnumerable<string> EnumerateAutoLanguages(YtDlpInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Language))
        {
            yield return info.Language!.Trim();
        }

        if (info.Subtitles is not null)
        {
            foreach (var key in info.Subtitles.Keys.OrderBy(language => language, StringComparer.OrdinalIgnoreCase))
            {
                yield return key;
            }
        }

        // Auto-captions for languages OTHER than the source are auto-translations, not facts about
        // what was spoken. Don't expand to the full pool; the orchestrator can pass an explicit
        // language list if they want a specific auto-translation.

        yield return "en.*";
        yield return "en";
        yield return "live_chat";
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

    private static string BuildEvidenceIndexMarkdown(
        AnalyzeRequest request,
        YtDlpInfo info,
        string? audioPath,
        TranscriptArtifact? transcript,
        IReadOnlyList<FrameArtifact> frames,
        string? ocrPath,
        string? visionPath,
        IReadOnlyList<ReplayWarning> warnings)
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
        if (!string.IsNullOrWhiteSpace(request.VisionInstruction))
        {
            builder.AppendLine($"Vision instruction: {request.VisionInstruction.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(request.OcrInstruction))
        {
            builder.AppendLine($"OCR instruction: {request.OcrInstruction.Trim()}");
        }
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
                builder.AppendLine($"- [{warning.Severity}] {warning.Code}: {warning.Message}");
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
    string VisionInstruction,
    bool IncludeTranscript,
    int FrameCount,
    string? RunId,
    string OcrInstruction = "",
    bool ExtractAudio = false,
    bool UseSpeechToText = false,
    bool UseOcr = false,
    bool UseVision = false,
    int MaxAiFrames = 5,
    string Model = GitHubCopilotLlmProvider.DefaultModel,
    string LlmProvider = LlmProviders.GitHubCopilot,
    bool Force = false,
    bool UseCache = false,
    string FrameStrategy = FrameSelectionStrategies.Interval,
    string? CookiesPath = null,
    string? CookiesFromBrowser = null,
    IReadOnlyList<string>? CaptionLanguages = null,
    bool? SlideGrouping = null,
    int? SlideHashDistance = null,
    int? FramesPerMinute = null,
    int? SceneSafetyCap = null);

public static class FrameSelectionStrategies
{
    public const string Interval = "interval";

    public const string Scene = "scene";

    public const string EveryFrame = "every-frame";
}

public sealed record AnalyzeResult(VideoRun Run, ArtifactManifest Manifest, bool Reused = false);
