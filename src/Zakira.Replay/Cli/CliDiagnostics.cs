using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Cli;

/// <summary>
/// Helpers that drive the <c>info</c> and <c>doctor</c> commands. Extracted out of
/// <see cref="CliApp"/> so the System.CommandLine root command stays focused on argument
/// parsing rather than carrying ~400 lines of dependency-probe code.
/// </summary>
internal static class CliDiagnostics
{
    public static async Task<ReplayInfo> BuildInfoAsync(CancellationToken cancellationToken)
    {
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

        return new ReplayInfo(
            Name: AppInfo.Name,
            Version: AppInfo.Version,
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

    public static async Task<DoctorReport> BuildDoctorReportAsync(CancellationToken cancellationToken)
    {
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

        reports.Add(BuildWhisperDoctorReport());
        reports.Add(await BuildOllamaDoctorReportAsync(cancellationToken).ConfigureAwait(false));
        reports.Add(BuildDiarizationDoctorReport());
        reports.Add(BuildOcrDoctorReport());
        reports.Add(BuildVisionDoctorReport());
        reports.Add(BuildEdgeProfileDoctorReport());

        return new DoctorReport(DateTimeOffset.UtcNow, reports);
    }

    /// <summary>
    /// Renders a <see cref="DoctorReport"/> as a scannable, optionally colorized report:
    /// a count summary header, one aligned row per dependency (green ✓ ok / yellow ! degraded /
    /// red ✗ missing) with the source and path dimmed, an indented reason line for anything that
    /// needs attention, and a closing summary. Pure function of its inputs — <paramref name="color"/>
    /// gates every escape code so redirected output and unit tests stay plain text.
    /// </summary>
    public static void RenderDoctorText(DoctorReport report, TextWriter writer, bool color)
    {
        var deps = report.Dependencies;
        var okCount = deps.Count(d => d.IsFound && d.IsRunnable);
        var degradedCount = deps.Count(d => d.IsFound && !d.IsRunnable);
        var missingCount = deps.Count(d => !d.IsFound);

        var badges = new List<string> { ConsoleStyle.Green($"{okCount} ok", color) };
        if (degradedCount > 0)
        {
            badges.Add(ConsoleStyle.Yellow($"{degradedCount} degraded", color));
        }
        if (missingCount > 0)
        {
            badges.Add(ConsoleStyle.Red($"{missingCount} missing", color));
        }

        writer.WriteLine($"{ConsoleStyle.Bold("Zakira.Replay doctor", color)}  {string.Join(" \u00b7 ", badges)}");
        writer.WriteLine();

        // Widest word is "degraded" (8). Pad the plain word before coloring so the ANSI escape
        // codes never throw the name column out of alignment.
        var nameWidth = deps.Count == 0 ? 0 : Math.Min(deps.Max(d => d.Name.Length), 20);
        const string Indent = "         ";

        foreach (var dep in deps)
        {
            if (dep.IsFound && dep.IsRunnable)
            {
                var detail = JoinDetail(dep.Source, dep.Path);
                var line = $"  {ConsoleStyle.Green("\u2713", color)} {ConsoleStyle.Green("ok".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}";
                if (!string.IsNullOrEmpty(detail))
                {
                    line += "  " + ConsoleStyle.Dim(detail, color);
                }

                writer.WriteLine(line);
            }
            else if (dep.IsFound)
            {
                writer.WriteLine($"  {ConsoleStyle.Yellow("!", color)} {ConsoleStyle.Yellow("degraded".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}{SourceSuffix(dep.Source, color)}");
                WriteDetailLines(writer, Indent, dep.Path, FirstNonEmpty(dep.RunnableError, dep.Message), ConsoleStyle.Yellow, color);
            }
            else
            {
                writer.WriteLine($"  {ConsoleStyle.Red("\u2717", color)} {ConsoleStyle.Red("missing".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}{SourceSuffix(dep.Source, color)}");
                WriteDetailLines(writer, Indent, dep.Path, FirstNonEmpty(dep.Message, dep.RunnableError), ConsoleStyle.Red, color);
            }
        }

        writer.WriteLine();
        if (missingCount == 0 && degradedCount == 0)
        {
            writer.WriteLine(ConsoleStyle.Green("All dependencies are ready.", color));
        }
        else
        {
            var issues = missingCount + degradedCount;
            var noun = issues == 1 ? "component needs" : "components need";
            writer.WriteLine($"{issues} {noun} attention \u00b7 run `zakira-replay doctor --output-format json` for full detail.");
        }
    }

    private static void WriteDetailLines(TextWriter writer, string indent, string? path, string? reason, Func<string, bool, string> reasonColor, bool color)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            writer.WriteLine(indent + ConsoleStyle.Dim(path!, color));
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            writer.WriteLine(indent + reasonColor("\u2192 " + reason, color));
        }
    }

    private static string SourceSuffix(string? source, bool color)
        => string.IsNullOrWhiteSpace(source) ? string.Empty : "  " + ConsoleStyle.Dim(source!, color);

    private static string JoinDetail(string? source, string? path)
    {
        var parts = new[] { source, path }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("  ", parts);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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

    private static DoctorDependencyReport BuildEdgeProfileDoctorReport()
    {
        try
        {
            var config = new ConfigStore().Load();
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
}
