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
            "discover" => await RunDiscoverAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "batch" => await RunBatchAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "queue" => await RunQueueAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "llm" => await RunLlmAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "deps" or "dependencies" => await RunDepsAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
            "config" => await RunConfigAsync(rest, stdout, cancellationToken).ConfigureAwait(false),
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
                "search-index.schema.json",
                "batch.schema.json",
                "batch-result.schema.json",
                "queue.schema.json",
                "queue-run-result.schema.json"
            ]);

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
        stdout.WriteLine("Schemas: " + string.Join(", ", info.Schemas));
        return 0;
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
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay analyze <url-or-file> [--instruction <text>] [--frames <count>] [--run-id <id>]");
        var instruction = parsed.Get("instruction") ?? "Extract transcript and representative frames for later LLM analysis.";
        var frames = parsed.GetInt("frames", 7);
        var runId = parsed.Get("run-id");
        var request = CreateAnalyzeRequest(parsed, source, instruction, includeTranscript: true, frameCount: frames, runId);

        return RunPipelineAsync(request, stdout, cancellationToken);
    }

    private static Task<int> RunTranscribeAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay transcribe <url-or-file> [--run-id <id>]");
        var runId = parsed.Get("run-id");
        var request = CreateAnalyzeRequest(parsed, source, "Extract an existing caption transcript or transcribe audio.", includeTranscript: true, frameCount: 0, runId) with
        {
            ExtractAudio = parsed.GetBool("stt", defaultValue: false) || parsed.GetBool("audio", defaultValue: false),
            UseSpeechToText = parsed.GetBool("stt", defaultValue: false)
        };

        return RunPipelineAsync(request, stdout, cancellationToken);
    }

    private static Task<int> RunFramesAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var source = parsed.GetRequiredPositional("source", "Usage: zakira-replay frames <url-or-file> [--count <count>] [--run-id <id>]");
        var count = parsed.GetInt("count", parsed.GetInt("frames", 7));
        var runId = parsed.Get("run-id");
        var request = CreateAnalyzeRequest(parsed, source, "Extract representative video frames.", includeTranscript: false, frameCount: count, runId);

        return RunPipelineAsync(request, stdout, cancellationToken);
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
                var instruction = parsed.Get("instruction") ?? "Extract transcript and representative frames for later LLM analysis.";
                var frames = parsed.GetInt("frames", parsed.GetInt("count", 7));
                var request = CreateAnalyzeRequest(parsed, source, instruction, includeTranscript: !parsed.GetBool("no-transcript", defaultValue: false), frameCount: frames, parsed.Get("run-id"));
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
                var result = await installer.InstallAsync(targets, parsed.GetBool("force", defaultValue: false), progress, cancellationToken).ConfigureAwait(false);
                stdout.WriteLine();
                stdout.WriteLine("Dependency install complete.");
                stdout.WriteLine($"Portable directory: {result.PortableDirectory}");
                stdout.WriteLine($"ONNX model directory: {result.OnnxModelDirectory}");
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
                return 0;

            default:
                throw new ReplayException("Usage: zakira-replay deps <install|path> ...");
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
                stdout.WriteLine($"- {warning}");
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
        return new AnalysisPipeline(artifactStore, ytDlp, ffmpeg, provider => LlmProviderFactory.Create(provider));
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
        string instruction,
        bool includeTranscript,
        int frameCount,
        string? runId)
    {
        var useOcr = parsed.GetBool("ocr", defaultValue: false);
        var useVision = parsed.GetBool("vision", defaultValue: false);
        var useSummary = parsed.GetBool("summary", defaultValue: false);
        var useStt = parsed.GetBool("stt", defaultValue: false);
        var extractAudio = parsed.GetBool("audio", defaultValue: false) || useStt;
        var browserAuth = parsed.Get("browser-auth");
        var config = new ConfigStore().Load();
        var llmProvider = LlmProviderFactory.GetConfiguredProvider(config);
        llmProvider = LlmProviderFactory.Normalize(parsed.Get("llm-provider") ?? parsed.Get("provider") ?? llmProvider);
        return new AnalyzeRequest(
            Source: source,
            Instruction: instruction,
            IncludeTranscript: includeTranscript,
            FrameCount: frameCount,
            RunId: runId,
            ExtractAudio: extractAudio,
            UseSpeechToText: useStt,
            UseOcr: useOcr,
            UseVision: useVision,
            UseSummary: useSummary,
            MaxAiFrames: parsed.GetInt("max-ai-frames", 5),
            Model: parsed.Get("model") ?? LlmProviderFactory.GetDefaultModel(llmProvider, config),
            LlmProvider: llmProvider,
            Force: parsed.GetBool("force", defaultValue: false),
            UseCache: parsed.GetBool("cache", defaultValue: false),
            FrameStrategy: ResolveFrameStrategy(parsed),
            CookiesPath: parsed.Get("cookies"),
            CookiesFromBrowser: browserAuth ?? parsed.Get("cookies-from-browser"));
    }

    private static string ResolveFrameStrategy(CommandOptions parsed)
    {
        if (parsed.GetBool("every-frame", defaultValue: false) || parsed.GetBool("frame-by-frame", defaultValue: false))
        {
            return FrameSelectionStrategies.EveryFrame;
        }

        return parsed.Get("frame-strategy") ?? FrameSelectionStrategies.Interval;
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
        stdout.WriteLine("  zakira-replay analyze <url-or-file> [--instruction <text>] [--frames <count>] [--frame-strategy interval|scene|every-frame] [--llm-provider github-copilot|openai|azure-openai] [--stt] [--ocr] [--vision] [--summary] [--run-id <id>] [--cache] [--force]");
        stdout.WriteLine("  zakira-replay transcribe <url-or-file> [--stt] [--audio] [--run-id <id>] [--cache] [--force]");
        stdout.WriteLine("  zakira-replay frames <url-or-file> [--count <count>] [--frame-strategy interval|scene|every-frame] [--ocr] [--vision] [--run-id <id>] [--cache] [--force]");
        stdout.WriteLine("  zakira-replay clip <url-or-file> --start <timestamp> --end <timestamp> [--run-id <id>] [--output-name <name>]");
        stdout.WriteLine("  zakira-replay search build <run-directory> [--backend json|sqlite|sqlite-onnx]");
        stdout.WriteLine("  zakira-replay search query <run-directory-or-index> <query> [--top <n>] [--backend auto|json|sqlite|sqlite-onnx]");
        stdout.WriteLine("  zakira-replay chapters build <run-directory> [--min-duration <seconds>] [--max-duration <seconds>]");
        stdout.WriteLine("  zakira-replay discover <url> [--browser] [--output <path>]");
        stdout.WriteLine("  zakira-replay batch run <manifest.json>");
        stdout.WriteLine("  zakira-replay queue enqueue <url-or-file> [analysis options] [--queue-id <id>] [--job-id <id>] [--retries <n>]");
        stdout.WriteLine("  zakira-replay queue run [--queue-id <id>] [--concurrency <n>] [--retries <n>]");
        stdout.WriteLine("  zakira-replay queue status [--queue-id <id>] [--json]");
        stdout.WriteLine("  zakira-replay llm ask <prompt> [--llm-provider github-copilot|openai|azure-openai] [--model <model>] [--attach <path>]");
        stdout.WriteLine("  zakira-replay deps install [yt-dlp|ffmpeg|ffprobe|onnx|media|all] [--force]  # default: media");
        stdout.WriteLine("  zakira-replay deps path");
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

    public const string Version = "0.1.0";
}

internal sealed record ReplayInfo(
    string Name,
    string Version,
    string ConfigPath,
    string RunsDirectory,
    string LlmProvider,
    string DefaultModel,
    IReadOnlyList<string> Schemas);

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
