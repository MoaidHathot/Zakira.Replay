namespace Zakira.Replay.Core;

public sealed class AnalysisPipeline
{
    private readonly ArtifactStore artifactStore;
    private readonly IYtDlpClient ytDlp;
    private readonly IFfmpegClient ffmpeg;
    private readonly Func<string?, ILlmProvider?> llmFactory;
    private readonly ILlmProvider? configuredLlm;
    private readonly IBrowserVideoCaptureClient? browserCaptureClient;

    public AnalysisPipeline(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg, ILlmProvider? llm = null, IBrowserVideoCaptureClient? browserCaptureClient = null)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
        configuredLlm = llm;
        llmFactory = _ => llm;
        this.browserCaptureClient = browserCaptureClient;
    }

    public AnalysisPipeline(ArtifactStore artifactStore, IYtDlpClient ytDlp, IFfmpegClient ffmpeg, Func<string?, ILlmProvider?> llmFactory, IBrowserVideoCaptureClient? browserCaptureClient = null)
    {
        this.artifactStore = artifactStore;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
        this.llmFactory = llmFactory;
        this.browserCaptureClient = browserCaptureClient;
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
        var timings = new RunTimings();
        ILlmProvider? llm = null;
        string? audioPath = null;
        string? ocrPath = null;
        string? visionPath = null;

        progress?.Report($"Run directory: {run.Directory}");
        await artifactStore.WriteJsonAsync(run, "request.json", request, cancellationToken).ConfigureAwait(false);

        YtDlpInfo info;
        using (timings.Measure(RunTimingStages.Probe))
        {
            info = isLocalFile
                ? CreateLocalInfo(localPath)
                : await ResolveUrlMetadataAsync(request, progress, cancellationToken).ConfigureAwait(false);
            info.AvailableSubtitleLanguages = BuildAvailableSubtitleLanguages(info);
        }
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
            using var sttScope = timings.Measure(RunTimingStages.Stt);
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
                var providerName = LlmProviderFactory.Normalize(request.LlmProvider);
                if (providerName == LlmProviders.LocalWhisper)
                {
                    var localSttResult = await TryRunLocalWhisperAsync(audioPath, run, warnings, progress, cancellationToken).ConfigureAwait(false);
                    if (localSttResult is { Transcript: { } localTranscript })
                    {
                        transcript = localTranscript;
                        if (missingTranscriptWarning is not null)
                        {
                            warnings.Remove(missingTranscriptWarning);
                        }
                    }
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
        }

        if (request.UseDiarization)
        {
            using var diarizationScope = timings.Measure(RunTimingStages.Diarization);
            await TryRunDiarizationAsync(request, run, audioPath, transcript, warnings, progress, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<FrameArtifact> frames = [];
        var sceneSafetyCap = ResolveSceneSafetyCap(request);
        var requestedFrameCount = ResolveEffectiveFrameCount(request, info.DurationSeconds, new ConfigStore().Load().Frames.PerMinute);
        var isSceneStrategy = request.FrameStrategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase);
        var captureMode = ResolveCaptureMode(request, warnings);
        var browserCaptureAttempted = false;
        var browserDiscoveredCaptions = new List<BrowserCapturedCaption>();
        if (requestedFrameCount > 0 || isSceneStrategy)
        {
            using var framesScope = timings.Measure(RunTimingStages.Frames);
            if (captureMode == CaptureModes.Browser && !isLocalFile)
            {
                browserCaptureAttempted = true;
                var (browserFrames, browserCaptions) = await CaptureFramesAndCaptionsWithBrowserAsync(request, run, requestedFrameCount, info, warnings, progress, cancellationToken).ConfigureAwait(false);
                frames = browserFrames;
                browserDiscoveredCaptions.AddRange(browserCaptions);
            }
            else if (mediaSource is null)
            {
                if (captureMode == CaptureModes.Auto && !isLocalFile)
                {
                    browserCaptureAttempted = true;
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserFallback,
                        "yt-dlp could not resolve a direct media URL; falling back to browser-based capture.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Info));
                    var (browserFrames, browserCaptions) = await CaptureFramesAndCaptionsWithBrowserAsync(request, run, requestedFrameCount, info, warnings, progress, cancellationToken).ConfigureAwait(false);
                    frames = browserFrames;
                    browserDiscoveredCaptions.AddRange(browserCaptions);
                }
                else
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.FramesNoMedia,
                        "Could not resolve media for frame extraction.",
                        Source: "ffmpeg",
                        Severity: ReplayWarningSeverities.Error));
                }
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
                        if (captureMode == CaptureModes.Auto)
                        {
                            browserCaptureAttempted = true;
                            warnings.Add(new ReplayWarning(
                                ReplayWarningCodes.CaptureBrowserFallback,
                                "Falling back to browser-based capture after ffmpeg and yt-dlp download both failed.",
                                Source: "playwright",
                                Severity: ReplayWarningSeverities.Info));
                            var (browserFrames, browserCaptions) = await CaptureFramesAndCaptionsWithBrowserAsync(request, run, requestedFrameCount, info, warnings, progress, cancellationToken).ConfigureAwait(false);
                            frames = browserFrames;
                            browserDiscoveredCaptions.AddRange(browserCaptions);
                        }
                        else
                        {
                            warnings.Add(new ReplayWarning(
                                ReplayWarningCodes.FramesDownloadFailed,
                                "Could not download media for frame extraction fallback.",
                                Source: "yt-dlp",
                                Severity: ReplayWarningSeverities.Error));
                        }
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

                if (!isSceneStrategy && (request.FramesPerMinute is null || request.FramesPerMinute <= 0) && frames.Count > 0 && info.DurationSeconds is { } durationSecs && durationSecs > 0)
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
        // Suppress unused-variable warning when browserCaptureAttempted is purely diagnostic.
        _ = browserCaptureAttempted;

        if (browserDiscoveredCaptions.Count > 0)
        {
            var captionsManifest = new BrowserCapturedCaptionsManifest(
                SchemaVersion: "0.8",
                DiscoveredAt: DateTimeOffset.UtcNow,
                OriginalLanguage: string.IsNullOrWhiteSpace(info.Language) ? null : info.Language,
                Captions: browserDiscoveredCaptions);
            await artifactStore.WriteJsonAsync(run, "captions/discovered.json", captionsManifest, cancellationToken).ConfigureAwait(false);

            if (request.IncludeTranscript && transcript is null)
            {
                transcript = await TryFillTranscriptFromBrowserCaptionsAsync(request, run, info, browserDiscoveredCaptions, warnings, progress, cancellationToken).ConfigureAwait(false);
                if (transcript is not null && missingTranscriptWarning is not null)
                {
                    warnings.Remove(missingTranscriptWarning);
                    missingTranscriptWarning = null;
                }
            }
        }
        else if (browserCaptureAttempted && request.IncludeTranscript && transcript is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptionsBrowserNetworkNone,
                "Browser capture ran but no caption (.vtt/.srt) responses were observed during playback.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Info));
        }

        if (frames.Count > 0)
        {
            frames = ApplySmartCrop(frames, run, request, warnings, progress);
        }

        if (frames.Count > 0)
        {
            progress?.Report("Computing perceptual hashes for frames...");
            frames = await ComputeFrameHashesAsync(frames, run, warnings, cancellationToken).ConfigureAwait(false);
        }

        var slideOptions = ResolveSlideGroupingOptions(request);
        IReadOnlyList<SlideArtifact> slides;
        using (timings.Measure(RunTimingStages.Slides))
        {
            slides = SlideGrouper.Group(frames, slideOptions);
        }

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

        // Local vision needs per-frame OCR to populate the structured fields (title, bullets,
        // code blocks, UI elements). When the caller asked for `--vision --vision-provider local`
        // without `--ocr`, transparently flip OCR on and record the implicit decision so the
        // orchestrator sees what happened.
        var effectiveUseOcr = request.UseOcr;
        if (!effectiveUseOcr
            && request.UseVision
            && VisionProviderFactory.Normalize(request.VisionProvider) == VisionProviders.Local
            && primarySlides.Length > 0)
        {
            effectiveUseOcr = true;
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalOcrRequired,
                "Local vision provider requires per-frame OCR results; OCR was auto-enabled for this run. Pass `--ocr` explicitly to silence this warning.",
                Source: "vision",
                Severity: ReplayWarningSeverities.Info));
        }

        var ocrResults = new List<OcrFrameResult>();
        if (effectiveUseOcr && primarySlides.Length > 0)
        {
            using var ocrScope = timings.Measure(RunTimingStages.Ocr);
            var ocrProviderName = OcrProviderFactory.Normalize(request.OcrProvider);
            IOcrProvider? ocrProvider = null;
            try
            {
                (ocrProvider, llm) = await ResolveOcrProviderAsync(ocrProviderName, request, llm, warnings, progress, cancellationToken).ConfigureAwait(false);
                if (ocrProvider is not null)
                {
                    foreach (var slide in primarySlides)
                    {
                        var primaryFrame = frames.First(frame => frame.Id == slide.PrimaryFrameId);
                        progress?.Report($"Running OCR ({ocrProviderName}) on {primaryFrame.Path} (slide {slide.Id})...");
                        string raw;
                        try
                        {
                            raw = await ocrProvider.ExtractTextAsync(run.GetPath(primaryFrame.Path), request.OcrInstruction, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ReplayException ex) when (ocrProviderName == OcrProviders.Local)
                        {
                            warnings.Add(new ReplayWarning(
                                ReplayWarningCodes.OcrLocalInferenceFailed,
                                $"Local OCR failed on {slide.Id}: {ex.Message}",
                                Source: "ocr",
                                Severity: ReplayWarningSeverities.Warning));
                            continue;
                        }

                        var parseResult = StructuredResponseParser.ParseOcrWithMode(raw);
                        var structured = parseResult.Structured;
                        if (parseResult.IsFallback)
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
                            Structured: structured,
                            Provider: ocrProviderName);
                        ocrResults.Add(result);
                        await artifactStore.WriteJsonAsync(run, $"ocr/{primaryFrame.Id}.json", result, cancellationToken).ConfigureAwait(false);
                    }

                    if (ocrResults.Count > 0)
                    {
                        ocrPath = "ocr/combined.md";
                        await artifactStore.WriteTextAsync(run, ocrPath, FormatOcr(ocrResults), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                (ocrProvider as IDisposable)?.Dispose();
            }
        }

        var visionResults = new List<VisionFrameResult>();
        if (request.UseVision && primarySlides.Length > 0)
        {
            using var visionScope = timings.Measure(RunTimingStages.Vision);
            var visionProviderName = VisionProviderFactory.Normalize(request.VisionProvider);
            IVisionProvider? visionProvider = null;
            try
            {
                if (visionProviderName == VisionProviders.Local)
                {
                    visionProvider = ResolveLocalVisionProvider(request, ocrResults, warnings);
                }
                else if (visionProviderName == VisionProviders.Copilot)
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
                        visionProvider = new CopilotVisionProvider(llm, request.Model);
                    }
                }
                else
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.VisionUnknownProvider,
                        $"Unknown vision provider '{request.VisionProvider}'. Use one of: copilot, local.",
                        Source: "vision",
                        Severity: ReplayWarningSeverities.Error));
                }

                if (visionProvider is not null)
                {
                    foreach (var slide in primarySlides)
                    {
                        var primaryFrame = frames.First(frame => frame.Id == slide.PrimaryFrameId);
                        progress?.Report($"Analyzing slide {slide.Id} ({primaryFrame.Path})...");
                        var ocrContext = ocrResults.FirstOrDefault(o => o.FrameId == primaryFrame.Id);
                        var visionInput = new VisionRequest(
                            ImagePath: run.GetPath(primaryFrame.Path),
                            Instruction: request.VisionInstruction,
                            Frame: primaryFrame,
                            OcrContext: ocrContext);
                        string raw;
                        try
                        {
                            raw = await visionProvider.DescribeAsync(visionInput, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (visionProviderName == VisionProviders.Local && ex is not OperationCanceledException)
                        {
                            warnings.Add(new ReplayWarning(
                                ReplayWarningCodes.VisionLocalInferenceFailed,
                                $"Local vision inference failed for {slide.Id}: {ex.Message}. Frame skipped.",
                                Source: "vision",
                                Severity: ReplayWarningSeverities.Warning));
                            continue;
                        }
                        var visionParseResult = StructuredResponseParser.ParseVisionWithMode(raw);
                        var structured = visionParseResult.Structured;
                        if (visionParseResult.IsFallback)
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
                            Structured: structured,
                            Provider: visionProviderName);
                        visionResults.Add(result);
                        await artifactStore.WriteJsonAsync(run, $"vision/{primaryFrame.Id}.json", result, cancellationToken).ConfigureAwait(false);
                    }

                    visionPath = "vision/combined.md";
                    await artifactStore.WriteTextAsync(run, visionPath, FormatVision(visionResults), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                (visionProvider as IDisposable)?.Dispose();
            }
        }

        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
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
        using (timings.Measure(RunTimingStages.Evidence))
        {
            await artifactStore.WriteTextAsync(run, "evidence.md", evidenceMarkdown, cancellationToken).ConfigureAwait(false);
            await artifactStore.WriteJsonAsync(run, "evidence.json", evidence, cancellationToken).ConfigureAwait(false);

            if (slides.Count > 0)
            {
                await artifactStore.WriteJsonAsync(run, "slides/slides.json", slides, cancellationToken).ConfigureAwait(false);
            }
        }

        var manifest = new ArtifactManifest(
            SchemaVersion: "0.8",
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
            Warnings: warnings,
            Timings: timings.ToArtifact());

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

    /// <summary>
    /// Build a <see cref="LocalOnnxVisionProvider"/> from the request + ambient config. Records
    /// any model-availability degradations as warnings on the run so the orchestrator can
    /// audit which mode actually ran (heuristic, clip, or clip-blip). Returns null when the
    /// underlying <see cref="LocalVisionOptions"/> could not be resolved at all (a hard
    /// configuration error rather than missing models).
    /// </summary>
    private LocalOnnxVisionProvider? ResolveLocalVisionProvider(AnalyzeRequest request, IReadOnlyList<OcrFrameResult> ocrResults, List<ReplayWarning> warnings)
    {
        LocalVisionOptions options;
        try
        {
            options = LocalVisionOptions.Resolve();
            if (!string.IsNullOrWhiteSpace(request.LocalVisionMode))
            {
                options = options with { Mode = VisionProviderFactory.NormalizeMode(request.LocalVisionMode) };
            }
        }
        catch (Exception ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalInitFailed,
                $"Local vision provider could not be initialised: {ex.Message}",
                Source: "vision",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }

        var requestedMode = options.Mode;
        var missing = options.MissingFilesFor(requestedMode);
        if (missing.Count > 0 && requestedMode != LocalVisionMode.Heuristic)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalModelsMissing,
                $"Local vision mode '{VisionProviderFactory.FormatMode(requestedMode)}' requires {missing.Count} model file(s) that are not present on disk: {string.Join(", ", missing)}. The provider will degrade to the highest mode whose files exist.",
                Source: "vision",
                Severity: ReplayWarningSeverities.Warning));
        }

        if (ocrResults.Count == 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalOcrRequired,
                "Local vision provider needs per-frame OCR results to populate the structured fields (title, bullets, code blocks, UI elements). Re-run with --ocr enabled, or expect those fields to remain empty.",
                Source: "vision",
                Severity: ReplayWarningSeverities.Info));
        }

        var provider = new LocalOnnxVisionProvider(options, ffmpeg);
        provider.Initialise();

        foreach (var initWarning in provider.InitializationWarnings)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalModeDegraded,
                initWarning,
                Source: "vision",
                Severity: ReplayWarningSeverities.Warning));
        }

        if (provider.EffectiveMode != requestedMode)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.VisionLocalModeDegraded,
                $"Local vision degraded from '{VisionProviderFactory.FormatMode(requestedMode)}' to '{VisionProviderFactory.FormatMode(provider.EffectiveMode)}' because not all required models were available.",
                Source: "vision",
                Severity: ReplayWarningSeverities.Warning));
        }

        return provider;
    }

    /// <summary>
    /// Run the fully-local Whisper.net STT path. Mirrors the LLM-backed branch above:
    /// drives <see cref="ChunkedTranscriptionService"/>, writes <c>transcript.md</c> and
    /// (when chunking actually splits the input) <c>audio/chunks/chunks.json</c>. All failures
    /// are surfaced as structured warnings instead of exceptions so the pipeline always lands
    /// on a coherent artifact tree, matching the existing
    /// <c>OCR_LOCAL_*</c> error-handling pattern.
    /// </summary>
    private async Task<LocalWhisperResult?> TryRunLocalWhisperAsync(
        string audioPath,
        VideoRun run,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var config = new ConfigStore().Load();
        LocalWhisperOptions options;
        try
        {
            options = LocalWhisperOptions.Resolve(config);
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.SttLocalInitFailed,
                $"Failed to resolve local Whisper options: {ex.Message}",
                Source: "stt",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }

        var modelPath = options.ModelPath;
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            if (IsLocalWhisperAutoDownloadEnabled(config))
            {
                progress?.Report("Local Whisper model missing; auto-downloading ggml model (this can take several minutes)...");
                try
                {
                    var installer = new PortableDependencyInstaller(config);
                    await installer.InstallAsync(
                        ["whisper-model"],
                        force: false,
                        progress,
                        cancellationToken,
                        whisperModelSize: config.Llm.LocalWhisper.ModelSize ?? LocalWhisperOptions.DefaultModelSize).ConfigureAwait(false);
                    options = LocalWhisperOptions.Resolve(config);
                    modelPath = options.ModelPath;
                }
                catch (Exception ex)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.SttLocalModelMissing,
                        $"Local Whisper auto-download failed: {ex.Message}. Run `zakira-replay deps install whisper-model {LocalWhisperOptions.DefaultModelSize}` manually.",
                        Source: "stt",
                        Severity: ReplayWarningSeverities.Error));
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.SttLocalModelMissing,
                    $"Local Whisper model not found. Run `zakira-replay deps install whisper-model {LocalWhisperOptions.DefaultModelSize}` (or set `llm.localWhisper.autoDownload=true`). Looked for: {modelPath ?? "<unresolved>"}.",
                    Source: "stt",
                    Severity: ReplayWarningSeverities.Error));
                return null;
            }
        }

        progress?.Report($"Transcribing audio with local Whisper ({Path.GetFileName(modelPath)}, chunked)...");
        try
        {
            using var provider = new LocalWhisperTranscriptionProvider(options);
            var chunkedService = new ChunkedTranscriptionService(provider, new AudioChunker(ffmpeg));
            var chunkedResult = await chunkedService.TranscribeAsync(run.GetPath(audioPath), run, options: null, progress, cancellationToken).ConfigureAwait(false);
            var markdownPath = run.GetPath("transcript.md");
            await File.WriteAllTextAsync(markdownPath, chunkedResult.MarkdownTranscript + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            var transcript = new TranscriptArtifact(run.GetPath(audioPath), markdownPath, "local-whisper-audio-transcription");
            if (chunkedResult.Chunks.Chunks.Count > 1)
            {
                await artifactStore.WriteJsonAsync(run, "audio/chunks/chunks.json", chunkedResult.Chunks, cancellationToken).ConfigureAwait(false);
            }

            foreach (var chunkWarning in chunkedResult.ChunkedTranscriptionWarnings)
            {
                warnings.Add(chunkWarning);
            }

            return new LocalWhisperResult(transcript);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplayException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.SttLocalInferenceFailed,
                ex.Message,
                Source: "stt",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }
        catch (Exception ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.SttLocalInferenceFailed,
                $"Local Whisper transcription failed: {ex.Message}",
                Source: "stt",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }
    }

    private static bool IsLocalWhisperAutoDownloadEnabled(ReplayConfig config)
    {
        // Mirror the OCR autodownload env override so air-gapped / bandwidth-constrained
        // environments can disable on-demand downloads without touching persistent config.
        var fromEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_AUTODOWNLOAD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var normalized = fromEnv.Trim().ToLowerInvariant();
            if (normalized is "false" or "0" or "no")
            {
                return false;
            }
            if (normalized is "true" or "1" or "yes")
            {
                return true;
            }
        }

        return config.Llm.LocalWhisper.AutoDownload;
    }

    private sealed record LocalWhisperResult(TranscriptArtifact Transcript);

    /// <summary>
    /// Run the local sherpa-onnx diarization pass. Reads the existing <c>transcript.md</c> the
    /// caption / STT step produced, attributes each segment to a speaker cluster via
    /// <see cref="DiarizationMerger"/>, and rewrites the markdown with <c>[SPEAKER_NN]</c>
    /// prefixes so the rest of the pipeline (<see cref="TranscriptNormalizer"/>, evidence
    /// alignment, search) picks up the new attribution automatically.
    /// </summary>
    private async Task TryRunDiarizationAsync(
        AnalyzeRequest request,
        VideoRun run,
        string? audioPath,
        TranscriptArtifact? transcript,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationNoAudio,
                "Diarization was requested but no audio artifact was available. Diarization needs the 16 kHz mono PCM WAV the audio extraction stage produces.",
                Source: "ffmpeg",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        if (transcript is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationNoTranscript,
                "Diarization was requested but no transcript was extracted. Diarization labels existing transcript segments; enable --stt or supply captions.",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        var providerName = DiarizationProviderFactory.Normalize(null);
        if (providerName != DiarizationProviders.SherpaOnnx)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationUnknownProvider,
                $"Unknown diarization provider: {providerName}. Only `sherpa-onnx` is wired in this release.",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        var config = new ConfigStore().Load();
        DiarizationOptions options;
        try
        {
            options = DiarizationOptions.Resolve(config) with
            {
                NumSpeakers = request.NumSpeakers ?? config.Diarization.NumSpeakers,
                Threshold = request.DiarizationThreshold ?? config.Diarization.Threshold
            };
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationInitFailed,
                $"Failed to resolve diarization options: {ex.Message}",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        var missing = options.MissingFiles();
        if (missing.Count > 0 && IsDiarizationAutoDownloadEnabled(config))
        {
            progress?.Report("Diarization models missing; auto-downloading pyannote-segmentation-3.0 + 3D-Speaker (~32 MB)...");
            try
            {
                var installer = new PortableDependencyInstaller(config);
                await installer.InstallAsync(["diarization"], force: false, progress, cancellationToken).ConfigureAwait(false);
                options = DiarizationOptions.Resolve(config) with
                {
                    NumSpeakers = request.NumSpeakers ?? config.Diarization.NumSpeakers,
                    Threshold = request.DiarizationThreshold ?? config.Diarization.Threshold
                };
                missing = options.MissingFiles();
            }
            catch (Exception ex)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.DiarizationModelsMissing,
                    $"Diarization auto-download failed: {ex.Message}. Run `zakira-replay deps install diarization` manually.",
                    Source: "diarization",
                    Severity: ReplayWarningSeverities.Error));
                return;
            }
        }

        if (missing.Count > 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationModelsMissing,
                $"Diarization models not found. Run `zakira-replay deps install diarization` (or set `diarization.autoDownload=true`). Missing: {string.Join(", ", missing)}.",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        IReadOnlyList<DiarizationSegment> segments;
        try
        {
            using var provider = new SherpaOnnxDiarizationProvider();
            progress?.Report($"Diarizing audio (provider={providerName}, model={Path.GetFileName(options.SegmentationModelPath)})...");
            segments = await provider.DiarizeAsync(run.GetPath(audioPath), options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplayException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationFailed,
                ex.Message,
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }
        catch (Exception ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationFailed,
                $"Diarization failed: {ex.Message}",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        if (segments.Count == 0)
        {
            // No speakers detected. Surface as info-level so orchestrators can branch but don't
            // treat as an error — silent / non-speech audio is a legitimate input.
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationFailed,
                "Diarization completed but produced no speaker segments. Audio may be silent, music-only, or below the model's detection threshold.",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Info));
            return;
        }

        // Rewrite transcript.md with [SPEAKER_NN] prefixes so the rest of the pipeline
        // (TranscriptNormalizer, evidence.json speakers[] rollup, evidence-aligned slide /
        // chapter speaker rollups, search) picks up the attribution automatically.
        var markdownPath = run.GetPath("transcript.md");
        if (!File.Exists(markdownPath))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.DiarizationNoTranscript,
                $"transcript.md disappeared between STT and diarization: {markdownPath}.",
                Source: "diarization",
                Severity: ReplayWarningSeverities.Error));
            return;
        }

        var existing = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        var annotated = DiarizationMerger.AnnotateMarkdown(existing, segments);
        await File.WriteAllTextAsync(markdownPath, annotated + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        progress?.Report($"Diarization wrote {segments.Count} speaker segments across {segments.Select(s => s.SpeakerId).Distinct().Count()} speaker(s).");
    }

    private static bool IsDiarizationAutoDownloadEnabled(ReplayConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_AUTODOWNLOAD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var normalized = fromEnv.Trim().ToLowerInvariant();
            if (normalized is "false" or "0" or "no")
            {
                return false;
            }
            if (normalized is "true" or "1" or "yes")
            {
                return true;
            }
        }

        return config.Diarization.AutoDownload;
    }

    private static bool IsOcrLocalAutoDownloadEnabled(ReplayConfig config)
    {
        // Env-var override gives test harnesses (and users in air-gapped or
        // bandwidth-constrained environments) a way to disable on-demand model downloads
        // without rewriting their persistent config. Accepted values: "false"/"0"/"no" disable;
        // anything else (or unset) falls through to the config value.
        var fromEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_LOCAL_AUTODOWNLOAD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var normalized = fromEnv.Trim().ToLowerInvariant();
            if (normalized is "false" or "0" or "no")
            {
                return false;
            }
            if (normalized is "true" or "1" or "yes")
            {
                return true;
            }
        }

        return config.Ocr.Local.AutoDownload;
    }

    private async Task<(IOcrProvider? Provider, ILlmProvider? UpdatedLlm)> ResolveOcrProviderAsync(string ocrProviderName, AnalyzeRequest request, ILlmProvider? llm, List<ReplayWarning> warnings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        switch (ocrProviderName)
        {
            case OcrProviders.Local:
            {
                var config = new ConfigStore().Load();
                LocalOcrModelPaths paths;
                try
                {
                    paths = LocalOcrModelPaths.Resolve(config);
                }
                catch (Exception ex) when (ex is not ReplayException)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.OcrLocalInitFailed,
                        $"Failed to resolve local OCR model paths: {ex.Message}",
                        Source: "ocr",
                        Severity: ReplayWarningSeverities.Error));
                    return (null, llm);
                }

                var missing = paths.MissingFiles();
                if (missing.Count > 0 && IsOcrLocalAutoDownloadEnabled(config))
                {
                    progress?.Report("Local OCR models missing; auto-downloading RapidOCR PP-OCRv5 (~30 MB)...");
                    try
                    {
                        var installer = new PortableDependencyInstaller(config);
                        await installer.InstallAsync(["ocr"], force: false, progress, cancellationToken).ConfigureAwait(false);
                        // Re-resolve in case the download succeeded.
                        paths = LocalOcrModelPaths.Resolve(config);
                        missing = paths.MissingFiles();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(new ReplayWarning(
                            ReplayWarningCodes.OcrLocalModelsMissing,
                            $"Local OCR auto-download failed: {ex.Message}. Run `zakira-replay deps install ocr` manually.",
                            Source: "ocr",
                            Severity: ReplayWarningSeverities.Error));
                        return (null, llm);
                    }
                }

                if (missing.Count > 0)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.OcrLocalModelsMissing,
                        $"Local OCR models not found. Run `zakira-replay deps install ocr` (or set `ocr.local.autoDownload=true`). Missing: {string.Join(", ", missing)}.",
                        Source: "ocr",
                        Severity: ReplayWarningSeverities.Error));
                    return (null, llm);
                }

                try
                {
                    return (new LocalOnnxOcrProvider(paths), llm);
                }
                catch (Exception ex)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.OcrLocalInitFailed,
                        $"Failed to initialise local OCR engine: {ex.Message}",
                        Source: "ocr",
                        Severity: ReplayWarningSeverities.Error));
                    return (null, llm);
                }
            }
            case OcrProviders.Copilot:
            {
                llm ??= TryResolveLlm(request);
                if (llm is null)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.OcrNoLlmProvider,
                        "OCR was requested but no LLM provider is configured.",
                        Source: "llm",
                        Severity: ReplayWarningSeverities.Error));
                    return (null, null);
                }

                return (new CopilotOcrProvider(llm, request.Model), llm);
            }
            default:
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.OcrUnknownProvider,
                    $"Unknown OCR provider '{ocrProviderName}'. Supported: copilot, local.",
                    Source: "ocr",
                    Severity: ReplayWarningSeverities.Error));
                return (null, llm);
        }
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

    private static string ResolveCaptureMode(AnalyzeRequest request, List<ReplayWarning> warnings)
    {
        var config = new ConfigStore().Load();
        var raw = CaptureModes.Normalize(request.CaptureMode ?? config.Capture.Mode);
        if (CaptureModes.IsKnown(raw))
        {
            return raw;
        }

        warnings.Add(new ReplayWarning(
            ReplayWarningCodes.CaptureUnknownMode,
            $"Unknown capture mode '{raw}'. Falling back to '{CaptureModes.YtDlp}'.",
            Source: "playwright",
            Severity: ReplayWarningSeverities.Warning));
        return CaptureModes.YtDlp;
    }

    private async Task<IReadOnlyList<FrameArtifact>> CaptureFramesWithBrowserAsync(
        AnalyzeRequest request,
        VideoRun run,
        int frameCount,
        YtDlpInfo info,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var (frames, _) = await CaptureFramesAndCaptionsWithBrowserAsync(request, run, frameCount, info, warnings, progress, cancellationToken).ConfigureAwait(false);
        return frames;
    }

    private async Task<(IReadOnlyList<FrameArtifact> Frames, IReadOnlyList<BrowserCapturedCaption> Captions)> CaptureFramesAndCaptionsWithBrowserAsync(
        AnalyzeRequest request,
        VideoRun run,
        int frameCount,
        YtDlpInfo info,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (browserCaptureClient is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserUnavailable,
                "Browser-capture mode was requested but no IBrowserVideoCaptureClient was provided. The CLI auto-wires PlaywrightVideoCaptureClient; tests must inject one explicitly.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Error));
            return ([], []);
        }

        var config = new ConfigStore().Load();
        var authStoragePath = ResolveAuthProfilePath(request, config, warnings);
        var browserCaptureRequest = new BrowserCaptureRequest(
            Url: request.Source,
            Run: run,
            FrameCount: frameCount > 0 ? frameCount : 7,
            PlayButtonSelector: config.Capture.Browser.PlayButtonSelector,
            VideoElementSelector: string.IsNullOrWhiteSpace(config.Capture.Browser.VideoElementSelector) ? "video" : config.Capture.Browser.VideoElementSelector,
            SeekWaitSeconds: config.Capture.Browser.SeekWaitSeconds,
            DurationProbeTimeoutSeconds: config.Capture.Browser.DurationProbeTimeoutSeconds,
            JpegQuality: config.Capture.Browser.JpegQuality,
            CaptureCaptions: config.Capture.Browser.CaptureCaptions,
            MaxCaptionBytes: config.Capture.Browser.MaxCaptionBytes,
            AuthStorageStatePath: authStoragePath);

        var result = await browserCaptureClient.CaptureAsync(browserCaptureRequest, progress, cancellationToken).ConfigureAwait(false);
        foreach (var warning in result.Warnings)
        {
            warnings.Add(warning);
        }

        if (result.DurationSeconds is { } duration && info.DurationSeconds is null)
        {
            info.DurationSeconds = duration;
        }

        return (result.Frames, result.Captions);
    }

    private static string? ResolveAuthProfilePath(AnalyzeRequest request, ReplayConfig config, List<ReplayWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.AuthProfile))
        {
            return null;
        }

        AuthProfile? profile;
        try
        {
            var store = new AuthProfileStore(config);
            profile = store.TryRead(request.AuthProfile);
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.AuthProfileLoadFailed,
                $"Failed to resolve auth profile '{request.AuthProfile}': {ex.Message}",
                Source: "auth",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }

        if (profile is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.AuthProfileNotFound,
                $"Auth profile '{request.AuthProfile}' not found. Run `zakira-replay auth login {request.AuthProfile}` to create one.",
                Source: "auth",
                Severity: ReplayWarningSeverities.Error));
            return null;
        }

        if (profile.IsStale)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.AuthProfileStale,
                $"Auth profile '{request.AuthProfile}' is {profile.FormatAge()} old (threshold {config.Auth.StaleThresholdMinutes} min); SSO sessions and CDN cookies may have expired. Run `zakira-replay auth login {request.AuthProfile}` to refresh.",
                Source: "auth",
                Severity: ReplayWarningSeverities.Info));
        }

        return profile.Path;
    }

    private async Task<TranscriptArtifact?> TryFillTranscriptFromBrowserCaptionsAsync(
        AnalyzeRequest request,
        VideoRun run,
        YtDlpInfo info,
        IReadOnlyList<BrowserCapturedCaption> captions,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (captions.Count == 0)
        {
            return null;
        }

        var preferences = ResolveSubtitleLanguages(request.CaptionLanguages, info);
        var pick = BrowserCaptionInterceptor.PickBest(captions, preferences);
        if (pick is null)
        {
            return null;
        }

        var captionPath = run.GetPath(pick.RelativePath);
        if (!File.Exists(captionPath))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptionsBrowserNetworkParseFailed,
                $"Browser-discovered caption {pick.RelativePath} was selected but is missing on disk.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Warning));
            return null;
        }

        IReadOnlyList<TranscriptSegment> segments;
        try
        {
            segments = await SubtitleConverter.ParseSegmentsAsync(captionPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptionsBrowserNetworkParseFailed,
                $"Failed to parse browser-discovered caption {pick.RelativePath}: {ex.Message}",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Warning));
            return null;
        }

        if (segments.Count == 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptionsBrowserNetworkParseFailed,
                $"Browser-discovered caption {pick.RelativePath} parsed to zero segments.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Warning));
            return null;
        }

        var languageLabel = string.IsNullOrWhiteSpace(pick.InferredLanguage) ? "unknown" : pick.InferredLanguage;
        progress?.Report($"Using browser-discovered captions for transcript ({languageLabel}, {pick.RelativePath}).");

        var markdownPath = run.GetPath("transcript.md");
        await File.WriteAllTextAsync(markdownPath, SubtitleConverter.ToMarkdown(segments), cancellationToken).ConfigureAwait(false);

        return new TranscriptArtifact(captionPath, markdownPath, "browser-network", segments);
    }

    private static IReadOnlyList<FrameArtifact> ApplySmartCrop(
        IReadOnlyList<FrameArtifact> frames,
        VideoRun run,
        AnalyzeRequest request,
        List<ReplayWarning> warnings,
        IProgress<string>? progress)
    {
        var config = new ConfigStore().Load();
        var enabled = request.SmartCrop ?? config.Crop.Enabled;
        if (!enabled)
        {
            return frames;
        }

        var requestedProfile = SmartCropProfiles.Normalize(request.SmartCropProfile ?? config.Crop.Profile);
        if (requestedProfile == SmartCropProfiles.Off)
        {
            return frames;
        }

        if (!SmartCropProfiles.IsKnown(requestedProfile))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CropProfileUnknown,
                $"Unknown smart-crop profile '{requestedProfile}'. Falling back to '{SmartCropProfiles.Auto}'.",
                Source: "crop",
                Severity: ReplayWarningSeverities.Warning));
            requestedProfile = SmartCropProfiles.Auto;
        }

        progress?.Report($"Smart-cropping {frames.Count} frame(s) for UI chrome (profile: {requestedProfile})...");
        var service = new SmartCropService();
        var processed = new List<FrameArtifact>(frames.Count);
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            var outcome = service.Process(frame, run, requestedProfile);
            processed.Add(outcome.Frame);
            if (outcome.Warning is not null && seenWarnings.Add(outcome.Warning.Code))
            {
                warnings.Add(outcome.Warning);
            }
        }

        return processed;
    }

    /// <summary>
    /// Resolves the effective number of frames to extract for interval / every-frame strategies.
    /// When <see cref="AnalyzeRequest.FramesPerMinute"/> is set (or
    /// <paramref name="defaultFramesPerMinute"/> is non-zero), scales by duration with
    /// <see cref="AnalyzeRequest.FrameCount"/> as a floor. Returns <see cref="AnalyzeRequest.FrameCount"/>
    /// verbatim otherwise. The scene strategy ignores this value entirely (it is bounded by the
    /// scene safety cap).
    /// </summary>
    internal static int ResolveEffectiveFrameCount(AnalyzeRequest request, double? durationSeconds, int defaultFramesPerMinute = 0)
    {
        var configuredFramesPerMinute = request.FramesPerMinute;
        if (configuredFramesPerMinute is null && defaultFramesPerMinute > 0)
        {
            configuredFramesPerMinute = defaultFramesPerMinute;
        }

        if (configuredFramesPerMinute is not { } framesPerMinute || framesPerMinute <= 0 || durationSeconds is not { } durationSecs || durationSecs <= 0)
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
    int MaxAiFrames = 50,
    string Model = GitHubCopilotLlmProvider.DefaultModel,
    string LlmProvider = LlmProviders.GitHubCopilot,
    bool Force = false,
    bool UseCache = false,
    string FrameStrategy = FrameSelectionStrategies.Scene,
    string? CookiesPath = null,
    string? CookiesFromBrowser = null,
    IReadOnlyList<string>? CaptionLanguages = null,
    bool? SlideGrouping = null,
    int? SlideHashDistance = null,
    int? FramesPerMinute = null,
    int? SceneSafetyCap = null,
    string OcrProvider = OcrProviders.Local,
    bool? SmartCrop = null,
    string? SmartCropProfile = null,
    string? CaptureMode = null,
    string? AuthProfile = null,
    bool UseDiarization = false,
    int? NumSpeakers = null,
    float? DiarizationThreshold = null,
    string VisionProvider = VisionProviders.Copilot,
    string? LocalVisionMode = null);

public static class FrameSelectionStrategies
{
    public const string Interval = "interval";

    public const string Scene = "scene";

    public const string EveryFrame = "every-frame";

    /// <summary>
    /// Ad-hoc capture strategy: caller supplies an explicit list of timestamps. Used by
    /// <see cref="FrameCaptureService"/> for spot captures (not by <see cref="AnalysisPipeline"/>).
    /// </summary>
    public const string Timestamps = "timestamps";

    /// <summary>
    /// Ad-hoc capture strategy: caller supplies a <c>[start, end]</c> window plus a per-window
    /// frame budget; used by <see cref="FrameCaptureService"/>.
    /// </summary>
    public const string Range = "range";
}

public sealed record AnalyzeResult(VideoRun Run, ArtifactManifest Manifest, bool Reused = false);
