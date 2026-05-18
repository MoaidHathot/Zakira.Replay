using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

/// <summary>
/// All Zakira.Replay MCP tools live here as instance methods decorated with
/// <see cref="McpServerToolAttribute"/>. The MCP SDK auto-generates each tool's input
/// schema from the method signature, so we don't hand-maintain JSON schemas anymore.
/// Tool names follow the <c>verb.noun</c> convention shipped in 0.9.0; the surface is a
/// hard break from the 0.8 names (analyze_video → analyze, build_search_index → index.build,
/// etc.).
///
/// Parameters are intentionally enumerated inline on each method instead of bundled into
/// parameter records. This keeps the SDK-generated input schema flat (top-level properties
/// agents can fill in directly) rather than nested under a wrapper object.
/// </summary>
[McpServerToolType]
public sealed class ReplayTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly Func<AnalysisPipeline> pipelineFactory;
    private readonly McpJobManager jobManager;
    private readonly ClipExtractionService clipService;
    private readonly FrameCaptureService frameCaptureService;
    private readonly SearchIndexService searchService;
    private readonly ChapterBuilder chapterBuilder;
    private readonly EvidenceAlignmentService alignmentService;
    private readonly DiscoveryService discoveryService;
    private readonly DependencyResolver dependencyResolver;

    public ReplayTools(
        Func<AnalysisPipeline> pipelineFactory,
        McpJobManager jobManager,
        ClipExtractionService clipService,
        FrameCaptureService frameCaptureService,
        SearchIndexService searchService,
        ChapterBuilder chapterBuilder,
        EvidenceAlignmentService alignmentService,
        DiscoveryService discoveryService,
        DependencyResolver dependencyResolver)
    {
        this.pipelineFactory = pipelineFactory;
        this.jobManager = jobManager;
        this.clipService = clipService;
        this.frameCaptureService = frameCaptureService;
        this.searchService = searchService;
        this.chapterBuilder = chapterBuilder;
        this.alignmentService = alignmentService;
        this.discoveryService = discoveryService;
        this.dependencyResolver = dependencyResolver;
    }

    // ---------------------------------------------------------------------------------------
    //  Analysis: synchronous + background-job lifecycle
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "analyze")]
    [Description("Runs a synchronous video analysis and returns the final job snapshot. Best for short videos (≤10 min). For long videos prefer analyze.start + analyze.result so the connection isn't held open while the pipeline runs.")]
    public async Task<string> AnalyzeAsync(
        [Description("Video URL or local media path.")] string source,
        [Description("Optional focus signal appended to the vision prompt.")] string? visionInstruction = null,
        [Description("Optional focus signal appended to the OCR prompt.")] string? ocrInstruction = null,
        [Description("Skip transcript extraction (frame-only run).")] bool noTranscript = false,
        [Description("Number of representative frames to extract. Defaults to 500.")] int frames = 500,
        [Description("Optional run id; reused for deterministic artifact folders.")] string? runId = null,
        [Description("Extract audio with ffmpeg.")] bool audio = false,
        [Description("Use the configured LLM provider to transcribe extracted audio when captions are missing.")] bool stt = false,
        [Description("Run OCR over selected frames.")] bool ocr = false,
        [Description("Use the configured LLM provider to describe selected frames.")] bool vision = false,
        [Description("Run sherpa-onnx speaker diarization on top of the transcript.")] bool diarize = false,
        [Description("Optional speaker count hint for diarization.")] int? numSpeakers = null,
        [Description("Optional diarization threshold (0..1).")] double? diarizeThreshold = null,
        [Description("Maximum frames to send to AI providers. Defaults to 50.")] int maxAiFrames = 50,
        [Description("LLM provider: github-copilot, openai, azure-openai, ollama, local-whisper.")] string? llmProvider = null,
        [Description("Provider model id.")] string? model = null,
        [Description("OCR provider: copilot or local.")] string? ocrProvider = null,
        [Description("Vision provider: copilot or local.")] string? visionProvider = null,
        [Description("Local vision sub-mode: heuristic, clip, or clip-blip.")] string? localVisionMode = null,
        [Description("Run smart-crop preprocessing on each frame before hashing/OCR/vision.")] bool? smartCrop = null,
        [Description("Smart-crop profile: auto, teams, zoom, webex, generic, off.")] string? smartCropProfile = null,
        [Description("Frame-capture mode: ytdlp, browser, auto.")] string? captureMode = null,
        [Description("Playwright auth profile name (created by `auth login`).")] string? authProfile = null,
        [Description("Frame selection strategy: interval, scene, every-frame.")] string? frameStrategy = null,
        [Description("Alias for frameStrategy=every-frame.")] bool everyFrame = false,
        [Description("Path to a Netscape cookies file for yt-dlp.")] string? cookies = null,
        [Description("Browser name/profile spec for yt-dlp --cookies-from-browser.")] string? cookiesFromBrowser = null,
        [Description("Caption/subtitle language preferences as a comma-separated string.")] string? captionLanguages = null,
        [Description("Group perceptually-similar frames into slides before OCR/vision.")] bool? slideGrouping = null,
        [Description("Maximum Hamming distance for slide grouping.")] int? slideHashDistance = null,
        [Description("Duration-aware sampling rate for the interval strategy.")] int? framesPerMinute = null,
        [Description("Per-run override of frames.sceneSafetyCap.")] int? sceneSafetyCap = null,
        [Description("Reuse a previous run with matching source and options.")] bool cache = false,
        [Description("Recompute even if the run id already has a completed manifest.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var request = BuildAnalyzeRequest(source, visionInstruction, ocrInstruction, noTranscript, frames, runId, audio, stt, ocr, vision,
            diarize, numSpeakers, diarizeThreshold, maxAiFrames, llmProvider, model, ocrProvider, visionProvider, localVisionMode,
            smartCrop, smartCropProfile, captureMode, authProfile, frameStrategy, everyFrame, cookies, cookiesFromBrowser,
            captionLanguages, slideGrouping, slideHashDistance, framesPerMinute, sceneSafetyCap, cache, force);
        var job = jobManager.Create(request);
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = job.Snapshot(includeLogs: true);
            if (snapshot.Status is "succeeded" or "failed" or "cancelled")
            {
                return Serialize(snapshot);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Serialize(job.Snapshot(includeLogs: true));
    }

    [McpServerTool(Name = "analyze.start")]
    [Description("Starts a background video analysis and returns a job id. Use analyze.status / analyze.result to poll progress, analyze.cancel to stop.")]
    public string AnalyzeStart(
        [Description("Video URL or local media path.")] string source,
        [Description("Optional focus signal appended to the vision prompt.")] string? visionInstruction = null,
        [Description("Optional focus signal appended to the OCR prompt.")] string? ocrInstruction = null,
        [Description("Skip transcript extraction (frame-only run).")] bool noTranscript = false,
        [Description("Number of representative frames to extract.")] int frames = 500,
        [Description("Optional run id.")] string? runId = null,
        [Description("Extract audio with ffmpeg.")] bool audio = false,
        [Description("Use the configured LLM provider for STT.")] bool stt = false,
        [Description("Run OCR over selected frames.")] bool ocr = false,
        [Description("Describe selected frames visually.")] bool vision = false,
        [Description("Run speaker diarization on top of the transcript.")] bool diarize = false,
        [Description("Speaker count hint.")] int? numSpeakers = null,
        [Description("Diarization threshold (0..1).")] double? diarizeThreshold = null,
        [Description("Max frames sent to AI providers.")] int maxAiFrames = 50,
        [Description("LLM provider.")] string? llmProvider = null,
        [Description("Provider model id.")] string? model = null,
        [Description("OCR provider.")] string? ocrProvider = null,
        [Description("Vision provider.")] string? visionProvider = null,
        [Description("Local vision sub-mode.")] string? localVisionMode = null,
        [Description("Smart-crop toggle.")] bool? smartCrop = null,
        [Description("Smart-crop profile.")] string? smartCropProfile = null,
        [Description("Capture mode: ytdlp, browser, auto.")] string? captureMode = null,
        [Description("Playwright auth profile name.")] string? authProfile = null,
        [Description("Frame strategy: interval, scene, every-frame.")] string? frameStrategy = null,
        [Description("Alias for frameStrategy=every-frame.")] bool everyFrame = false,
        [Description("Cookies file path.")] string? cookies = null,
        [Description("Browser/profile spec for cookies-from-browser.")] string? cookiesFromBrowser = null,
        [Description("Caption languages, comma-separated.")] string? captionLanguages = null,
        [Description("Slide-grouping toggle.")] bool? slideGrouping = null,
        [Description("Slide hash Hamming distance.")] int? slideHashDistance = null,
        [Description("Frames per minute for interval sampling.")] int? framesPerMinute = null,
        [Description("Per-run scene safety cap.")] int? sceneSafetyCap = null,
        [Description("Reuse cached run.")] bool cache = false,
        [Description("Force recompute.")] bool force = false)
    {
        var request = BuildAnalyzeRequest(source, visionInstruction, ocrInstruction, noTranscript, frames, runId, audio, stt, ocr, vision,
            diarize, numSpeakers, diarizeThreshold, maxAiFrames, llmProvider, model, ocrProvider, visionProvider, localVisionMode,
            smartCrop, smartCropProfile, captureMode, authProfile, frameStrategy, everyFrame, cookies, cookiesFromBrowser,
            captionLanguages, slideGrouping, slideHashDistance, framesPerMinute, sceneSafetyCap, cache, force);
        var job = jobManager.Create(request);
        return Serialize(job.Snapshot(includeLogs: false));
    }

    [McpServerTool(Name = "analyze.status")]
    [Description("Returns analysis job status and the most recent log messages.")]
    public string AnalyzeStatus(
        [Description("Job id returned by analyze.start.")] string jobId)
    {
        var job = jobManager.Get(jobId) ?? throw new McpException($"Unknown jobId: {jobId}");
        return Serialize(job.Snapshot(includeLogs: true));
    }

    [McpServerTool(Name = "analyze.result")]
    [Description("Returns the final analysis job result with manifest summary; reports {status:'running'} when the job hasn't finished yet.")]
    public string AnalyzeResult(
        [Description("Job id returned by analyze.start.")] string jobId)
    {
        var job = jobManager.Get(jobId) ?? throw new McpException($"Unknown jobId: {jobId}");
        var snapshot = job.Snapshot(includeLogs: true);
        if (snapshot.Status is not ("succeeded" or "failed" or "cancelled"))
        {
            return Serialize(new
            {
                snapshot.JobId,
                snapshot.Status,
                message = "Job is not complete yet.",
                snapshot.Logs
            });
        }

        return Serialize(snapshot);
    }

    [McpServerTool(Name = "analyze.cancel")]
    [Description("Cancels a running analysis job. Returns {cancelled:false} if the id is unknown or the job already finished.")]
    public string AnalyzeCancel(
        [Description("Job id returned by analyze.start.")] string jobId)
    {
        var cancelled = jobManager.Cancel(jobId);
        return Serialize(new { jobId, cancelled });
    }

    // ---------------------------------------------------------------------------------------
    //  Persistent queue
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "queue.enqueue")]
    [Description("Adds an analysis request to a persistent local queue under runs/.queue/. The queue survives process restarts.")]
    public async Task<string> QueueEnqueueAsync(
        [Description("Video URL or local media path.")] string source,
        [Description("Persistent queue id. Defaults to 'default'.")] string? queueId = null,
        [Description("Optional stable job id.")] string? jobId = null,
        [Description("Retry count beyond the first attempt.")] int retries = 0,
        [Description("Optional vision focus instruction.")] string? visionInstruction = null,
        [Description("Optional OCR focus instruction.")] string? ocrInstruction = null,
        [Description("Skip transcript extraction.")] bool noTranscript = false,
        [Description("Frame count.")] int frames = 500,
        [Description("Run id.")] string? runId = null,
        [Description("Extract audio.")] bool audio = false,
        [Description("Use STT.")] bool stt = false,
        [Description("Run OCR.")] bool ocr = false,
        [Description("Run vision.")] bool vision = false,
        [Description("Run diarization.")] bool diarize = false,
        [Description("Max AI frames.")] int maxAiFrames = 50,
        [Description("LLM provider.")] string? llmProvider = null,
        [Description("Model id.")] string? model = null,
        [Description("OCR provider.")] string? ocrProvider = null,
        [Description("Vision provider.")] string? visionProvider = null,
        [Description("Frame strategy.")] string? frameStrategy = null,
        [Description("Alias for frameStrategy=every-frame.")] bool everyFrame = false,
        [Description("Cookies file path.")] string? cookies = null,
        [Description("cookies-from-browser spec.")] string? cookiesFromBrowser = null,
        [Description("Reuse cached run.")] bool cache = false,
        [Description("Force recompute.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var queue = new AnalysisQueue(pipelineFactory);
        var request = new AnalyzeRequest(
            Source: source,
            VisionInstruction: visionInstruction ?? string.Empty,
            OcrInstruction: ocrInstruction ?? string.Empty,
            IncludeTranscript: !noTranscript,
            FrameCount: frames,
            RunId: runId,
            ExtractAudio: audio || stt,
            UseSpeechToText: stt,
            UseOcr: ocr,
            UseVision: vision,
            MaxAiFrames: maxAiFrames,
            Model: model ?? LlmProviderFactory.GetDefaultModel(llmProvider),
            LlmProvider: LlmProviderFactory.Normalize(llmProvider),
            Force: force,
            UseCache: cache,
            FrameStrategy: everyFrame ? FrameSelectionStrategies.EveryFrame : (frameStrategy ?? FrameSelectionStrategies.Scene),
            CookiesPath: cookies,
            CookiesFromBrowser: cookiesFromBrowser,
            OcrProvider: OcrProviderFactory.Normalize(ocrProvider),
            VisionProvider: VisionProviderFactory.Normalize(visionProvider),
            UseDiarization: diarize);
        var result = await queue.EnqueueAsync(queueId, request, jobId, retries, cancellationToken).ConfigureAwait(false);
        return Serialize(new
        {
            result.QueueId,
            result.JobId,
            result.QueueDirectory,
            result.Job
        });
    }

    [McpServerTool(Name = "queue.run")]
    [Description("Runs pending analysis queue jobs with local concurrency and retry limits.")]
    public async Task<string> QueueRunAsync(
        [Description("Persistent queue id. Defaults to 'default'.")] string? queueId = null,
        [Description("Number of queue jobs to run concurrently. Defaults to 1.")] int concurrency = 1,
        [Description("Retry count beyond the first attempt.")] int retries = 0,
        CancellationToken cancellationToken = default)
    {
        var queue = new AnalysisQueue(pipelineFactory);
        var result = await queue.RunAsync(queueId, new AnalysisQueueRunOptions(Concurrency: concurrency, Retries: retries), progress: null, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    [McpServerTool(Name = "queue.status")]
    [Description("Returns persistent analysis queue state and job statuses.")]
    public async Task<string> QueueStatusAsync(
        [Description("Persistent queue id. Defaults to 'default'.")] string? queueId = null,
        CancellationToken cancellationToken = default)
    {
        var queue = new AnalysisQueue(pipelineFactory);
        var state = await queue.GetStatusAsync(queueId, cancellationToken).ConfigureAwait(false);
        return Serialize(state);
    }

    // ---------------------------------------------------------------------------------------
    //  Clip / frame capture
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "clip")]
    [Description("Extracts a timestamped video clip from a URL or local media file.")]
    public async Task<string> ClipAsync(
        [Description("Video URL or local media path.")] string source,
        [Description("Clip start timestamp: seconds, MM:SS, or HH:MM:SS.")] string start,
        [Description("Clip end timestamp.")] string end,
        [Description("Optional run id.")] string? runId = null,
        [Description("Optional output file name.")] string? outputName = null,
        [Description("Cookies file path.")] string? cookies = null,
        [Description("cookies-from-browser spec.")] string? cookiesFromBrowser = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ClipExtractionRequest(
            Source: source,
            Start: Timestamp.ParseRequired(start, "start"),
            End: Timestamp.ParseRequired(end, "end"),
            RunId: runId,
            OutputName: outputName,
            CookiesPath: cookies,
            CookiesFromBrowser: cookiesFromBrowser);
        var result = await clipService.ExtractAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
        return Serialize(new
        {
            runId = result.Run.Id,
            artifactDirectory = result.Run.Directory,
            clipPath = result.Run.GetPath(result.Manifest.ClipPath),
            manifest = result.Manifest
        });
    }

    [McpServerTool(Name = "frames")]
    [Description("Ad-hoc frame capture from a URL or local media file. Pass either `at` (exact timestamps) or `from`/`to`/`count` (a time window). JPEG stills only; the analyze pipeline (slides/OCR/vision/alignment) is skipped for speed.")]
    public async Task<string> FramesAsync(
        [Description("Video URL or local media path.")] string source,
        [Description("Exact timestamps to capture, comma-separated.")] string? at = null,
        [Description("Window start (inclusive). Required with `to`.")] string? from = null,
        [Description("Window end (inclusive). Required with `from`.")] string? to = null,
        [Description("Frame count inside the window.")] int? count = null,
        [Description("Window strategy: interval (default) or scene.")] string? strategy = null,
        [Description("Optional run id.")] string? runId = null,
        [Description("Max long edge in pixels.")] int? maxLongEdgePixels = null,
        [Description("JPEG quality 1-100.")] int? jpegQuality = null,
        [Description("Compute 64-bit perceptual hashes.")] bool computePerceptualHash = false,
        [Description("Scene safety cap.")] int? sceneSafetyCap = null,
        [Description("Cookies file path.")] string? cookies = null,
        [Description("cookies-from-browser spec.")] string? cookiesFromBrowser = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TimeSpan>? timestamps = null;
        TimeSpan? rangeStart = null;
        TimeSpan? rangeEnd = null;
        if (!string.IsNullOrWhiteSpace(at))
        {
            timestamps = FrameCaptureInput.ParseTimestamps(at, "at");
        }
        if (!string.IsNullOrWhiteSpace(from))
        {
            rangeStart = Timestamp.ParseRequired(from, "from");
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            rangeEnd = Timestamp.ParseRequired(to, "to");
        }

        var request = new FrameCaptureRequest(
            Source: source,
            Timestamps: timestamps,
            RangeStart: rangeStart,
            RangeEnd: rangeEnd,
            RangeCount: count,
            RangeStrategy: string.IsNullOrWhiteSpace(strategy) ? FrameSelectionStrategies.Interval : strategy,
            RunId: runId,
            MaxLongEdgePixels: maxLongEdgePixels,
            JpegQuality: jpegQuality,
            ComputePerceptualHash: computePerceptualHash,
            SceneSafetyCap: sceneSafetyCap,
            CookiesPath: cookies,
            CookiesFromBrowser: cookiesFromBrowser);

        var result = await frameCaptureService.CaptureAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
        var resolvedFrames = result.Manifest.Frames
            .Select(frame => new
            {
                id = frame.Id,
                path = result.Run.GetPath(frame.Path),
                relativePath = frame.Path,
                timestampSeconds = frame.TimestampSeconds,
                timestampLabel = frame.TimestampLabel,
                perceptualHash = frame.PerceptualHash
            })
            .ToArray();

        return Serialize(new
        {
            runId = result.Run.Id,
            artifactDirectory = result.Run.Directory,
            manifestPath = result.Run.GetPath("frame-capture.json"),
            frameCount = resolvedFrames.Length,
            frames = resolvedFrames,
            warnings = result.Manifest.Warnings,
            manifest = result.Manifest
        });
    }

    // ---------------------------------------------------------------------------------------
    //  Search index
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "index.build")]
    [Description("Builds a local search index over a completed run's evidence.json.")]
    public async Task<string> IndexBuildAsync(
        [Description("Completed Zakira.Replay run directory containing evidence.json.")] string runDirectory,
        [Description("Search backend: json (default), sqlite, sqlite-onnx.")] string? backend = null,
        [Description("Optional ONNX embedding model path.")] string? onnxModel = null,
        [Description("Optional WordPiece vocabulary path.")] string? onnxVocab = null,
        [Description("Optional ONNX tokenizer sequence length.")] int? onnxMaxSequenceLength = null,
        [Description("Optional embedding dimension hint.")] int? embeddingDimensions = null,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchIndexBuildOptions(
            Backend: backend ?? SearchBackends.Json,
            OnnxModelPath: onnxModel,
            OnnxVocabularyPath: onnxVocab,
            OnnxMaxSequenceLength: onnxMaxSequenceLength,
            EmbeddingDimensions: embeddingDimensions);
        var result = await searchService.BuildAsync(runDirectory, options, cancellationToken).ConfigureAwait(false);
        return Serialize(new
        {
            runDirectory,
            backend = result.Backend,
            indexPath = result.IndexPath,
            manifest = result.Manifest,
            documentCount = result.DocumentCount,
            createdAt = result.CreatedAt
        });
    }

    [McpServerTool(Name = "index.query")]
    [Description("Queries a Zakira.Replay search index or run directory.")]
    public async Task<string> IndexQueryAsync(
        [Description("Run directory or search/index.json path.")] string target,
        [Description("Search query string.")] string query,
        [Description("Maximum matches.")] int top = 5,
        [Description("Search backend: auto (default), json, sqlite, sqlite-onnx.")] string? backend = null,
        [Description("Optional ONNX embedding model path.")] string? onnxModel = null,
        [Description("Optional WordPiece vocabulary path.")] string? onnxVocab = null,
        [Description("Optional ONNX tokenizer sequence length.")] int? onnxMaxSequenceLength = null,
        [Description("Optional embedding dimension hint.")] int? embeddingDimensions = null,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchIndexQueryOptions(
            Backend: backend ?? SearchBackends.Auto,
            OnnxModelPath: onnxModel,
            OnnxVocabularyPath: onnxVocab,
            OnnxMaxSequenceLength: onnxMaxSequenceLength,
            EmbeddingDimensions: embeddingDimensions);
        var result = await searchService.QueryAsync(target, query, top, options, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    // ---------------------------------------------------------------------------------------
    //  Chapters + alignment
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "chapters.build")]
    [Description("Builds deterministic transcript-based chapters for a completed run.")]
    public async Task<string> ChaptersBuildAsync(
        [Description("Completed Zakira.Replay run directory containing transcript evidence.")] string runDirectory,
        [Description("Minimum chapter duration in seconds.")] double minDuration = 60,
        [Description("Maximum chapter duration in seconds.")] double maxDuration = 600,
        CancellationToken cancellationToken = default)
    {
        var result = await chapterBuilder.BuildAsync(
            runDirectory,
            new ChapterBuildOptions(MinDurationSeconds: minDuration, MaxDurationSeconds: maxDuration),
            cancellationToken).ConfigureAwait(false);
        return Serialize(new
        {
            runDirectory,
            runId = result.RunId,
            chapterCount = result.ChapterCount,
            chaptersPath = result.JsonPath,
            markdownPath = result.MarkdownPath,
            document = result.Document
        });
    }

    [McpServerTool(Name = "align")]
    [Description("Builds cross-modal evidence alignment views (by-chapter and by-slide) over a completed run. Pure rearrangement; no model calls.")]
    public async Task<string> AlignAsync(
        [Description("Completed Zakira.Replay run directory containing evidence.json. chapters/chapters.json is optional but recommended for the by-chapter view.")] string runDirectory,
        CancellationToken cancellationToken = default)
    {
        var result = await alignmentService.BuildAsync(runDirectory, new EvidenceAlignmentOptions(), cancellationToken).ConfigureAwait(false);
        return Serialize(new
        {
            runDirectory,
            runId = result.RunId,
            byChapterPath = result.ByChapterPath,
            bySlidePath = result.BySlidePath,
            chapterCount = result.ByChapter.Chapters.Count,
            slideCount = result.BySlide.Slides.Count,
            chaptersLoaded = result.ChaptersLoaded
        });
    }

    // ---------------------------------------------------------------------------------------
    //  Discovery + diagnostics
    // ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "discover")]
    [Description("Discovers candidate video URLs from a web page.")]
    public async Task<string> DiscoverAsync(
        [Description("Page URL to inspect.")] string url,
        [Description("Use Edge/Playwright for dynamic page discovery.")] bool browser = false,
        CancellationToken cancellationToken = default)
    {
        var result = await discoveryService.DiscoverAsync(url, browser, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    [McpServerTool(Name = "doctor")]
    [Description("Reports Zakira.Replay dependency availability without installing anything.")]
    public string Doctor()
    {
        return Serialize(dependencyResolver.GetAllStatuses());
    }

    // ---------------------------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------------------------

    private static AnalyzeRequest BuildAnalyzeRequest(
        string source, string? visionInstruction, string? ocrInstruction, bool noTranscript, int frames, string? runId,
        bool audio, bool stt, bool ocr, bool vision, bool diarize, int? numSpeakers, double? diarizeThreshold,
        int maxAiFrames, string? llmProvider, string? model, string? ocrProvider, string? visionProvider, string? localVisionMode,
        bool? smartCrop, string? smartCropProfile, string? captureMode, string? authProfile, string? frameStrategy, bool everyFrame,
        string? cookies, string? cookiesFromBrowser, string? captionLanguages, bool? slideGrouping, int? slideHashDistance,
        int? framesPerMinute, int? sceneSafetyCap, bool cache, bool force)
    {
        return new AnalyzeRequest(
            Source: source,
            VisionInstruction: visionInstruction ?? string.Empty,
            OcrInstruction: ocrInstruction ?? string.Empty,
            IncludeTranscript: !noTranscript,
            FrameCount: frames,
            RunId: runId,
            ExtractAudio: audio || stt,
            UseSpeechToText: stt,
            UseOcr: ocr,
            UseVision: vision,
            MaxAiFrames: maxAiFrames,
            Model: model ?? LlmProviderFactory.GetDefaultModel(llmProvider),
            LlmProvider: LlmProviderFactory.Normalize(llmProvider),
            Force: force,
            UseCache: cache,
            FrameStrategy: everyFrame ? FrameSelectionStrategies.EveryFrame : (frameStrategy ?? FrameSelectionStrategies.Scene),
            CookiesPath: cookies,
            CookiesFromBrowser: cookiesFromBrowser,
            CaptionLanguages: ParseCaptionLanguages(captionLanguages),
            SlideGrouping: slideGrouping,
            SlideHashDistance: slideHashDistance,
            FramesPerMinute: framesPerMinute,
            SceneSafetyCap: sceneSafetyCap,
            OcrProvider: OcrProviderFactory.Normalize(ocrProvider),
            SmartCrop: smartCrop,
            SmartCropProfile: smartCropProfile,
            CaptureMode: captureMode,
            AuthProfile: authProfile,
            UseDiarization: diarize,
            NumSpeakers: numSpeakers,
            DiarizationThreshold: diarizeThreshold is null ? null : (float)diarizeThreshold.Value,
            VisionProvider: VisionProviderFactory.Normalize(visionProvider),
            LocalVisionMode: localVisionMode);
    }

    private static IReadOnlyList<string>? ParseCaptionLanguages(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var languages = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return languages.Length == 0 ? null : languages;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
