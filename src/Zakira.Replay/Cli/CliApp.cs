using System.Text.Json;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Cli;

public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp(stdout);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        return command switch
        {
            "version" or "--version" or "-v" => RunVersion(stdout),
            "info" => await RunInfoAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "doctor" => await RunDoctorAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "analyze" => await RunAnalyzeAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "transcribe" => await RunTranscribeAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "frames" => await RunFramesAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "clip" => await RunClipAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "search" => await RunSearchAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "chapters" => await RunChaptersAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "align" => await RunAlignAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "discover" => await RunDiscoverAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "batch" => await RunBatchAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "queue" => await RunQueueAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "llm" => await RunLlmAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "deps" or "dependencies" => await RunDepsAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "auth" => await RunAuthAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "config" => await RunConfigAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "vision" => await RunVisionAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "mcp" => await RunMcpAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand(command, stderr)
        };
    }

    private static int RunVersion(TextWriter stdout)
    {
        stdout.WriteLine(AppInfo.Version);
        return 0;
    }

    private static async Task<int> RunInfoAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = CommandOptions.Parse(args);
        var configStore = new ConfigStore();
        var config = await configStore.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
        var installer = new PortableDependencyInstaller(config);
        var whisperOptions = SafeResolveWhisperOptions(config);
        var ocrPaths = SafeResolveOcrPaths(config);
        var dependencies = new DependencyResolver(config);
        var dependencyStatuses = dependencies.GetAllStatuses().ToDictionary(s => s.Name, s => s);

        var resolvedDependencies = new ReplayInfoDependencies(
            PortableDirectory: installer.Layout.PortableDirectory,
            OcrModelDirectory: installer.Layout.OcrModelDirectory,
            OcrLanguagePack: ocrPaths?.LanguagePack ?? config.Ocr.Local.LanguagePack ?? OcrLanguagePacks.Latin,
            OnnxModelDirectory: installer.Layout.OnnxModelDirectory,
            WhisperModelDirectory: installer.Layout.WhisperModelDirectory,
            WhisperModelPath: whisperOptions?.ModelPath,
            WhisperModelSize: config.Llm.LocalWhisper.ModelSize,
            DiarizationModelDirectory: installer.Layout.DiarizationModelDirectory,
            OllamaEndpoint: config.Llm.Ollama.Endpoint,
            OllamaModel: config.Llm.Ollama.Model,
            OllamaVisionModel: config.Llm.Ollama.VisionModel);

        var capabilities = new ReplayInfoCapabilities(
            LocalOcrReady: ocrPaths is not null && ocrPaths.MissingFiles().Count == 0,
            LocalWhisperReady: whisperOptions is not null && !string.IsNullOrWhiteSpace(whisperOptions.ModelPath) && File.Exists(whisperOptions.ModelPath),
            DiarizationReady: TryCheckDiarizationReady(config),
            YtDlpAvailable: dependencyStatuses.TryGetValue("yt-dlp", out var ytdlp) && ytdlp.IsFound,
            FfmpegAvailable: dependencyStatuses.TryGetValue("ffmpeg", out var ffmpeg) && ffmpeg.IsFound);

        var info = new ReplayInfo(
            Name: AppInfo.Name,
            Version: AppInfo.Version,
            ConfigPath: configStore.ConfigPath,
            RunsDirectory: ArtifactStore.GetDefaultRootDirectory(),
            LlmProvider: LlmProviderFactory.GetConfiguredProvider(config),
            DefaultModel: LlmProviderFactory.GetDefaultModel(null, config),
            Schemas:
            [
                "request.schema.json",
                "manifest.schema.json",
                "evidence.schema.json",
                "transcript-normalization.schema.json",
                "chapters.schema.json",
                "clip.schema.json",
                "frame-capture.schema.json",
                "search-index.schema.json",
                "batch.schema.json",
                "batch-result.schema.json",
                "queue.schema.json",
                "queue-run-result.schema.json",
                "audio-chunks.schema.json",
                "slides.schema.json",
                "ocr.schema.json",
                "vision.schema.json",
                "evidence-aligned.schema.json",
                "captions-discovered.schema.json"
            ],
            ResolvedDependencies: resolvedDependencies,
            Capabilities: capabilities);

        if (parsed.GetBool("json", defaultValue: false))
        {
            stdout.WriteLine(JsonSerializer.Serialize(info, CliJson.Options));
            return 0;
        }

        stdout.WriteLine($"{info.Name} {info.Version}");
        stdout.WriteLine($"Config: {info.ConfigPath}");
        stdout.WriteLine($"Runs: {info.RunsDirectory}");
        stdout.WriteLine($"LLM provider: {info.LlmProvider}");
        stdout.WriteLine($"Default model: {info.DefaultModel}");
        stdout.WriteLine($"OCR pack: {resolvedDependencies.OcrLanguagePack}");
        stdout.WriteLine($"Capabilities: local-ocr={capabilities.LocalOcrReady}, local-whisper={capabilities.LocalWhisperReady}, diarization={capabilities.DiarizationReady}, yt-dlp={capabilities.YtDlpAvailable}, ffmpeg={capabilities.FfmpegAvailable}");
        stdout.WriteLine("Schemas: " + string.Join(", ", info.Schemas));
        return 0;
    }

    private static LocalWhisperOptions? SafeResolveWhisperOptions(ReplayConfig config)
    {
        try
        {
            return LocalWhisperOptions.Resolve(config);
        }
        catch
        {
            return null;
        }
    }

    private static LocalOcrModelPaths? SafeResolveOcrPaths(ReplayConfig config)
    {
        try
        {
            return LocalOcrModelPaths.Resolve(config);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCheckDiarizationReady(ReplayConfig config)
    {
        try
        {
            var options = DiarizationOptions.Resolve(config);
            return options.MissingFiles().Count == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunDoctorAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = CommandOptions.Parse(args);
        var dependencies = new DependencyResolver();
        var runner = new ProcessRunner();
        var reports = new List<DoctorDependencyReport>();
        foreach (var status in dependencies.GetAllStatuses())
        {
            string? runnable = null;
            if (status.IsFound)
            {
                runnable = await TryCheckRunnableAsync(status, runner, cancellationToken).ConfigureAwait(false);
            }

            reports.Add(new DoctorDependencyReport(status.Name, status.IsFound, string.IsNullOrWhiteSpace(runnable), status.Path, status.Source, status.Message, runnable));
        }

        var whisperReport = BuildWhisperDoctorReport();
        reports.Add(whisperReport);

        var ollamaReport = await BuildOllamaDoctorReportAsync(cancellationToken).ConfigureAwait(false);
        reports.Add(ollamaReport);

        var diarizationReport = BuildDiarizationDoctorReport();
        reports.Add(diarizationReport);

        var ocrReport = BuildOcrDoctorReport();
        reports.Add(ocrReport);

        var visionReport = BuildVisionDoctorReport();
        reports.Add(visionReport);

        if (parsed.GetBool("json", defaultValue: false))
        {
            stdout.WriteLine(JsonSerializer.Serialize(new DoctorReport(DateTimeOffset.UtcNow, reports), CliJson.Options));
            return 0;
        }

        foreach (var report in reports)
        {
            if (report.IsFound)
            {
                var source = string.IsNullOrWhiteSpace(report.Source) ? string.Empty : $" via {report.Source}";
                stdout.WriteLine(report.IsRunnable
                    ? $"{report.Name}: found{source} ({report.Path})"
                    : $"{report.Name}: found but not runnable{source} ({report.Path}) - {report.RunnableError}");
            }
            else
            {
                stdout.WriteLine($"{report.Name}: missing ({report.Message})");
            }
        }

        return 0;
    }

    /// <summary>
    /// Probe the configured Ollama daemon with a 2-second HEAD-ish request against
    /// <c>/api/tags</c> (cheap; daemon answers immediately when up). Synthesises a
    /// <c>DoctorDependencyReport</c> so users get a clear "Ollama daemon running" or "Ollama
    /// daemon unreachable" signal alongside the other dependencies.
    /// </summary>
    private static DoctorDependencyReport BuildWhisperDoctorReport()
    {
        try
        {
            var config = new ConfigStore().Load();
            var options = LocalWhisperOptions.Resolve(config);
            var modelPath = options.ModelPath ?? string.Empty;
            var exists = !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);
            var message = exists
                ? $"language={options.Language}, threads={(options.Threads is null ? "auto" : options.Threads.ToString())}"
                : $"Run `zakira-replay deps install whisper-model {LocalWhisperOptions.DefaultModelSize}` to download.";
            return new DoctorDependencyReport(
                Name: "whisper-model",
                IsFound: exists,
                IsRunnable: exists,
                Path: string.IsNullOrWhiteSpace(modelPath) ? null : modelPath,
                Source: "config",
                Message: message,
                RunnableError: exists ? null : "model file not found");
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "whisper-model",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "config",
                Message: ex.Message,
                RunnableError: ex.Message);
        }
    }

    /// <summary>
    /// Inspect the resolved local OCR models and return a synthetic
    /// <see cref="DoctorDependencyReport"/> showing the configured language pack and the
    /// missing-file list (if any). Mirrors the Whisper / diarization probes.
    /// </summary>
    private static DoctorDependencyReport BuildOcrDoctorReport()
    {
        try
        {
            var config = new ConfigStore().Load();
            var paths = LocalOcrModelPaths.Resolve(config);
            var missing = paths.MissingFiles();
            var ok = missing.Count == 0;
            var message = ok
                ? $"pack={paths.LanguagePack}, dir={Path.GetDirectoryName(paths.RecognitionPath)}"
                : $"pack={paths.LanguagePack} (run `zakira-replay deps install ocr --language {paths.LanguagePack}` to download)";
            return new DoctorDependencyReport(
                Name: "ocr-models",
                IsFound: ok,
                IsRunnable: ok,
                Path: paths.RecognitionPath,
                Source: "config",
                Message: message,
                RunnableError: ok ? null : $"missing: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "ocr-models",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "config",
                Message: ex.Message,
                RunnableError: ex.Message);
        }
    }

    /// <summary>
    /// Inspect the resolved local-vision model files and return a synthetic
    /// <see cref="DoctorDependencyReport"/> so <c>doctor</c> exposes whether the
    /// <c>--vision-provider local --local-vision-mode clip</c> path is wired up.
    /// Heuristic mode always reports as ready; clip / clip-blip modes report missing
    /// files explicitly so users see exactly what to fetch.
    /// </summary>
    private static DoctorDependencyReport BuildVisionDoctorReport()
    {
        try
        {
            var config = new ConfigStore().Load();
            var options = LocalVisionOptions.Resolve(config);

            if (options.Mode == LocalVisionMode.Heuristic)
            {
                return new DoctorDependencyReport(
                    Name: "vision-models",
                    IsFound: true,
                    IsRunnable: true,
                    Path: options.ModelDirectory,
                    Source: "config",
                    Message: "mode=heuristic (no models required)",
                    RunnableError: null);
            }

            var missing = options.MissingFilesFor(options.Mode);
            var ok = missing.Count == 0;
            var modeLabel = VisionProviderFactory.FormatMode(options.Mode);
            var message = ok
                ? $"mode={modeLabel}, dir={options.ModelDirectory}"
                : $"mode={modeLabel} (run `zakira-replay deps install vision --mode {modeLabel}` to download)";
            return new DoctorDependencyReport(
                Name: "vision-models",
                IsFound: ok,
                IsRunnable: ok,
                Path: options.ClipImageEncoderPath,
                Source: "config",
                Message: message,
                RunnableError: ok ? null : $"missing: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "vision-models",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "config",
                Message: ex.Message,
                RunnableError: ex.Message);
        }
    }

    /// <summary>
    /// Inspect the resolved diarization models and return a synthetic
    /// <see cref="DoctorDependencyReport"/> so <c>doctor</c> exposes whether the
    /// <c>--diarize</c> path is wired up.
    /// </summary>
    private static DoctorDependencyReport BuildDiarizationDoctorReport()
    {
        try
        {
            var config = new ConfigStore().Load();
            var options = DiarizationOptions.Resolve(config);
            var missing = options.MissingFiles();
            var ok = missing.Count == 0;
            var message = ok
                ? $"provider={config.Diarization.Provider}, numSpeakers={(config.Diarization.NumSpeakers?.ToString() ?? "auto")}, threshold={(config.Diarization.Threshold?.ToString("0.00") ?? "0.50")}"
                : "Run `zakira-replay deps install diarization` to download.";
            return new DoctorDependencyReport(
                Name: "diarization-models",
                IsFound: ok,
                IsRunnable: ok,
                Path: options.SegmentationModelPath,
                Source: "config",
                Message: message,
                RunnableError: ok ? null : $"missing: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "diarization-models",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "config",
                Message: ex.Message,
                RunnableError: ex.Message);
        }
    }

    /// <summary>
    /// Probe the configured Ollama daemon with a 2-second HEAD-ish request against
    /// <c>/api/tags</c> (cheap; daemon answers immediately when up). Synthesises a
    /// <c>DoctorDependencyReport</c> so users get a clear "Ollama daemon running" or "Ollama
    /// daemon unreachable" signal alongside the other dependencies.
    /// </summary>
    private static async Task<DoctorDependencyReport> BuildOllamaDoctorReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = new ConfigStore().Load();
            var endpointValue = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OLLAMA_ENDPOINT")
                ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
                ?? config.Llm.Ollama.Endpoint
                ?? OllamaLlmProvider.DefaultEndpoint;

            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
            {
                return new DoctorDependencyReport(
                    Name: "ollama",
                    IsFound: false,
                    IsRunnable: false,
                    Path: endpointValue,
                    Source: "config",
                    Message: $"Invalid Ollama endpoint: '{endpointValue}'",
                    RunnableError: "invalid endpoint");
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri(endpoint, "/api/tags"), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var configured = string.IsNullOrWhiteSpace(config.Llm.Ollama.Model)
                    ? OllamaLlmProvider.DefaultChatModel
                    : config.Llm.Ollama.Model;
                var vision = string.IsNullOrWhiteSpace(config.Llm.Ollama.VisionModel)
                    ? "<none>"
                    : config.Llm.Ollama.VisionModel;
                return new DoctorDependencyReport(
                    Name: "ollama",
                    IsFound: true,
                    IsRunnable: true,
                    Path: endpoint.ToString(),
                    Source: "daemon",
                    Message: $"model={configured}, visionModel={vision}",
                    RunnableError: null);
            }

            return new DoctorDependencyReport(
                Name: "ollama",
                IsFound: false,
                IsRunnable: false,
                Path: endpoint.ToString(),
                Source: "daemon",
                Message: $"daemon responded {(int)response.StatusCode}",
                RunnableError: response.ReasonPhrase);
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "ollama",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "daemon",
                Message: $"daemon unreachable: {ex.Message}",
                RunnableError: "daemon unreachable");
        }
    }

    private static async Task<string?> TryCheckRunnableAsync(DependencyStatus status, ProcessRunner runner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.Path) || status.Name is not ("yt-dlp" or "ffmpeg" or "ffprobe"))
        {
            return null;
        }

        var args = status.Name == "yt-dlp" ? new[] { "--version" } : new[] { "-version" };
        try
        {
            var result = await runner.RunAsync(status.Path, args, timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? null : FirstLine(result.StandardError + result.StandardOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static string FirstLine(string value)
    {
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
    }

    private static Task<int> RunAnalyzeAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay analyze <url-or-file> [--vision-instruction <text>] [--ocr-instruction <text>] [--frames <count>] [--run-id <id>]");
        var frames = parsed.GetInt("frames", 500);
        var runId = parsed.Get("run-id");
        var request = CreateAnalyzeRequest(parsed, source, includeTranscript: true, frameCount: frames, runId);

        return RunPipelineAsync(request, stdout, cancellationToken);
    }

    private static Task<int> RunTranscribeAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay transcribe <url-or-file> [--run-id <id>]");
        var runId = parsed.Get("run-id");
        var request = CreateAnalyzeRequest(parsed, source, includeTranscript: true, frameCount: 0, runId) with
        {
            ExtractAudio = parsed.GetBool("stt", defaultValue: false) || parsed.GetBool("audio", defaultValue: false),
            UseSpeechToText = parsed.GetBool("stt", defaultValue: false)
        };

        return RunPipelineAsync(request, stdout, cancellationToken);
    }

    private static async Task<int> RunFramesAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay frames <url-or-file> [--at <timestamps>] [--from <ts> --to <ts> [--count <n>] [--strategy interval|scene]] [--count <count>] [--run-id <id>]");

        var atRaw = parsed.Get("at") ?? parsed.Get("timestamps");
        var fromRaw = parsed.Get("from");
        var toRaw = parsed.Get("to");
        var hasAdHocFlags = !string.IsNullOrWhiteSpace(atRaw) || !string.IsNullOrWhiteSpace(fromRaw) || !string.IsNullOrWhiteSpace(toRaw);

        if (!hasAdHocFlags)
        {
            // Backward-compatible "frames-only" full analyze: equivalent to `analyze --no-transcript`.
            var count = parsed.GetInt("count", parsed.GetInt("frames", 500));
            var runId = parsed.Get("run-id");
            var request = CreateAnalyzeRequest(parsed, source, includeTranscript: false, frameCount: count, runId);
            return await RunPipelineAsync(request, stdout, cancellationToken).ConfigureAwait(false);
        }

        var captureRequest = BuildFrameCaptureRequest(parsed, source, atRaw, fromRaw, toRaw);
        var service = CreateFrameCaptureService();
        var progress = new Progress<string>(message => stdout.WriteLine(message));
        var result = await service.CaptureAsync(captureRequest, progress, cancellationToken).ConfigureAwait(false);

        if (parsed.GetBool("json", defaultValue: false))
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                runId = result.Run.Id,
                artifactDirectory = result.Run.Directory,
                manifestPath = result.Run.GetPath("frame-capture.json"),
                frameCount = result.Manifest.Frames.Count,
                frames = result.Manifest.Frames.Select(frame => new
                {
                    id = frame.Id,
                    path = result.Run.GetPath(frame.Path),
                    relativePath = frame.Path,
                    timestampSeconds = frame.TimestampSeconds,
                    timestampLabel = frame.TimestampLabel,
                    perceptualHash = frame.PerceptualHash
                }),
                warnings = result.Manifest.Warnings
            }, CliJson.Options));
            return 0;
        }

        stdout.WriteLine();
        stdout.WriteLine($"Captured {result.Manifest.Frames.Count} frame(s) into {result.Run.Directory}.");
        foreach (var frame in result.Manifest.Frames)
        {
            stdout.WriteLine($"  {frame.TimestampLabel}  {result.Run.GetPath(frame.Path)}");
        }

        foreach (var warning in result.Manifest.Warnings)
        {
            stdout.WriteLine($"[{warning.Severity}] {warning.Code}: {warning.Message}");
        }

        return 0;
    }

    private static FrameCaptureRequest BuildFrameCaptureRequest(CommandOptions parsed, string source, string? atRaw, string? fromRaw, string? toRaw)
    {
        var hasAt = !string.IsNullOrWhiteSpace(atRaw);
        var hasFromOrTo = !string.IsNullOrWhiteSpace(fromRaw) || !string.IsNullOrWhiteSpace(toRaw);
        if (hasAt && hasFromOrTo)
        {
            throw new ReplayException("`--at` and `--from`/`--to` are mutually exclusive.");
        }

        IReadOnlyList<TimeSpan>? timestamps = null;
        TimeSpan? rangeStart = null;
        TimeSpan? rangeEnd = null;

        if (hasAt)
        {
            timestamps = FrameCaptureInput.ParseTimestamps(atRaw!, "at");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw))
            {
                throw new ReplayException("Window capture requires both `--from` and `--to`.");
            }

            rangeStart = Timestamp.ParseRequired(fromRaw!, "from");
            rangeEnd = Timestamp.ParseRequired(toRaw!, "to");
        }

        var strategy = parsed.Get("strategy") ?? parsed.Get("frame-strategy") ?? FrameSelectionStrategies.Interval;
        var maxEdge = parsed.GetOptionalInt("max-edge") ?? parsed.GetOptionalInt("max-long-edge-pixels");
        var quality = parsed.GetOptionalInt("quality") ?? parsed.GetOptionalInt("jpeg-quality");
        var phash = parsed.GetBool("phash", defaultValue: false) || parsed.GetBool("perceptual-hash", defaultValue: false);
        var browserAuth = parsed.Get("browser-auth");

        return new FrameCaptureRequest(
            Source: source,
            Timestamps: timestamps,
            RangeStart: rangeStart,
            RangeEnd: rangeEnd,
            RangeCount: parsed.GetOptionalInt("count") ?? parsed.GetOptionalInt("frames"),
            RangeStrategy: strategy,
            RunId: parsed.Get("run-id"),
            MaxLongEdgePixels: maxEdge,
            JpegQuality: quality,
            ComputePerceptualHash: phash,
            SceneSafetyCap: parsed.GetOptionalInt("scene-safety-cap"),
            CookiesPath: parsed.Get("cookies"),
            CookiesFromBrowser: browserAuth ?? parsed.Get("cookies-from-browser"));
    }

    private static async Task<int> RunClipAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp> [--run-id <id>] [--output-name <name>]");
        var start = Timestamp.ParseRequired(GetRequiredOption(parsed, "start", "Usage: zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp>"), "start");
        var end = Timestamp.ParseRequired(GetRequiredOption(parsed, "end", "Usage: zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp>"), "end");
        var browserAuth = parsed.Get("browser-auth");
        var service = CreateClipService();
        var progress = new Progress<string>(message => stdout.WriteLine(message));
        var result = await service.ExtractAsync(new ClipExtractionRequest(
            Source: source,
            Start: start,
            End: end,
            RunId: parsed.Get("run-id"),
            OutputName: parsed.Get("output-name") ?? parsed.Get("name"),
            CookiesPath: parsed.Get("cookies"),
            CookiesFromBrowser: browserAuth ?? parsed.Get("cookies-from-browser")), progress, cancellationToken).ConfigureAwait(false);

        stdout.WriteLine();
        stdout.WriteLine($"Completed clip: {result.Run.Id}");
        stdout.WriteLine($"Artifacts: {result.Run.Directory}");
        stdout.WriteLine($"Clip: {result.Run.GetPath(result.Manifest.ClipPath)}");
        return 0;
    }

    private static async Task<int> RunSearchAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay search <build|query> ...");
        }

        var command = args[0].ToLowerInvariant();
        var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
        var service = new SearchIndexService();
        switch (command)
        {
            case "build":
                var runDirectory = parsed.GetRequiredPositional("run-directory", "Usage: zakira-replay search build <run-directory> [--backend json|sqlite|sqlite-onnx]");
                var buildResult = await service.BuildAsync(Path.GetFullPath(runDirectory), CreateSearchBuildOptions(parsed), cancellationToken).ConfigureAwait(false);
                stdout.WriteLine($"Indexed {buildResult.DocumentCount} document(s) with {buildResult.Backend} backend.");
                stdout.WriteLine($"Index: {buildResult.IndexPath}");
                return 0;

            case "query":
                var target = parsed.GetRequiredPositional("run-directory-or-index", "Usage: zakira-replay search query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx]");
                var query = parsed.Get("query") ?? parsed.GetRequiredPositional(1, "query", "Usage: zakira-replay search query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx]");
                var result = await service.QueryAsync(Path.GetFullPath(target), query, parsed.GetInt("top", 5), CreateSearchQueryOptions(parsed), cancellationToken).ConfigureAwait(false);
                stdout.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
                return 0;

            default:
                throw new ReplayException("Usage: zakira-replay search <build|query> ...");
        }
    }

    private static SearchIndexBuildOptions CreateSearchBuildOptions(CommandOptions parsed)
    {
        return new SearchIndexBuildOptions(
            Backend: parsed.Get("backend") ?? SearchBackends.Json,
            OnnxModelPath: parsed.Get("onnx-model") ?? parsed.Get("model-path"),
            OnnxVocabularyPath: parsed.Get("onnx-vocab") ?? parsed.Get("onnx-vocabulary") ?? parsed.Get("vocab-path"),
            OnnxMaxSequenceLength: parsed.GetOptionalInt("onnx-max-sequence-length") ?? parsed.GetOptionalInt("max-sequence-length"),
            EmbeddingDimensions: parsed.GetOptionalInt("embedding-dimensions"));
    }

    private static SearchIndexQueryOptions CreateSearchQueryOptions(CommandOptions parsed)
    {
        return new SearchIndexQueryOptions(
            Backend: parsed.Get("backend") ?? SearchBackends.Auto,
            OnnxModelPath: parsed.Get("onnx-model") ?? parsed.Get("model-path"),
            OnnxVocabularyPath: parsed.Get("onnx-vocab") ?? parsed.Get("onnx-vocabulary") ?? parsed.Get("vocab-path"),
            OnnxMaxSequenceLength: parsed.GetOptionalInt("onnx-max-sequence-length") ?? parsed.GetOptionalInt("max-sequence-length"),
            EmbeddingDimensions: parsed.GetOptionalInt("embedding-dimensions"));
    }

    private static async Task<int> RunChaptersAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplayException("Usage: zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]");
        }

        var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
        var runDirectory = parsed.GetRequiredPositional("run-directory", "Usage: zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]");
        var result = await new ChapterBuilder().BuildAsync(Path.GetFullPath(runDirectory), new ChapterBuildOptions(
            MinDurationSeconds: parsed.GetDouble("min-duration", 60),
            MaxDurationSeconds: parsed.GetDouble("max-duration", 600)), cancellationToken).ConfigureAwait(false);

        stdout.WriteLine($"Built {result.ChapterCount} chapter(s).");
        stdout.WriteLine($"Chapters: {result.JsonPath}");
        stdout.WriteLine($"Markdown: {result.MarkdownPath}");
        return 0;
    }

    private static async Task<int> RunAlignAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var runDirectory = parsed.GetRequiredPositional("run-directory", "Usage: zakira-replay align <run-directory>");
        var result = await new EvidenceAlignmentService().BuildAsync(Path.GetFullPath(runDirectory), new EvidenceAlignmentOptions(), cancellationToken).ConfigureAwait(false);

        stdout.WriteLine($"Aligned evidence for run {result.RunId}.");
        stdout.WriteLine($"By chapter: {result.ByChapterPath} ({result.ByChapter.Chapters.Count} chapter(s))");
        stdout.WriteLine($"By slide: {result.BySlidePath} ({result.BySlide.Slides.Count} slide(s))");
        if (!result.ChaptersLoaded)
        {
            stdout.WriteLine("Note: chapters/chapters.json was not found; by-chapter view is empty. Run `zakira-replay chapters build` first to populate it.");
        }

        return 0;
    }

    private static async Task<int> RunDiscoverAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("url", "Usage: zakira-replay discover <url> [--browser] [--output <path>]");
        var output = parsed.Get("output");
        var useBrowser = parsed.GetBool("browser", defaultValue: false);
        var dependencies = new DependencyResolver();
        var processRunner = new ProcessRunner();
        var discovery = new DiscoveryService(dependencies, processRunner);
        var result = await discovery.DiscoverAsync(source, useBrowser, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(result, CliJson.Options);

        if (string.IsNullOrWhiteSpace(output))
        {
            stdout.WriteLine(json);
        }
        else
        {
            var outputPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Discovered {result.Sources.Count} source(s).");
            stdout.WriteLine($"Output: {outputPath}");
        }

        return 0;
    }

    private static async Task<int> RunBatchAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplayException("Usage: zakira-replay batch run <manifest.json>");
        }

        var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
        var manifestPath = parsed.GetRequiredPositional("manifest", "Usage: zakira-replay batch run <manifest.json>");
        var runner = new BatchRunner(CreatePipeline);
        var progress = new Progress<string>(message => stdout.WriteLine(message));
        var result = await runner.RunAsync(manifestPath, progress, cancellationToken).ConfigureAwait(false);

        stdout.WriteLine();
        stdout.WriteLine($"Completed batch: {result.BatchId}");
        stdout.WriteLine($"Batch artifacts: {result.BatchDirectory}");
        stdout.WriteLine($"Succeeded: {result.Items.Count(item => item.Succeeded)}");
        stdout.WriteLine($"Failed: {result.Items.Count(item => !item.Succeeded)}");
        return result.Items.Any(item => !item.Succeeded) ? 1 : 0;
    }

    private static async Task<int> RunQueueAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay queue <enqueue|run|status> ...");
        }

        var command = args[0].ToLowerInvariant();
        var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
        var queue = new AnalysisQueue(CreatePipeline);
        switch (command)
        {
            case "enqueue":
                var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay queue enqueue <url-or-file> [analysis options] [--queue-id <id>] [--job-id <id>] [--retries <n>]");
                var frames = parsed.GetInt("frames", parsed.GetInt("count", 500));
                var request = CreateAnalyzeRequest(parsed, source, includeTranscript: !parsed.GetBool("no-transcript", defaultValue: false), frameCount: frames, parsed.Get("run-id"));
                var enqueueResult = await queue.EnqueueAsync(parsed.Get("queue-id"), request, parsed.Get("job-id"), parsed.GetInt("retries", 0), cancellationToken).ConfigureAwait(false);
                stdout.WriteLine($"Enqueued job: {enqueueResult.JobId}");
                stdout.WriteLine($"Queue: {enqueueResult.QueueId}");
                stdout.WriteLine($"Queue directory: {enqueueResult.QueueDirectory}");
                return 0;

            case "run":
            case "worker":
                var progress = new Progress<string>(message => stdout.WriteLine(message));
                var result = await queue.RunAsync(parsed.Get("queue-id"), new AnalysisQueueRunOptions(
                    Concurrency: parsed.GetInt("concurrency", parsed.GetInt("workers", 1)),
                    Retries: parsed.GetInt("retries", 0)), progress, cancellationToken).ConfigureAwait(false);
                stdout.WriteLine();
                stdout.WriteLine($"Completed queue run: {result.QueueId}");
                stdout.WriteLine($"Queue directory: {result.QueueDirectory}");
                stdout.WriteLine($"Attempted: {result.Attempted}");
                stdout.WriteLine($"Succeeded: {result.Succeeded}");
                stdout.WriteLine($"Failed: {result.Failed}");
                stdout.WriteLine($"Pending: {result.Pending}");
                return result.Failed > 0 ? 1 : 0;

            case "status":
                var state = await queue.GetStatusAsync(parsed.Get("queue-id"), cancellationToken).ConfigureAwait(false);
                if (parsed.GetBool("json", defaultValue: false))
                {
                    stdout.WriteLine(JsonSerializer.Serialize(state, CliJson.Options));
                    return 0;
                }

                stdout.WriteLine($"Queue: {state.QueueId}");
                stdout.WriteLine($"Updated: {state.UpdatedAt:u}");
                stdout.WriteLine($"Total: {state.Jobs.Count}");
                stdout.WriteLine($"Pending: {state.Jobs.Count(job => job.Status == AnalysisQueueJobStatuses.Pending)}");
                stdout.WriteLine($"Running: {state.Jobs.Count(job => job.Status == AnalysisQueueJobStatuses.Running)}");
                stdout.WriteLine($"Succeeded: {state.Jobs.Count(job => job.Status == AnalysisQueueJobStatuses.Succeeded)}");
                stdout.WriteLine($"Failed: {state.Jobs.Count(job => job.Status == AnalysisQueueJobStatuses.Failed)}");
                return 0;

            default:
                throw new ReplayException("Usage: zakira-replay queue <enqueue|run|status> ...");
        }
    }

    private static async Task<int> RunLlmAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !args[0].Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplayException("Usage: zakira-replay llm ask <prompt> [--model <model>] [--attach <path>]");
        }

        var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
        var prompt = parsed.GetRequiredPositional("prompt", "Usage: zakira-replay llm ask <prompt> [--model <model>] [--attach <path>]");
        var attachment = parsed.Get("attach");
        var providerName = parsed.Get("llm-provider") ?? parsed.Get("provider");
        var provider = LlmProviderFactory.Create(providerName);
        var response = await provider.CompleteAsync(new LlmRequest(
            Prompt: prompt,
            AttachmentPaths: string.IsNullOrWhiteSpace(attachment) ? [] : [Path.GetFullPath(attachment)],
            Model: parsed.Get("model") ?? LlmProviderFactory.GetDefaultModel(providerName),
            SystemMessage: "You are a concise assistant used by Zakira.Replay smoke tests.",
            Timeout: TimeSpan.FromSeconds(parsed.GetInt("timeout-seconds", 180))), cancellationToken).ConfigureAwait(false);

        stdout.WriteLine(response);
        return 0;
    }

    private static async Task<int> RunConfigAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay config <path|list|get|set> ...");
        }

        var store = new ConfigStore();
        switch (args[0].ToLowerInvariant())
        {
            case "path":
                await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
                stdout.WriteLine(store.ConfigPath);
                return 0;

            case "list":
                var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in ConfigStore.ToFlatDictionary(config))
                {
                    stdout.WriteLine($"{item.Key}={item.Value}");
                }
                return 0;

            case "get":
                if (args.Length < 2)
                {
                    throw new ReplayException("Usage: zakira-replay config get <key>");
                }

                stdout.WriteLine(await store.GetAsync(args[1], cancellationToken).ConfigureAwait(false));
                return 0;

            case "set":
                if (args.Length < 3)
                {
                    throw new ReplayException("Usage: zakira-replay config set <key> <value>");
                }

                await store.SetAsync(args[1], args[2], cancellationToken).ConfigureAwait(false);
                stdout.WriteLine($"Set {args[1]}={await store.GetAsync(args[1], cancellationToken).ConfigureAwait(false)}");
                stdout.WriteLine($"Config: {store.ConfigPath}");
                return 0;

            default:
                throw new ReplayException("Usage: zakira-replay config <path|list|get|set> ...");
        }
    }


    private static async Task<int> RunDepsAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay deps <install|path> ...");
        }

        var store = new ConfigStore();
        var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
        var installer = new PortableDependencyInstaller(config);
        switch (args[0].ToLowerInvariant())
        {
            case "install":
                var parsed = CommandOptions.Parse(args.Skip(1).ToArray());
                var targets = parsed.Positionals.Count == 0 ? ["media"] : parsed.Positionals;
                var progress = new Progress<string>(message => stdout.WriteLine(message));
                var whisperModelSize = parsed.Get("whisper-model") ?? parsed.Get("model-size");
                var ocrLanguagePack = parsed.Get("language") ?? parsed.Get("ocr-language") ?? parsed.Get("language-pack");
                var visionMode = parsed.Get("mode") ?? parsed.Get("vision-mode") ?? parsed.Get("local-vision-mode");
                var result = await installer.InstallAsync(targets, parsed.GetBool("force", defaultValue: false), progress, cancellationToken, whisperModelSize, ocrLanguagePack, visionMode).ConfigureAwait(false);
                stdout.WriteLine();
                stdout.WriteLine("Dependency install complete.");
                stdout.WriteLine($"Portable directory: {result.PortableDirectory}");
                stdout.WriteLine($"ONNX model directory: {result.OnnxModelDirectory}");
                stdout.WriteLine($"OCR model directory: {result.OcrModelDirectory}");
                stdout.WriteLine($"Whisper model directory: {result.WhisperModelDirectory}");
                stdout.WriteLine($"Diarization model directory: {result.DiarizationModelDirectory}");
                stdout.WriteLine($"Vision model directory: {result.VisionModelDirectory}");
                foreach (var item in result.Items)
                {
                    stdout.WriteLine($"{item.Name}: {item.Message} ({item.Path})");
                }

                return 0;

            case "path":
            case "paths":
                stdout.WriteLine($"Portable directory: {installer.Layout.PortableDirectory}");
                stdout.WriteLine($"yt-dlp: {installer.GetPortableExecutablePath(PortableDependencyInstaller.YtDlp)}");
                stdout.WriteLine($"ffmpeg: {installer.GetPortableExecutablePath(PortableDependencyInstaller.Ffmpeg)}");
                stdout.WriteLine($"ffprobe: {installer.GetPortableExecutablePath(PortableDependencyInstaller.Ffprobe)}");
                stdout.WriteLine($"ONNX model directory: {installer.Layout.OnnxModelDirectory}");
                stdout.WriteLine($"ONNX model: {installer.GetOnnxModelPath()}");
                stdout.WriteLine($"ONNX vocabulary: {installer.GetOnnxVocabularyPath()}");
                stdout.WriteLine($"OCR model directory: {installer.Layout.OcrModelDirectory}");
                stdout.WriteLine($"OCR detection: {installer.GetOcrDetectionModelPath()}");
                stdout.WriteLine($"OCR classification: {installer.GetOcrClassificationModelPath()}");
                stdout.WriteLine($"OCR recognition: {installer.GetOcrRecognitionModelPath()}");
                stdout.WriteLine($"OCR dictionary: {installer.GetOcrDictionaryPath()}");
                stdout.WriteLine($"Whisper model directory: {installer.Layout.WhisperModelDirectory}");
                stdout.WriteLine($"Whisper default model: {installer.GetWhisperModelPath()}");
                stdout.WriteLine($"Diarization model directory: {installer.Layout.DiarizationModelDirectory}");
                stdout.WriteLine($"Diarization segmentation: {installer.GetDiarizationSegmentationPath()}");
                stdout.WriteLine($"Diarization embedding: {installer.GetDiarizationEmbeddingPath()}");
                stdout.WriteLine($"Vision model directory: {installer.Layout.VisionModelDirectory}");
                stdout.WriteLine($"Vision CLIP image encoder: {installer.GetVisionClipImageEncoderPath()}");
                stdout.WriteLine($"Vision CLIP text encoder: {installer.GetVisionClipTextEncoderPath()}");
                stdout.WriteLine($"Vision CLIP kind embeddings: {installer.GetVisionClipKindEmbeddingsPath()}");
                stdout.WriteLine($"Vision CLIP tokenizer vocab: {installer.GetVisionClipTokenizerVocabPath()}");
                stdout.WriteLine($"Vision CLIP tokenizer merges: {installer.GetVisionClipTokenizerMergesPath()}");
                return 0;

            default:
                throw new ReplayException("Usage: zakira-replay deps <install|path> ...");
        }
    }

    private static async Task<int> RunVisionAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay vision <generate-clip-embeddings> ...");
        }

        var subcommand = args[0].ToLowerInvariant().Replace('_', '-');
        var rest = args.Skip(1).ToArray();
        return subcommand switch
        {
            "generate-clip-embeddings" or "generate-embeddings" or "clip-embeddings"
                => await VisionGenerateClipEmbeddingsCommand.RunAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            _ => throw new ReplayException($"Unknown vision subcommand: {args[0]}. Use `vision generate-clip-embeddings`.")
        };
    }


    private static async Task<int> RunAuthAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ReplayException("Usage: zakira-replay auth <login|list|show|clear|path> [profile-name]");
        }

        var configStore = new ConfigStore();
        var config = await configStore.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
        var store = new AuthProfileStore(config, configStore.ConfigPath);
        var subcommand = args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "login":
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    throw new ReplayException("Usage: zakira-replay auth login <profile-name> [--url <start-url>]");
                }

                var parsed = CommandOptions.Parse(args.Skip(2).ToArray());
                var profileName = args[1];
                var startUrl = parsed.Get("url") ?? parsed.Get("start-url");
                var loginService = new AuthProfileLoginService(new DependencyResolver(config), store);
                var result = await loginService.RunAsync(
                    new AuthLoginRequest(profileName, startUrl),
                    Console.In,
                    stdout,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Saved)
                {
                    return 1;
                }

                stdout.WriteLine($"Profile slug: {AuthProfileStore.SlugifyProfileName(profileName)}");
                return 0;
            }

            case "list":
            case "ls":
            {
                var profiles = store.List();
                if (profiles.Count == 0)
                {
                    stdout.WriteLine($"No auth profiles in {store.Directory}.");
                    stdout.WriteLine("Create one with: zakira-replay auth login <profile-name>");
                    return 0;
                }

                stdout.WriteLine($"Auth directory: {store.Directory}");
                stdout.WriteLine("Profile (slug)              Age      Stale  Bytes  Path");
                foreach (var profile in profiles)
                {
                    var stale = profile.IsStale ? "yes" : "no";
                    stdout.WriteLine($"{profile.Slug,-26}  {profile.FormatAge(),-7}  {stale,-5}  {profile.ByteCount,-6}  {profile.Path}");
                }
                return 0;
            }

            case "show":
            {
                if (args.Length < 2)
                {
                    throw new ReplayException("Usage: zakira-replay auth show <profile-name>");
                }

                var profile = store.TryRead(args[1]);
                if (profile is null)
                {
                    stdout.WriteLine($"Profile '{args[1]}' not found.");
                    stdout.WriteLine($"Expected at: {store.GetProfilePath(args[1])}");
                    return 1;
                }

                stdout.WriteLine($"Name (slug):          {profile.Slug}");
                stdout.WriteLine($"Path:                 {profile.Path}");
                stdout.WriteLine($"Bytes:                {profile.ByteCount}");
                stdout.WriteLine($"Created (UTC):        {profile.CreatedAtUtc:O}");
                stdout.WriteLine($"Last write (UTC):     {profile.LastWriteAtUtc:O}");
                stdout.WriteLine($"Age:                  {profile.FormatAge()}");
                stdout.WriteLine($"Stale (>{config.Auth.StaleThresholdMinutes} min): {profile.IsStale}");
                return 0;
            }

            case "clear":
            case "remove":
            case "rm":
            case "delete":
            {
                if (args.Length < 2)
                {
                    throw new ReplayException("Usage: zakira-replay auth clear <profile-name>");
                }

                var existed = store.Clear(args[1]);
                if (existed)
                {
                    stdout.WriteLine($"Removed auth profile: {AuthProfileStore.SlugifyProfileName(args[1])}");
                    return 0;
                }

                stdout.WriteLine($"Profile '{args[1]}' did not exist.");
                return 1;
            }

            case "path":
            {
                if (args.Length < 2)
                {
                    stdout.WriteLine(store.Directory);
                    return 0;
                }

                stdout.WriteLine(store.GetProfilePath(args[1]));
                return 0;
            }

            default:
                throw new ReplayException("Usage: zakira-replay auth <login|list|show|clear|path> [profile-name]");
        }
    }


    private static async Task<int> RunPipelineAsync(AnalyzeRequest request, TextWriter stdout, CancellationToken cancellationToken)
    {
        var pipeline = CreatePipeline();
        var progress = new Progress<string>(message => stdout.WriteLine(message));
        var result = await pipeline.AnalyzeAsync(request, progress, cancellationToken).ConfigureAwait(false);

        stdout.WriteLine();
        stdout.WriteLine(result.Reused ? $"Reused run: {result.Run.Id}" : $"Completed run: {result.Run.Id}");
        stdout.WriteLine($"Artifacts: {result.Run.Directory}");
        stdout.WriteLine($"Manifest: {result.Run.GetPath("manifest.json")}");

        if (result.Manifest.Warnings.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("Warnings:");
            foreach (var warning in result.Manifest.Warnings)
            {
                stdout.WriteLine($"- [{warning.Severity}] {warning.Code}: {warning.Message}");
            }
        }

        return 0;
    }

    private static async Task<int> RunMcpAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
        {
            var server = new McpServer(CreatePipeline, stdout, stderr);
            await server.RunAsync(Console.In, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        stderr.WriteLine("Usage: zakira-replay mcp serve");
        return 1;
    }

    private static AnalysisPipeline CreatePipeline()
    {
        var dependencies = new DependencyResolver();
        var processRunner = new ProcessRunner();
        var artifactStore = new ArtifactStore(ArtifactStore.GetDefaultRootDirectory());
        var ytDlp = new YtDlpClient(dependencies, processRunner);
        var ffmpeg = new FfmpegClient(dependencies, processRunner);
        var browserCapture = new PlaywrightVideoCaptureClient(dependencies);
        return new AnalysisPipeline(artifactStore, ytDlp, ffmpeg, provider => LlmProviderFactory.TryCreate(provider), browserCapture);
    }

    private static ClipExtractionService CreateClipService()
    {
        var dependencies = new DependencyResolver();
        var processRunner = new ProcessRunner();
        var artifactStore = new ArtifactStore(ArtifactStore.GetDefaultRootDirectory());
        var ytDlp = new YtDlpClient(dependencies, processRunner);
        var ffmpeg = new FfmpegClient(dependencies, processRunner);
        return new ClipExtractionService(artifactStore, ytDlp, ffmpeg);
    }

    private static FrameCaptureService CreateFrameCaptureService()
    {
        var dependencies = new DependencyResolver();
        var processRunner = new ProcessRunner();
        var artifactStore = new ArtifactStore(ArtifactStore.GetDefaultRootDirectory());
        var ytDlp = new YtDlpClient(dependencies, processRunner);
        var ffmpeg = new FfmpegClient(dependencies, processRunner);
        return new FrameCaptureService(artifactStore, ytDlp, ffmpeg);
    }

    private static string GetRequiredOption(CommandOptions parsed, string name, string usage)
    {
        var value = parsed.Get(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new ReplayException($"Missing required option `--{name}`.\n{usage}");
    }

    private static AnalyzeRequest CreateAnalyzeRequest(
        CommandOptions parsed,
        string source,
        bool includeTranscript,
        int frameCount,
        string? runId)
    {
        var useOcr = parsed.GetBool("ocr", defaultValue: false);
        var useVision = parsed.GetBool("vision", defaultValue: false);
        var useStt = parsed.GetBool("stt", defaultValue: false);
        var extractAudio = parsed.GetBool("audio", defaultValue: false) || useStt;
        var browserAuth = parsed.Get("browser-auth");
        var config = new ConfigStore().Load();
        var llmProvider = LlmProviderFactory.GetConfiguredProvider(config);
        llmProvider = LlmProviderFactory.Normalize(parsed.Get("llm-provider") ?? parsed.Get("provider") ?? llmProvider);
        var ocrProvider = OcrProviderFactory.GetConfiguredProvider(config);
        ocrProvider = OcrProviderFactory.Normalize(parsed.Get("ocr-provider") ?? ocrProvider);
        var visionProvider = VisionProviderFactory.GetConfiguredProvider(config);
        visionProvider = VisionProviderFactory.Normalize(parsed.Get("vision-provider") ?? visionProvider);
        var localVisionMode = parsed.Get("local-vision-mode") ?? parsed.Get("vision-local-mode");
        bool? smartCrop = null;
        if (parsed.GetBool("smart-crop", defaultValue: false))
        {
            smartCrop = true;
        }
        else if (parsed.GetBool("no-smart-crop", defaultValue: false))
        {
            smartCrop = false;
        }
        var smartCropProfile = parsed.Get("smart-crop-profile") ?? parsed.Get("crop-profile");
        var captureMode = parsed.Get("capture-mode") ?? parsed.Get("capture");
        var authProfile = parsed.Get("auth-profile") ?? parsed.Get("auth");
        var captionLanguages = ParseCaptionLanguagesOption(parsed);
        bool? slideGrouping = parsed.GetBool("no-slide-grouping", defaultValue: false) ? false : null;
        var slideHashDistance = parsed.GetOptionalInt("slide-hash-distance");
        var framesPerMinute = parsed.GetOptionalInt("frames-per-minute");
        var sceneSafetyCap = parsed.GetOptionalInt("scene-safety-cap");
        var visionInstruction = parsed.Get("vision-instruction") ?? string.Empty;
        var ocrInstruction = parsed.Get("ocr-instruction") ?? string.Empty;
        var useDiarization = parsed.GetBool("diarize", defaultValue: false) || parsed.GetBool("diarization", defaultValue: false);
        var numSpeakers = parsed.GetOptionalInt("num-speakers");
        float? diarizationThreshold = null;
        var thresholdValue = parsed.Get("diarize-threshold") ?? parsed.Get("diarization-threshold");
        if (!string.IsNullOrWhiteSpace(thresholdValue)
            && float.TryParse(thresholdValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedThreshold)
            && parsedThreshold > 0)
        {
            diarizationThreshold = parsedThreshold;
        }
        return new AnalyzeRequest(
            Source: source,
            VisionInstruction: visionInstruction,
            OcrInstruction: ocrInstruction,
            IncludeTranscript: includeTranscript,
            FrameCount: frameCount,
            RunId: runId,
            ExtractAudio: extractAudio,
            UseSpeechToText: useStt,
            UseOcr: useOcr,
            UseVision: useVision,
            MaxAiFrames: parsed.GetInt("max-ai-frames", 50),
            Model: parsed.Get("model") ?? LlmProviderFactory.GetDefaultModel(llmProvider, config),
            LlmProvider: llmProvider,
            Force: parsed.GetBool("force", defaultValue: false),
            UseCache: parsed.GetBool("cache", defaultValue: false),
            FrameStrategy: ResolveFrameStrategy(parsed),
            CookiesPath: parsed.Get("cookies"),
            CookiesFromBrowser: browserAuth ?? parsed.Get("cookies-from-browser"),
            CaptionLanguages: captionLanguages,
            SlideGrouping: slideGrouping,
            SlideHashDistance: slideHashDistance,
            FramesPerMinute: framesPerMinute,
            SceneSafetyCap: sceneSafetyCap,
            OcrProvider: ocrProvider,
            SmartCrop: smartCrop,
            SmartCropProfile: smartCropProfile,
            CaptureMode: captureMode,
            AuthProfile: authProfile,
            UseDiarization: useDiarization,
            NumSpeakers: numSpeakers,
            DiarizationThreshold: diarizationThreshold,
            VisionProvider: visionProvider,
            LocalVisionMode: localVisionMode);
    }

    private static IReadOnlyList<string>? ParseCaptionLanguagesOption(CommandOptions parsed)
    {
        var raw = parsed.Get("caption-languages") ?? parsed.Get("captions-languages") ?? parsed.Get("sub-langs");
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

    private static string ResolveFrameStrategy(CommandOptions parsed)
    {
        if (parsed.GetBool("every-frame", defaultValue: false) || parsed.GetBool("frame-by-frame", defaultValue: false))
        {
            return FrameSelectionStrategies.EveryFrame;
        }

        return parsed.Get("frame-strategy") ?? FrameSelectionStrategies.Scene;
    }

    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown command: {command}");
        stderr.WriteLine("Run `zakira-replay --help` for usage.");
        return 1;
    }

    private static bool IsHelp(string value)
    {
        return value.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || value.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || value.Equals("help", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteHelp(TextWriter stdout)
    {
        stdout.WriteLine("Zakira.Replay");
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  zakira-replay version");
        stdout.WriteLine("  zakira-replay info [--json]");
        stdout.WriteLine("  zakira-replay doctor [--json]");
        stdout.WriteLine("  zakira-replay analyze <url-or-file> [--vision-instruction <text>] [--ocr-instruction <text>] [--frames <count>] [--frames-per-minute <n>] [--frame-strategy interval|scene|every-frame] [--scene-safety-cap <n>] [--llm-provider github-copilot|openai|azure-openai|ollama|local-whisper] [--ocr-provider copilot|local] [--vision-provider copilot|local] [--local-vision-mode heuristic|clip|clip-blip] [--smart-crop] [--smart-crop-profile auto|teams|zoom|webex|generic|off] [--capture-mode auto|ytdlp|browser] [--auth-profile <name>] [--stt] [--ocr] [--vision] [--diarize] [--num-speakers <n>] [--diarize-threshold <0.0-1.0>] [--caption-languages <list>] [--no-slide-grouping] [--slide-hash-distance <n>] [--run-id <id>] [--cache] [--force]    # Defaults: --frames 500, --frame-strategy scene, --ocr-provider local (auto-downloads models), --vision-provider copilot, --max-ai-frames 50, --scene-safety-cap 5000. --vision-provider local runs a fully-on-device LocalOnnxVisionProvider that never calls an LLM (heuristic mode is zero-model; clip/clip-blip require user-supplied ONNX files configured via `config set vision.local.*`). ollama provides fully-local chat/vision via an Ollama daemon (no STT); local-whisper provides fully-local STT only; --diarize runs local sherpa-onnx speaker diarization on top of the transcript.");
        stdout.WriteLine("  zakira-replay transcribe <url-or-file> [--stt] [--audio] [--run-id <id>] [--cache] [--force]");
        stdout.WriteLine("  zakira-replay frames <url-or-file> [--at <ts1,ts2,...>] [--from <ts> --to <ts> [--count <n>] [--strategy interval|scene]] [--max-edge <px>] [--quality <1-100>] [--phash] [--scene-safety-cap <n>] [--run-id <id>] [--json]   # Ad-hoc capture: pass `--at` for exact timestamps OR `--from`/`--to` for a window. Without either flag, falls back to the legacy analyze-frames pipeline (--count controls how many).");
        stdout.WriteLine("  zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp> [--run-id <id>] [--output-name <name>]");
        stdout.WriteLine("  zakira-replay search build <run-directory> [--backend json|sqlite|sqlite-onnx]");
        stdout.WriteLine("  zakira-replay search query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx]");
        stdout.WriteLine("  zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]");
        stdout.WriteLine("  zakira-replay align <run-directory>");
        stdout.WriteLine("  zakira-replay discover <url> [--browser] [--output <path>]");
        stdout.WriteLine("  zakira-replay batch run <manifest.json>");
        stdout.WriteLine("  zakira-replay queue enqueue <url-or-file> [analysis options] [--queue-id <id>] [--job-id <id>] [--retries <n>]");
        stdout.WriteLine("  zakira-replay queue run [--queue-id <id>] [--concurrency <n>] [--retries <n>]");
        stdout.WriteLine("  zakira-replay queue status [--queue-id <id>] [--json]");
        stdout.WriteLine("  zakira-replay llm ask <prompt> [--llm-provider github-copilot|openai|azure-openai|ollama] [--model <model>] [--attach <path>]    # local-whisper is STT-only and not valid for `llm ask`");
        stdout.WriteLine("  zakira-replay deps install [yt-dlp|ffmpeg|ffprobe|onnx|ocr|whisper-model|diarization|media|all] [--whisper-model tiny|base|small|medium|large-v3|large-v3-turbo] [--language latin|chinese|english|korean|cyrillic|arabic|devanagari|greek|telugu|tamil] [--force]  # defaults: target=media, --whisper-model=small, --language=latin");
        stdout.WriteLine("  zakira-replay deps path");
        stdout.WriteLine("  zakira-replay auth login <profile-name> [--url <start-url>]");
        stdout.WriteLine("  zakira-replay auth list");
        stdout.WriteLine("  zakira-replay auth show <profile-name>");
        stdout.WriteLine("  zakira-replay auth clear <profile-name>");
        stdout.WriteLine("  zakira-replay auth path [profile-name]");
        stdout.WriteLine("  zakira-replay config <path|list|get|set> ...");
        stdout.WriteLine("  zakira-replay mcp serve");
        stdout.WriteLine();
        stdout.WriteLine("Dependency policy:");
        stdout.WriteLine("  Dependencies are not installed automatically unless dependencies.autoDownload=true.");
        stdout.WriteLine("  Use `zakira-replay deps install media` to install portable yt-dlp/ffmpeg, or configure paths manually.");
        stdout.WriteLine("  Supported keys include dependency paths, search.onnx.*, llm.provider, llm.openai.*, and llm.azureOpenAi.*.");
        stdout.WriteLine("  Pass yt-dlp auth with --cookies <file> or --cookies-from-browser/--browser-auth <browser>.");
    }
}

internal static class AppInfo
{
    public const string Name = "Zakira.Replay";

    public static string Version => ReplayVersion.Current;
}

internal sealed record ReplayInfo(
    string Name,
    string Version,
    string ConfigPath,
    string RunsDirectory,
    string LlmProvider,
    string DefaultModel,
    IReadOnlyList<string> Schemas,
    ReplayInfoDependencies? ResolvedDependencies = null,
    ReplayInfoCapabilities? Capabilities = null);

/// <summary>
/// Resolved on-disk paths and configured names for every optional dependency. Useful as a
/// pre-flight for orchestrators: they can call <c>zakira-replay info --json</c> once and know
/// which optional features are wired up without separately running <c>doctor</c>.
/// </summary>
internal sealed record ReplayInfoDependencies(
    string PortableDirectory,
    string OcrModelDirectory,
    string OcrLanguagePack,
    string OnnxModelDirectory,
    string WhisperModelDirectory,
    string? WhisperModelPath,
    string? WhisperModelSize,
    string DiarizationModelDirectory,
    string? OllamaEndpoint,
    string? OllamaModel,
    string? OllamaVisionModel);

/// <summary>
/// Static capability summary: which optional features are available without launching a
/// daemon, downloading a model, or hitting the network. Booleans here reflect what's
/// installed and reachable at info-time; they do NOT promise the dependency will still be
/// working at analysis-time (use <c>doctor</c> for that).
/// </summary>
internal sealed record ReplayInfoCapabilities(
    bool LocalOcrReady,
    bool LocalWhisperReady,
    bool DiarizationReady,
    bool YtDlpAvailable,
    bool FfmpegAvailable);

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string?> options;
    private readonly List<string> positionals;

    private CommandOptions(Dictionary<string, string?> options, List<string> positionals)
    {
        this.options = options;
        this.positionals = positionals;
    }

    public static CommandOptions Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var nameValue = arg[2..].Split('=', 2);
            var name = nameValue[0];
            if (nameValue.Length == 2)
            {
                options[name] = nameValue[1];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = args[++i];
            }
            else
            {
                options[name] = "true";
            }
        }

        return new CommandOptions(options, positionals);
    }

    public string? Get(string name) => options.GetValueOrDefault(name);

    public IReadOnlyList<string> Positionals => positionals;

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public int? GetOptionalInt(string name)
    {
        var value = Get(name);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    public double GetDouble(string name, double defaultValue)
    {
        var value = Get(name);
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    public bool GetBool(string name, bool defaultValue)
    {
        var value = Get(name);
        if (value is null)
        {
            return defaultValue;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public string GetRequiredPositional(string name, string usage)
    {
        return GetRequiredPositional(0, name, usage);
    }

    public string GetRequiredPositional(int index, string name, string usage)
    {
        if (positionals.Count > index && !string.IsNullOrWhiteSpace(positionals[index]))
        {
            return positionals[index];
        }

        throw new ReplayException($"Missing required argument `{name}`.\n{usage}");
    }
}

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

internal sealed record DoctorReport(DateTimeOffset CreatedAt, IReadOnlyList<DoctorDependencyReport> Dependencies);

internal sealed record DoctorDependencyReport(
    string Name,
    bool IsFound,
    bool IsRunnable,
    string? Path,
    string? Source,
    string? Message,
    string? RunnableError);
