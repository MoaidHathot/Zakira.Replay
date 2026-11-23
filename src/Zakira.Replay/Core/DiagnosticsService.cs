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
        var checkTimeout = TimeoutsConfig.Resolve(config.Timeouts.DependencyCheckSeconds, 60);
        var reports = new List<DoctorDependencyReport>();
        foreach (var status in dependencies.GetAllStatuses())
        {
            string? error = null;
            string? version = null;
            if (status.Path is not null && status.Name is ("yt-dlp" or "ffmpeg" or "ffprobe"))
            {
                (error, version) = await DependencyVersionProbe.RunAsync(status.Name, status.Path, processRunner, checkTimeout, cancellationToken).ConfigureAwait(false);
            }

            var advice = error is not null
                ? DoctorAdvice.ForRunnableError(status.Name, error)
                : status.Name == "yt-dlp" ? DoctorAdvice.ForYtDlpVersion(version, DateTimeOffset.UtcNow) : null;
            if (status.Name == "yt-dlp" && !status.IsFound && status.Source is null && config.Dependencies.AutoDownload)
            {
                advice = "Auto-download is enabled: the first operation that needs yt-dlp will install it. To check it now, run `zakira-replay deps install yt-dlp` and re-run doctor.";
            }

            reports.Add(new DoctorDependencyReport(status.Name, status.IsFound, status.IsFound && error is null, status.Path, status.Source, status.Message, error, version, advice));
        }

        reports.Add(await BuildJsRuntimeReportAsync(checkTimeout, cancellationToken).ConfigureAwait(false));
        reports.Add(BuildWhisperDoctorReport(config));
        reports.Add(await BuildOllamaDoctorReportAsync(config, cancellationToken).ConfigureAwait(false));
        reports.Add(BuildDiarizationDoctorReport(config));
        reports.Add(BuildOcrDoctorReport(config));
        reports.Add(BuildVisionDoctorReport(config));
        reports.Add(BuildEdgeProfileDoctorReport(config));

        return new DoctorReport(DateTimeOffset.UtcNow, reports);
    }

    /// <summary>
    /// yt-dlp needs a JavaScript runtime on PATH to solve YouTube's player challenges (the
    /// "n"/signature functions moved to an external solver in 2025). Without one, formats go
    /// missing ("Requested format is not available"), downloads throttle, or <c>-g</c> stalls -
    /// the single most common real-world failure behind "yt-dlp timed out". Reported as its own
    /// row so the fix (install deno) is visible before the first failed run.
    /// </summary>
    private async Task<DoctorDependencyReport> BuildJsRuntimeReportAsync(TimeSpan checkTimeout, CancellationToken cancellationToken)
    {
        foreach (var runtime in DoctorAdvice.JsRuntimes)
        {
            var path = DependencyResolver.FindExecutableOnPath(runtime);
            if (path is null)
            {
                continue;
            }

            var (error, version) = await DependencyVersionProbe.RunAsync(runtime, path, processRunner, checkTimeout, cancellationToken).ConfigureAwait(false);

            return new DoctorDependencyReport(
                "js-runtime",
                IsFound: true,
                IsRunnable: error is null,
                path,
                "PATH",
                $"{runtime} — JavaScript runtime yt-dlp uses for YouTube challenge solving",
                error,
                version,
                Advice: null);
        }

        return new DoctorDependencyReport(
            "js-runtime",
            IsFound: false,
            IsRunnable: false,
            Path: null,
            Source: "PATH",
            Message: "No JavaScript runtime (deno, node or bun) found on PATH. Current yt-dlp needs one to solve YouTube's player challenges; without it formats go missing, downloads throttle and `-g` can stall until the timeout.",
            RunnableError: null,
            Version: null,
            Advice: DoctorAdvice.InstallJsRuntime);
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
}

/// <summary>
/// Doctor heuristics that turn raw probe output into a next step: yt-dlp staleness (date-based
/// versions; YouTube breaks old releases within weeks), the JavaScript runtime yt-dlp needs, and
/// known "found but not runnable" signatures.
/// </summary>
public static class DoctorAdvice
{
    /// <summary>yt-dlp releases are dated; after this many days a release is very likely behind a YouTube change.</summary>
    public const int YtDlpStaleAfterDays = 60;

    /// <summary>Runtimes yt-dlp can use for its JavaScript challenge solver, in preference order.</summary>
    public static readonly string[] JsRuntimes = ["deno", "node", "bun"];

    public const string InstallJsRuntime = "Install deno (recommended: https://deno.com - `winget install DenoLand.Deno`, `brew install deno`, or `curl -fsSL https://deno.land/install.sh | sh`) or node, make sure it is on PATH, then re-run `zakira-replay doctor`.";

    public static string? ParseVersionLine(string dependencyName, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (dependencyName is "ffmpeg" or "ffprobe")
        {
            // "ffmpeg version N-124279-g0f6ba39122-20260430 Copyright (c) ..." -> "N-124279-g0f6ba39122-20260430"
            const string prefix = " version ";
            var start = trimmed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                trimmed = trimmed[(start + prefix.Length)..];
            }

            var copyright = trimmed.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
            if (copyright > 0)
            {
                trimmed = trimmed[..copyright];
            }
        }

        return trimmed.Trim();
    }

    /// <summary>Parses yt-dlp's <c>YYYY.MM.DD[.hotfix]</c> version into a release date.</summary>
    public static DateOnly? ParseYtDlpReleaseDate(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var year)
            || !int.TryParse(parts[1], out var month)
            || !int.TryParse(parts[2], out var day)
            || year < 2020 || month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string? ForYtDlpVersion(string? version, DateTimeOffset now)
    {
        var released = ParseYtDlpReleaseDate(version);
        if (released is null)
        {
            return null;
        }

        var age = DateOnly.FromDateTime(now.UtcDateTime).DayNumber - released.Value.DayNumber;
        if (age <= YtDlpStaleAfterDays)
        {
            return null;
        }

        return $"yt-dlp {version} is {age} days old. YouTube changes break older releases within weeks (missing formats, bot checks, `-g` stalls); update with `zakira-replay deps install yt-dlp --force` (or `pip install -U yt-dlp` / `winget upgrade yt-dlp`).";
    }

    /// <summary>Known "installed but will not start" signatures.</summary>
    public static string? ForRunnableError(string dependencyName, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        if (error.Contains("Application Control policy", StringComparison.OrdinalIgnoreCase)
            || error.Contains("blocked by your organization", StringComparison.OrdinalIgnoreCase)
            || error.Contains("AppLocker", StringComparison.OrdinalIgnoreCase))
        {
            return dependencyName == "yt-dlp"
                ? "An application-control policy blocked yt-dlp or one of its DLLs. For a single-file build, run `zakira-replay deps install yt-dlp --force` and clear any yt-dlp path override to use the Windows directory build. If that build is also blocked, ask your administrator for an approved installation."
                : "An application-control policy blocks this executable. Install it through a channel your policy trusts (winget/MSI) and point the matching *.path config key at it.";
        }

        if (dependencyName == "yt-dlp" && error.Contains("Failed to load Python DLL", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlp could not load its bundled Python runtime. Run `zakira-replay deps install yt-dlp --force` to install the complete Windows directory build; clear any yt-dlp path override to use it.";
        }

        if (error.Contains("did not answer", StringComparison.OrdinalIgnoreCase))
        {
            return "The probe timed out rather than failed. Re-run doctor once (first start of a self-extracting binary is slow), then raise timeouts.dependencyCheckSeconds if it keeps happening.";
        }

        return null;
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
    string? RunnableError,
    string? Version = null,
    string? Advice = null);
