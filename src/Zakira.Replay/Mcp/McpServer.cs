using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

public sealed class McpServer
{
    private static readonly string ServerVersion = ReplayVersion.Current;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<AnalysisPipeline> pipelineFactory;
    private readonly McpJobManager jobManager;
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;

    public McpServer(Func<AnalysisPipeline> pipelineFactory, TextWriter stdout, TextWriter stderr)
    {
        this.pipelineFactory = pipelineFactory;
        jobManager = new McpJobManager(pipelineFactory);
        this.stdout = stdout;
        this.stderr = stderr;
    }

    public async Task RunAsync(TextReader stdin, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonRpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(null, -32700, $"Parse error: {ex.Message}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (request is null)
            {
                await WriteErrorAsync(null, -32600, "Invalid request.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await HandleAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (MissingDependencyException ex)
            {
                await WriteErrorAsync(request.Id, -32010, ex.ToDisplayString(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(request.Id, -32000, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "initialize":
                await WriteResultAsync(request.Id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "Zakira.Replay", version = ServerVersion }
                }, cancellationToken).ConfigureAwait(false);
                break;

            case "notifications/initialized":
                break;

            case "tools/list":
                await WriteResultAsync(request.Id, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "analyze_video",
                            description = "Compatibility wrapper: starts an analysis job and waits for completion. Prefer create_analysis_job for long videos.",
                            inputSchema = AnalysisInputSchema()
                        },
                        new
                        {
                            name = "create_analysis_job",
                            description = "Starts video analysis in the background and returns a job id.",
                            inputSchema = AnalysisInputSchema()
                        },
                        new
                        {
                            name = "get_job_status",
                            description = "Gets analysis job status and recent logs.",
                            inputSchema = JobIdInputSchema(includeLogs: true)
                        },
                        new
                        {
                            name = "get_job_result",
                            description = "Gets completed analysis job result, including artifact directory and manifest.",
                            inputSchema = JobIdInputSchema(includeLogs: false)
                        },
                        new
                        {
                            name = "cancel_job",
                            description = "Cancels a running analysis job.",
                            inputSchema = JobIdInputSchema(includeLogs: false)
                        },
                        new
                        {
                            name = "enqueue_analysis_queue_job",
                            description = "Adds an analysis request to a persistent local queue under runs/.queue.",
                            inputSchema = QueueEnqueueInputSchema()
                        },
                        new
                        {
                            name = "run_analysis_queue",
                            description = "Runs pending analysis queue jobs with local concurrency and retry limits.",
                            inputSchema = QueueRunInputSchema()
                        },
                        new
                        {
                            name = "get_analysis_queue_status",
                            description = "Returns persistent analysis queue state and job statuses.",
                            inputSchema = QueueStatusInputSchema()
                        },
                        new
                        {
                            name = "extract_clip",
                            description = "Extracts a timestamped video clip from a URL or local media file.",
                            inputSchema = ClipInputSchema()
                        },
                        new
                        {
                            name = "build_search_index",
                            description = "Builds a local search index over a completed run's evidence.json.",
                            inputSchema = SearchBuildInputSchema()
                        },
                        new
                        {
                            name = "query_search_index",
                            description = "Queries a Zakira.Replay search index or run directory.",
                            inputSchema = SearchQueryInputSchema()
                        },
                        new
                        {
                            name = "build_chapters",
                            description = "Builds deterministic transcript-based chapters for a completed run.",
                            inputSchema = ChaptersBuildInputSchema()
                        },
                        new
                        {
                            name = "build_evidence_alignment",
                            description = "Builds cross-modal evidence alignment views (by-chapter and by-slide) over a completed run. Pure rearrangement; no model calls.",
                            inputSchema = AlignmentBuildInputSchema()
                        },
                        new
                        {
                            name = "doctor",
                            description = "Reports Zakira.Replay dependency availability without installing anything.",
                            inputSchema = new { type = "object", properties = new { } }
                        },
                        new
                        {
                            name = "discover_videos",
                            description = "Discovers candidate video URLs from a web page.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    url = new { type = "string", description = "Page URL to inspect." },
                                    browser = new { type = "boolean", description = "Use Edge/Playwright for dynamic page discovery." }
                                },
                                required = new[] { "url" }
                            }
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
                break;

            case "tools/call":
                await HandleToolCallAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await WriteErrorAsync(request.Id, -32601, $"Method not found: {request.Method}", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleToolCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var name = request.Params?["name"]?.GetValue<string>();
        var args = request.Params?["arguments"]?.AsObject();

        switch (name)
        {
            case "doctor":
                var dependencies = new DependencyResolver();
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(dependencies.GetAllStatuses(), JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "discover_videos":
                if (args is null || !args.TryGetPropertyValue("url", out var urlNode) || urlNode is null)
                {
                    await WriteErrorAsync(request.Id, -32602, "discover_videos requires `url`.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var useBrowser = args.TryGetPropertyValue("browser", out var browserNode) && browserNode is not null && browserNode.GetValue<bool>();
                var discoveryDependencies = new DependencyResolver();
                var processRunner = new ProcessRunner();
                var discovery = new DiscoveryService(discoveryDependencies, processRunner);
                var discoveryResult = await discovery.DiscoverAsync(urlNode.GetValue<string>(), useBrowser, cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(discoveryResult, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "analyze_video":
                await HandleBlockingAnalyzeAsync(request, args, cancellationToken).ConfigureAwait(false);
                break;

            case "create_analysis_job":
                var analysisRequest = ParseAnalyzeRequest(args);
                var job = jobManager.Create(analysisRequest);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(job.Snapshot(includeLogs: false), JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "get_job_status":
                await HandleJobSnapshotAsync(request, args, includeLogs: true, requireCompleted: false, cancellationToken).ConfigureAwait(false);
                break;

            case "get_job_result":
                await HandleJobSnapshotAsync(request, args, includeLogs: true, requireCompleted: true, cancellationToken).ConfigureAwait(false);
                break;

            case "cancel_job":
                var cancelJobId = GetJobId(args);
                var cancelled = jobManager.Cancel(cancelJobId);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new { jobId = cancelJobId, cancelled }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "enqueue_analysis_queue_job":
                var queue = new AnalysisQueue(pipelineFactory);
                var queueRequest = ParseAnalyzeRequest(args);
                var enqueueResult = await queue.EnqueueAsync(
                    args is null ? null : GetString(args, "queueId") ?? GetString(args, "queue-id"),
                    queueRequest,
                    args is null ? null : GetString(args, "jobId") ?? GetString(args, "job-id"),
                    args is null ? 0 : GetInt(args, "retries", 0),
                    cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
                {
                    enqueueResult.QueueId,
                    enqueueResult.JobId,
                    enqueueResult.QueueDirectory,
                    enqueueResult.Job
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "run_analysis_queue":
                var runQueue = new AnalysisQueue(pipelineFactory);
                var queueRunResult = await runQueue.RunAsync(
                    args is null ? null : GetString(args, "queueId") ?? GetString(args, "queue-id"),
                    new AnalysisQueueRunOptions(
                        Concurrency: args is null ? 1 : GetInt(args, "concurrency", GetInt(args, "workers", 1)),
                        Retries: args is null ? 0 : GetInt(args, "retries", 0)),
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(queueRunResult, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "get_analysis_queue_status":
                var statusQueue = new AnalysisQueue(pipelineFactory);
                var queueStatus = await statusQueue.GetStatusAsync(args is null ? null : GetString(args, "queueId") ?? GetString(args, "queue-id"), cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(queueStatus, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "extract_clip":
                var clipService = CreateClipService();
                var clipResult = await clipService.ExtractAsync(ParseClipRequest(args), progress: null, cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
                {
                    runId = clipResult.Run.Id,
                    artifactDirectory = clipResult.Run.Directory,
                    clipPath = clipResult.Run.GetPath(clipResult.Manifest.ClipPath),
                    manifest = clipResult.Manifest
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "build_search_index":
                var runDirectory = GetRequiredString(args, "runDirectory", "build_search_index requires `runDirectory`.");
                var buildResult = await new SearchIndexService().BuildAsync(runDirectory, ParseSearchBuildOptions(args), cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
                {
                    runDirectory,
                    backend = buildResult.Backend,
                    indexPath = buildResult.IndexPath,
                    manifest = buildResult.Manifest,
                    documentCount = buildResult.DocumentCount,
                    createdAt = buildResult.CreatedAt
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "query_search_index":
                var indexPathOrRunDirectory = GetRequiredString(args, "target", "query_search_index requires `target`.");
                var query = GetRequiredString(args, "query", "query_search_index requires `query`.");
                var queryResult = await new SearchIndexService().QueryAsync(indexPathOrRunDirectory, query, args is null ? 5 : GetInt(args, "top", 5), ParseSearchQueryOptions(args), cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(queryResult, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "build_chapters":
                var chapterRunDirectory = GetRequiredString(args, "runDirectory", "build_chapters requires `runDirectory`.");
                var chapterResult = await new ChapterBuilder().BuildAsync(chapterRunDirectory, new ChapterBuildOptions(
                    MinDurationSeconds: args is null ? 60 : GetDouble(args, "minDuration", 60),
                    MaxDurationSeconds: args is null ? 600 : GetDouble(args, "maxDuration", 600)), cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
                {
                    runDirectory = chapterRunDirectory,
                    runId = chapterResult.RunId,
                    chapterCount = chapterResult.ChapterCount,
                    chaptersPath = chapterResult.JsonPath,
                    markdownPath = chapterResult.MarkdownPath,
                    document = chapterResult.Document
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            case "build_evidence_alignment":
                var alignmentRunDirectory = GetRequiredString(args, "runDirectory", "build_evidence_alignment requires `runDirectory`.");
                var alignmentResult = await new EvidenceAlignmentService().BuildAsync(alignmentRunDirectory, new EvidenceAlignmentOptions(), cancellationToken).ConfigureAwait(false);
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
                {
                    runDirectory = alignmentRunDirectory,
                    runId = alignmentResult.RunId,
                    byChapterPath = alignmentResult.ByChapterPath,
                    bySlidePath = alignmentResult.BySlidePath,
                    chapterCount = alignmentResult.ByChapter.Chapters.Count,
                    slideCount = alignmentResult.BySlide.Slides.Count,
                    chaptersLoaded = alignmentResult.ChaptersLoaded
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
                break;

            default:
                await WriteErrorAsync(request.Id, -32602, $"Unknown tool: {name}", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleBlockingAnalyzeAsync(JsonRpcRequest request, JsonObject? args, CancellationToken cancellationToken)
    {
        var job = jobManager.Create(ParseAnalyzeRequest(args));
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = job.Snapshot(includeLogs: true);
            if (snapshot.Status is "succeeded" or "failed" or "cancelled")
            {
                await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleJobSnapshotAsync(JsonRpcRequest request, JsonObject? args, bool includeLogs, bool requireCompleted, CancellationToken cancellationToken)
    {
        var jobId = GetJobId(args);
        var job = jobManager.Get(jobId);
        if (job is null)
        {
            await WriteErrorAsync(request.Id, -32602, $"Unknown jobId: {jobId}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var snapshot = job.Snapshot(includeLogs);
        if (requireCompleted && snapshot.Status is not ("succeeded" or "failed" or "cancelled"))
        {
            await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(new
            {
                snapshot.JobId,
                snapshot.Status,
                message = "Job is not complete yet.",
                snapshot.Logs
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteToolTextAsync(request.Id, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private static AnalyzeRequest ParseAnalyzeRequest(JsonObject? args)
    {
        if (args is null || !args.TryGetPropertyValue("source", out var sourceNode) || sourceNode is null)
        {
            throw new ReplayException("analyze_video requires `source`.");
        }

        var stt = GetBool(args, "stt");
        var llmProvider = LlmProviderFactory.Normalize(GetString(args, "llmProvider") ?? GetString(args, "provider") ?? GetString(args, "llm-provider"));
        var ocrProvider = OcrProviderFactory.Normalize(GetString(args, "ocrProvider") ?? GetString(args, "ocr-provider"));
        bool? slideGrouping = null;
        if (args.TryGetPropertyValue("slideGrouping", out var slideGroupingNode) && slideGroupingNode is not null)
        {
            slideGrouping = slideGroupingNode.GetValue<bool>();
        }

        bool? smartCrop = null;
        if (args.TryGetPropertyValue("smartCrop", out var smartCropNode) && smartCropNode is not null)
        {
            smartCrop = smartCropNode.GetValue<bool>();
        }
        var smartCropProfile = GetString(args, "smartCropProfile") ?? GetString(args, "smart-crop-profile") ?? GetString(args, "cropProfile");
        var captureMode = GetString(args, "captureMode") ?? GetString(args, "capture-mode") ?? GetString(args, "capture");
        var authProfile = GetString(args, "authProfile") ?? GetString(args, "auth-profile") ?? GetString(args, "auth");

        return new AnalyzeRequest(
            Source: sourceNode.GetValue<string>(),
            VisionInstruction: GetString(args, "visionInstruction") ?? string.Empty,
            OcrInstruction: GetString(args, "ocrInstruction") ?? string.Empty,
            IncludeTranscript: !GetBool(args, "noTranscript"),
            FrameCount: GetInt(args, "frames", 500),
            RunId: GetString(args, "runId"),
            ExtractAudio: GetBool(args, "audio") || stt,
            UseSpeechToText: stt,
            UseOcr: GetBool(args, "ocr"),
            UseVision: GetBool(args, "vision"),
            MaxAiFrames: GetInt(args, "maxAiFrames", 50),
            Model: GetString(args, "model") ?? LlmProviderFactory.GetDefaultModel(llmProvider),
            LlmProvider: llmProvider,
            Force: GetBool(args, "force"),
            UseCache: GetBool(args, "cache"),
            FrameStrategy: GetFrameStrategy(args),
            CookiesPath: GetString(args, "cookies"),
            CookiesFromBrowser: GetString(args, "browserAuth") ?? GetString(args, "cookiesFromBrowser"),
            CaptionLanguages: GetCaptionLanguages(args),
            SlideGrouping: slideGrouping,
            SlideHashDistance: GetOptionalInt(args, "slideHashDistance"),
            FramesPerMinute: GetOptionalInt(args, "framesPerMinute"),
            SceneSafetyCap: GetOptionalInt(args, "sceneSafetyCap"),
            OcrProvider: ocrProvider,
            SmartCrop: smartCrop,
            SmartCropProfile: smartCropProfile,
            CaptureMode: captureMode,
            AuthProfile: authProfile);
    }

    private static IReadOnlyList<string>? GetCaptionLanguages(JsonObject args)
    {
        if (!args.TryGetPropertyValue("captionLanguages", out var node) || node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            var languages = array
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return languages.Length == 0 ? null : languages;
        }

        var raw = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parsed.Length == 0 ? null : parsed;
    }

    private static string GetFrameStrategy(JsonObject args)
    {
        if (GetBool(args, "everyFrame") || GetBool(args, "frameByFrame"))
        {
            return FrameSelectionStrategies.EveryFrame;
        }

        return GetString(args, "frameStrategy") ?? FrameSelectionStrategies.Scene;
    }

    private static ClipExtractionRequest ParseClipRequest(JsonObject? args)
    {
        var source = GetRequiredString(args, "source", "extract_clip requires `source`.");
        var start = Timestamp.ParseRequired(GetRequiredString(args, "start", "extract_clip requires `start`."), "start");
        var end = Timestamp.ParseRequired(GetRequiredString(args, "end", "extract_clip requires `end`."), "end");
        return new ClipExtractionRequest(
            Source: source,
            Start: start,
            End: end,
            RunId: args is null ? null : GetString(args, "runId"),
            OutputName: args is null ? null : GetString(args, "outputName"),
            CookiesPath: args is null ? null : GetString(args, "cookies"),
            CookiesFromBrowser: args is null ? null : GetString(args, "browserAuth") ?? GetString(args, "cookiesFromBrowser"));
    }

    private static SearchIndexBuildOptions ParseSearchBuildOptions(JsonObject? args)
    {
        return new SearchIndexBuildOptions(
            Backend: args is null ? SearchBackends.Json : GetString(args, "backend") ?? SearchBackends.Json,
            OnnxModelPath: args is null ? null : GetString(args, "onnxModel") ?? GetString(args, "onnxModelPath"),
            OnnxVocabularyPath: args is null ? null : GetString(args, "onnxVocab") ?? GetString(args, "onnxVocabulary") ?? GetString(args, "onnxVocabularyPath"),
            OnnxMaxSequenceLength: args is null ? null : GetOptionalInt(args, "onnxMaxSequenceLength"),
            EmbeddingDimensions: args is null ? null : GetOptionalInt(args, "embeddingDimensions"));
    }

    private static SearchIndexQueryOptions ParseSearchQueryOptions(JsonObject? args)
    {
        return new SearchIndexQueryOptions(
            Backend: args is null ? SearchBackends.Auto : GetString(args, "backend") ?? SearchBackends.Auto,
            OnnxModelPath: args is null ? null : GetString(args, "onnxModel") ?? GetString(args, "onnxModelPath"),
            OnnxVocabularyPath: args is null ? null : GetString(args, "onnxVocab") ?? GetString(args, "onnxVocabulary") ?? GetString(args, "onnxVocabularyPath"),
            OnnxMaxSequenceLength: args is null ? null : GetOptionalInt(args, "onnxMaxSequenceLength"),
            EmbeddingDimensions: args is null ? null : GetOptionalInt(args, "embeddingDimensions"));
    }

    private static ClipExtractionService CreateClipService()
    {
        var dependencies = new DependencyResolver();
        var processRunner = new ProcessRunner();
        var artifactStore = new ArtifactStore(ArtifactStore.GetDefaultRootDirectory());
        return new ClipExtractionService(artifactStore, new YtDlpClient(dependencies, processRunner), new FfmpegClient(dependencies, processRunner));
    }

    private static string GetJobId(JsonObject? args)
    {
        if (args is null || !args.TryGetPropertyValue("jobId", out var jobIdNode) || jobIdNode is null)
        {
            throw new ReplayException("Tool requires `jobId`.");
        }

        return jobIdNode.GetValue<string>();
    }

    private static string GetRequiredString(JsonObject? args, string name, string message)
    {
        if (args is null || !args.TryGetPropertyValue(name, out var node) || node is null)
        {
            throw new ReplayException(message);
        }

        return node.GetValue<string>();
    }

    private Task WriteToolTextAsync(JsonNode? id, string text, CancellationToken cancellationToken)
    {
        return WriteResultAsync(id, new
        {
            content = new[] { new { type = "text", text } }
        }, cancellationToken);
    }

    private static bool GetBool(JsonObject args, string name)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null && node.GetValue<bool>();
    }

    private static int GetInt(JsonObject args, string name, int defaultValue)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<int>() : defaultValue;
    }

    private static int? GetOptionalInt(JsonObject args, string name)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<int>() : null;
    }

    private static double GetDouble(JsonObject args, string name, double defaultValue)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<double>() : defaultValue;
    }

    private static string? GetString(JsonObject args, string name)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<string>() : null;
    }

    private static object AnalysisInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                source = new { type = "string", description = "Video URL or local media path." },
                visionInstruction = new { type = "string", description = "Optional focus signal appended to the vision prompt. Defaults to empty (the model still extracts every visible piece of content)." },
                ocrInstruction = new { type = "string", description = "Optional focus signal appended to the OCR prompt. Defaults to empty (the model still extracts every readable piece of text)." },
                frames = new { type = "integer", description = "Number of representative frames to extract." },
                frameStrategy = new { type = "string", description = "Frame selection strategy: interval, scene, or every-frame." },
                everyFrame = new { type = "boolean", description = "Alias for frameStrategy=every-frame." },
                stt = new { type = "boolean", description = "Use the configured LLM provider to transcribe extracted audio if captions are missing." },
                audio = new { type = "boolean", description = "Extract audio with ffmpeg." },
                ocr = new { type = "boolean", description = "Use the configured LLM provider as OCR over selected frames." },
                vision = new { type = "boolean", description = "Use the configured LLM provider to describe selected frames." },
                maxAiFrames = new { type = "integer", description = "Maximum number of frames to send to AI providers." },
                llmProvider = new { type = "string", description = "LLM provider: github-copilot, openai, or azure-openai." },
                model = new { type = "string", description = "Provider model id. Defaults from provider config." },
                runId = new { type = "string", description = "Optional run ID for artifact folder naming." },
                cache = new { type = "boolean", description = "Reuse a previous run with matching source and analysis options." },
                cookies = new { type = "string", description = "Path to a Netscape cookies file for yt-dlp." },
                cookiesFromBrowser = new { type = "string", description = "Browser name/profile spec for yt-dlp --cookies-from-browser." },
                browserAuth = new { type = "string", description = "Alias for cookiesFromBrowser." },
                captionLanguages = new
                {
                    description = "Caption/subtitle language preferences for yt-dlp --sub-langs. Accepts an array of BCP-47-style codes (e.g. [\"fr\", \"en\"]) or a comma-separated string. Use \"auto\" to merge with the source's advertised languages plus English defaults.",
                    type = new[] { "array", "string", "null" },
                    items = new { type = "string" }
                },
                slideGrouping = new { type = "boolean", description = "Group perceptually-similar frames into slides before OCR/vision. Defaults to config (true). Set false to run OCR/vision per individual frame." },
                slideHashDistance = new { type = "integer", description = "Maximum Hamming distance (0-64) between adjacent perceptual hashes still considered the same slide. Defaults to config (6)." },
                framesPerMinute = new { type = "integer", description = "Optional duration-aware sampling rate for the interval strategy. When set, the effective frame count is max(framesPerMinute * durationMinutes, frames). Ignored for scene and every-frame strategies." },
                sceneSafetyCap = new { type = "integer", description = "Per-run override of frames.sceneSafetyCap (default 2000). Bounds the maximum number of scene-cut frames extracted. When the cap is reached, the run carries a FRAMES_SCENE_CAP_REACHED warning." },
                ocrProvider = new { type = "string", description = "OCR provider: 'copilot' (LLM vision-as-OCR, default) or 'local' (RapidOCR / PP-OCRv5 via ONNX). Install local models with `deps install ocr`." },
                smartCrop = new { type = "boolean", description = "Run smart-crop preprocessing on each frame before perceptual hashing, OCR, and vision. Removes Teams/Zoom/WebEx UI chrome (controls bar, participant gallery, black letterbox bars). Defaults to false." },
                smartCropProfile = new { type = "string", description = "Smart-crop profile: 'auto' (default), 'teams', 'zoom', 'webex', 'generic', or 'off'. All non-off profiles share the same algorithm in this release; the value is recorded on each FrameCropBox for audit." },
                captureMode = new { type = "string", description = "Frame-capture mode: 'ytdlp' (default; yt-dlp + ffmpeg), 'browser' (Playwright-driven Chromium for sites yt-dlp can't reach), or 'auto' (try yt-dlp, fall back to browser on failure with CAPTURE_BROWSER_FALLBACK)." },
                authProfile = new { type = "string", description = "Name of a Playwright storage-state profile (created by `zakira-replay auth login <name>`) to load into the browser context before navigating. Only used in browser/auto capture mode. Emits AUTH_PROFILE_NOT_FOUND when missing and AUTH_PROFILE_STALE when older than auth.staleThresholdMinutes (default 60)." },
                force = new { type = "boolean", description = "Recompute even if the run ID already has a completed manifest." }
            },
            required = new[] { "source" }
        };
    }

    private static object JobIdInputSchema(bool includeLogs)
    {
        return new
        {
            type = "object",
            properties = new
            {
                jobId = new { type = "string", description = "Analysis job id." },
                includeLogs = new { type = "boolean", description = includeLogs ? "Recent logs are included by default." : "Ignored." }
            },
            required = new[] { "jobId" }
        };
    }

    private static object QueueEnqueueInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                source = new { type = "string", description = "Video URL or local media path." },
                queueId = new { type = "string", description = "Persistent queue id. Defaults to default." },
                jobId = new { type = "string", description = "Optional stable job id. Defaults to a generated id." },
                retries = new { type = "integer", description = "Retry count beyond the first attempt." },
                visionInstruction = new { type = "string", description = "Optional focus signal appended to the vision prompt. Defaults to empty." },
                ocrInstruction = new { type = "string", description = "Optional focus signal appended to the OCR prompt. Defaults to empty." },
                frames = new { type = "integer", description = "Number of frames to extract." },
                frameStrategy = new { type = "string", description = "Frame selection strategy: interval, scene, or every-frame." },
                everyFrame = new { type = "boolean", description = "Alias for frameStrategy=every-frame." },
                stt = new { type = "boolean", description = "Use the configured LLM provider for missing captions." },
                audio = new { type = "boolean", description = "Extract audio with ffmpeg." },
                ocr = new { type = "boolean", description = "Run OCR over selected frames." },
                vision = new { type = "boolean", description = "Analyze selected frames visually." },
                maxAiFrames = new { type = "integer", description = "Maximum frames to send to AI providers." },
                llmProvider = new { type = "string", description = "LLM provider: github-copilot, openai, or azure-openai." },
                model = new { type = "string", description = "Provider model id." },
                runId = new { type = "string", description = "Optional run ID for artifact folder naming." },
                cache = new { type = "boolean", description = "Reuse a previous matching run." },
                cookies = new { type = "string", description = "Path to a Netscape cookies file for yt-dlp." },
                cookiesFromBrowser = new { type = "string", description = "Browser name/profile spec for yt-dlp --cookies-from-browser." },
                browserAuth = new { type = "string", description = "Alias for cookiesFromBrowser." },
                captionLanguages = new
                {
                    description = "Caption/subtitle language preferences for yt-dlp --sub-langs. Accepts an array of BCP-47-style codes (e.g. [\"fr\", \"en\"]) or a comma-separated string. Use \"auto\" to merge with the source's advertised languages plus English defaults.",
                    type = new[] { "array", "string", "null" },
                    items = new { type = "string" }
                },
                slideGrouping = new { type = "boolean", description = "Group perceptually-similar frames into slides before OCR/vision. Defaults to config (true)." },
                slideHashDistance = new { type = "integer", description = "Maximum Hamming distance (0-64) between adjacent perceptual hashes still considered the same slide. Defaults to config (6)." },
                framesPerMinute = new { type = "integer", description = "Optional duration-aware sampling rate for the interval strategy." },
                sceneSafetyCap = new { type = "integer", description = "Per-run override of frames.sceneSafetyCap (default 2000)." },
                ocrProvider = new { type = "string", description = "OCR provider: 'copilot' (LLM vision-as-OCR, default) or 'local' (RapidOCR via ONNX, requires `deps install ocr`)." },
                smartCrop = new { type = "boolean", description = "Run smart-crop preprocessing on every frame before perceptual hashing, OCR, and vision (Teams/Zoom/WebEx UI chrome removal)." },
                smartCropProfile = new { type = "string", description = "Smart-crop profile: auto|teams|zoom|webex|generic|off." },
                captureMode = new { type = "string", description = "Frame-capture mode: ytdlp|browser|auto." },
                authProfile = new { type = "string", description = "Playwright storage-state profile name (created by `auth login`). Only consulted in browser/auto capture mode." },
                force = new { type = "boolean", description = "Recompute existing run id." }
            },
            required = new[] { "source" }
        };
    }

    private static object QueueRunInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                queueId = new { type = "string", description = "Persistent queue id. Defaults to default." },
                concurrency = new { type = "integer", description = "Number of queue jobs to run concurrently. Defaults to 1." },
                workers = new { type = "integer", description = "Alias for concurrency." },
                retries = new { type = "integer", description = "Retry count beyond the first attempt." }
            }
        };
    }

    private static object QueueStatusInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                queueId = new { type = "string", description = "Persistent queue id. Defaults to default." }
            }
        };
    }

    private static object ClipInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                source = new { type = "string", description = "Video URL or local media path." },
                start = new { type = "string", description = "Clip start timestamp: seconds, MM:SS, or HH:MM:SS." },
                end = new { type = "string", description = "Clip end timestamp: seconds, MM:SS, or HH:MM:SS." },
                runId = new { type = "string", description = "Optional run ID for artifact folder naming." },
                outputName = new { type = "string", description = "Optional output file name." },
                cookies = new { type = "string", description = "Path to a Netscape cookies file for yt-dlp." },
                cookiesFromBrowser = new { type = "string", description = "Browser name/profile spec for yt-dlp --cookies-from-browser." },
                browserAuth = new { type = "string", description = "Alias for cookiesFromBrowser." }
            },
            required = new[] { "source", "start", "end" }
        };
    }

    private static object SearchBuildInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                runDirectory = new { type = "string", description = "Completed Zakira.Replay run directory containing evidence.json." },
                backend = new { type = "string", description = "Search backend: json, sqlite, or sqlite-onnx. Defaults to json." },
                onnxModel = new { type = "string", description = "Optional ONNX embedding model path for sqlite-onnx." },
                onnxVocab = new { type = "string", description = "Optional WordPiece vocabulary path for sqlite-onnx." },
                onnxMaxSequenceLength = new { type = "integer", description = "Optional ONNX tokenizer sequence length." },
                embeddingDimensions = new { type = "integer", description = "Optional embedding dimension hint." }
            },
            required = new[] { "runDirectory" }
        };
    }

    private static object SearchQueryInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                target = new { type = "string", description = "Run directory or search/index.json path." },
                query = new { type = "string", description = "Search query." },
                top = new { type = "integer", description = "Maximum number of matches to return." },
                backend = new { type = "string", description = "Search backend: auto, json, sqlite, or sqlite-onnx. Defaults to auto." },
                onnxModel = new { type = "string", description = "Optional ONNX embedding model path for sqlite-onnx queries." },
                onnxVocab = new { type = "string", description = "Optional WordPiece vocabulary path for sqlite-onnx queries." },
                onnxMaxSequenceLength = new { type = "integer", description = "Optional ONNX tokenizer sequence length." },
                embeddingDimensions = new { type = "integer", description = "Optional embedding dimension hint." }
            },
            required = new[] { "target", "query" }
        };
    }

    private static object ChaptersBuildInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                runDirectory = new { type = "string", description = "Completed Zakira.Replay run directory containing transcript evidence." },
                minDuration = new { type = "number", description = "Minimum chapter duration in seconds. Defaults to 60." },
                maxDuration = new { type = "number", description = "Maximum chapter duration in seconds. Defaults to 600." }
            },
            required = new[] { "runDirectory" }
        };
    }

    private static object AlignmentBuildInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                runDirectory = new { type = "string", description = "Completed Zakira.Replay run directory containing evidence.json. chapters/chapters.json is optional but recommended for the by-chapter view." }
            },
            required = new[] { "runDirectory" }
        };
    }

    private async Task WriteResultAsync(JsonNode? id, object result, CancellationToken cancellationToken)
    {
        var response = new JsonRpcResponse("2.0", id, result, null);
        await WriteJsonLineAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken)
    {
        var response = new JsonRpcResponse("2.0", id, null, new JsonRpcError(code, message));
        await WriteJsonLineAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync<T>(T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await stdout.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string? JsonRpc,
    [property: JsonPropertyName("id")] JsonNode? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonObject? Params);

internal sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonNode? Id,
    [property: JsonPropertyName("result")] object? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);
