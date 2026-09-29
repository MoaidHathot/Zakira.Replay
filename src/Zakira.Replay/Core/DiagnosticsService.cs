namespace Zakira.Replay.Core;

/// <summary>
/// Builds the machine-readable <c>info</c> and <c>doctor</c> reports. Lives in Core (rather
/// than the CLI) so both the <c>zakira-replay</c> CLI and the MCP server consume identical
/// dependency-probe logic. The CLI keeps only the human-readable rendering
/// (<c>CliDiagnostics.RenderDoctorText</c>); everything else — probing binaries, models,
/// daemons, and the Edge profile — is here.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly ConfigStore configStore;
    private readonly ProcessRunner processRunner;

    public DiagnosticsService(ConfigStore? configStore = null, ProcessRunner? processRunner = null)
    {
        this.configStore = configStore ?? new ConfigStore();
        this.processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<ReplayInfo> BuildInfoAsync(CancellationToken cancellationToken)
    {
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

        return new ReplayInfo(
            Name: "Zakira.Replay",
            Version: ReplayVersion.Current,
            ConfigPath: configStore.ConfigPath,
            RunsDirectory: ArtifactStore.ResolveRootDirectory(config),
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
    }

    public async Task<DoctorReport> BuildDoctorReportAsync(CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var dependencies = new DependencyResolver(config);
        var reports = new List<DoctorDependencyReport>();
        foreach (var status in dependencies.GetAllStatuses())
        {
            string? runnable = null;
            if (status.IsFound)
            {
                runnable = await TryCheckRunnableAsync(status, processRunner, TimeoutsConfig.Resolve(config.Timeouts.DependencyCheckSeconds, 60), cancellationToken).ConfigureAwait(false);
            }

            reports.Add(new DoctorDependencyReport(status.Name, status.IsFound, string.IsNullOrWhiteSpace(runnable), status.Path, status.Source, status.Message, runnable));
        }

        reports.Add(BuildWhisperDoctorReport(config));
        reports.Add(await BuildOllamaDoctorReportAsync(config, cancellationToken).ConfigureAwait(false));
        reports.Add(BuildDiarizationDoctorReport(config));
        reports.Add(BuildOcrDoctorReport(config));
        reports.Add(BuildVisionDoctorReport(config));
        reports.Add(BuildEdgeProfileDoctorReport(config));

        return new DoctorReport(DateTimeOffset.UtcNow, reports);
    }

    private static LocalWhisperOptions? SafeResolveWhisperOptions(ReplayConfig config)
    {
        try { return LocalWhisperOptions.Resolve(config); } catch { return null; }
    }

    private static LocalOcrModelPaths? SafeResolveOcrPaths(ReplayConfig config)
    {
        try { return LocalOcrModelPaths.Resolve(config); } catch { return null; }
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

    private static DoctorDependencyReport BuildWhisperDoctorReport(ReplayConfig config)
    {
        try
        {
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

    private static DoctorDependencyReport BuildOcrDoctorReport(ReplayConfig config)
    {
        try
        {
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

    private static DoctorDependencyReport BuildVisionDoctorReport(ReplayConfig config)
    {
        try
        {
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

    private static DoctorDependencyReport BuildDiarizationDoctorReport(ReplayConfig config)
    {
        try
        {
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

    private static DoctorDependencyReport BuildEdgeProfileDoctorReport(ReplayConfig config)
    {
        try
        {
            var userDataDir = config.Capture.Browser.ResolveEdgeUserDataDir();
            var profileDirectory = config.Capture.Browser.ResolveEdgeProfileDirectory();
            var dirExists = Directory.Exists(userDataDir);
            var initialized = config.Capture.Browser.IsEdgeProfileInitialized();
            var singletonLock = config.Capture.Browser.GetEdgeProfileSingletonLockPath();
            var isLocked = File.Exists(singletonLock);

            string message;
            bool isFound;
            string? runnableError;
            if (isLocked)
            {
                message = $"profile '{profileDirectory}' is locked by a running Edge instance ({singletonLock})";
                isFound = false;
                runnableError = "Close Edge before running analyze.";
            }
            else if (initialized)
            {
                message = $"profile '{profileDirectory}' ready (DPAPI-encrypted cookies present)";
                isFound = true;
                runnableError = null;
            }
            else if (!dirExists)
            {
                message = $"directory does not exist. Run `zakira-replay auth init-edge-profile` to create it.";
                isFound = false;
                runnableError = "user-data-dir missing";
            }
            else
            {
                message = $"directory exists but profile '{profileDirectory}' has no Cookies yet. Run `zakira-replay auth init-edge-profile`.";
                isFound = false;
                runnableError = "profile not initialized";
            }

            return new DoctorDependencyReport(
                Name: "edge-profile",
                IsFound: isFound,
                IsRunnable: isFound,
                Path: userDataDir,
                Source: "config",
                Message: message,
                RunnableError: runnableError);
        }
        catch (Exception ex)
        {
            return new DoctorDependencyReport(
                Name: "edge-profile",
                IsFound: false,
                IsRunnable: false,
                Path: null,
                Source: "config",
                Message: ex.Message,
                RunnableError: ex.Message);
        }
    }

    private static async Task<DoctorDependencyReport> BuildOllamaDoctorReportAsync(ReplayConfig config, CancellationToken cancellationToken)
    {
        try
        {
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

    private static async Task<string?> TryCheckRunnableAsync(DependencyStatus status, ProcessRunner runner, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.Path) || status.Name is not ("yt-dlp" or "ffmpeg" or "ffprobe"))
        {
            return null;
        }

        var args = status.Name == "yt-dlp" ? new[] { "--version" } : new[] { "-version" };
        try
        {
            var result = await runner.RunAsync(status.Path, args, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? null : FirstLine(result.StandardError + result.StandardOutput);
        }
        catch (ProcessTimeoutException)
        {
            return $"did not answer `{args[0]}` within {timeout.TotalSeconds:0}s (raise timeouts.dependencyCheckSeconds if this machine starts it slowly; antivirus scanning of a self-extracting yt-dlp.exe is a common cause)";
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
}

/// <summary>
/// Resolved configuration, dependency layout, and capability summary emitted by <c>info</c>.
/// </summary>
public sealed record ReplayInfo(
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
/// pre-flight for orchestrators: they can read <c>info</c> once and know which optional
/// features are wired up without separately running <c>doctor</c>.
/// </summary>
public sealed record ReplayInfoDependencies(
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
/// daemon, downloading a model, or hitting the network. Booleans reflect what's installed and
/// reachable at info-time; they do NOT promise the dependency will still be working at
/// analysis-time (use <c>doctor</c> for that).
/// </summary>
public sealed record ReplayInfoCapabilities(
    bool LocalOcrReady,
    bool LocalWhisperReady,
    bool DiarizationReady,
    bool YtDlpAvailable,
    bool FfmpegAvailable);

public sealed record DoctorReport(DateTimeOffset CreatedAt, IReadOnlyList<DoctorDependencyReport> Dependencies);

public sealed record DoctorDependencyReport(
    string Name,
    bool IsFound,
    bool IsRunnable,
    string? Path,
    string? Source,
    string? Message,
    string? RunnableError);
