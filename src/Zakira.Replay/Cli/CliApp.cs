using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Cli;

/// <summary>
/// Entry point for the <c>zakira-replay</c> CLI. Built on
/// <see href="https://learn.microsoft.com/dotnet/standard/commandline/">System.CommandLine</see> 3.0
/// (the 0.8.x hand-rolled <c>CommandOptions</c> parser is retired).
///
/// The 0.9.0 surface is grouped <c>noun verb</c>:
/// <list type="bullet">
///   <item><c>runs list|show|delete|export</c> — first-class run inspection</item>
///   <item><c>index build|query</c> — replaces <c>search build|query</c></item>
///   <item><c>chapters build</c>, <c>align build</c></item>
///   <item><c>queue enqueue|run|status</c></item>
/// </list>
///
/// Global flags (recursive across every subcommand):
/// <list type="bullet">
///   <item><c>--output-format text|json|ndjson</c></item>
///   <item><c>--log-file &lt;path&gt;</c></item>
///   <item><c>--log-level info|debug|trace</c></item>
///   <item><c>--correlation-id &lt;string&gt;</c></item>
/// </list>
/// </summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var root = BuildRootCommand(stdout, stderr);
        var parseResult = root.Parse(args);
        return await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand BuildRootCommand(TextWriter stdout, TextWriter stderr)
    {
        var outputFormat = new Option<string>("--output-format")
        {
            Description = "Output format for command results: text (default), json, or ndjson.",
            DefaultValueFactory = _ => "text",
            Recursive = true
        };
        outputFormat.AcceptOnlyFromAmong("text", "json", "ndjson");

        var logFile = new Option<FileInfo?>("--log-file")
        {
            Description = "Optional path to write structured log output to. Stderr stays human-readable.",
            Recursive = true
        };

        var logLevel = new Option<string>("--log-level")
        {
            Description = "Minimum log level emitted (info, debug, trace).",
            DefaultValueFactory = _ => "info",
            Recursive = true
        };
        logLevel.AcceptOnlyFromAmong("info", "debug", "trace");

        var correlationId = new Option<string?>("--correlation-id")
        {
            Description = "Optional correlation id propagated to evidence and logs so agent runs can be cross-referenced.",
            Recursive = true
        };

        var verbose = new Option<bool>("--verbose", "-v")
        {
            Description = "Show all in-flight progress messages and info-severity warnings. Default mode suppresses both and only surfaces the final summary plus warning/error severities; quiet mode suppresses everything except errors.",
            Recursive = true
        };

        var quiet = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress all in-flight progress and all non-error output. Only error-severity warnings are emitted. Mutually exclusive with --verbose.",
            Recursive = true
        };

        // Expose the option references to the verbosity helpers so any sub-command's
        // SetAction can resolve verbosity via ParseResult without re-plumbing the references
        // through every BuildXxxCommand signature. Set once per process at root construction.
        CliVerbosityHelpers.VerboseOption = verbose;
        CliVerbosityHelpers.QuietOption = quiet;

        var root = new RootCommand("Zakira.Replay — turns video sources into agent-consumable evidence.")
        {
            outputFormat,
            logFile,
            logLevel,
            correlationId,
            verbose,
            quiet,

            BuildVersionCommand(stdout),
            BuildInfoCommand(stdout, outputFormat),
            BuildDoctorCommand(stdout, outputFormat),
            BuildAnalyzeCommand(stdout, stderr, outputFormat),
            BuildTranscribeCommand(stdout, stderr, outputFormat),
            BuildFramesCommand(stdout, stderr, outputFormat),
            BuildClipCommand(stdout, stderr, outputFormat),
            BuildDiscoverCommand(stdout),
            BuildBatchCommand(stdout, outputFormat),
            BuildRunsCommand(stdout, outputFormat),
            BuildChaptersCommand(stdout),
            BuildAlignCommand(stdout),
            BuildIndexCommand(stdout, outputFormat),
            BuildQueueCommand(stdout, stderr, outputFormat),
            BuildLlmCommand(stdout),
            BuildDepsCommand(stdout),
            BuildAuthCommand(stdout),
            BuildConfigCommand(stdout),
            BuildVisionCommand(stdout),
            BuildMcpCommand(stderr),
            BuildCompletionCommand(stdout)
        };

        return root;
    }

    // ---------------------------------------------------------------------------------------
    //  Standalone diagnostics
    // ---------------------------------------------------------------------------------------

    private static Command BuildVersionCommand(TextWriter stdout)
    {
        var command = new Command("version", "Prints the Zakira.Replay package version.");
        command.SetAction(_ =>
        {
            stdout.WriteLine(AppInfo.Version);
            return 0;
        });
        return command;
    }

    private static Command BuildInfoCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var command = new Command("info", "Prints the resolved configuration, dependency layout, and capability summary.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var info = await CliDiagnostics.BuildInfoAsync(cancellationToken).ConfigureAwait(false);
            if (IsJson(parseResult, outputFormat))
            {
                stdout.WriteLine(JsonSerializer.Serialize(info, CliJson.Options));
                return 0;
            }

            stdout.WriteLine($"{info.Name} {info.Version}");
            stdout.WriteLine($"Config: {info.ConfigPath}");
            stdout.WriteLine($"Runs: {info.RunsDirectory}");
            stdout.WriteLine($"LLM provider: {info.LlmProvider}");
            stdout.WriteLine($"Default model: {info.DefaultModel}");
            if (info.ResolvedDependencies is not null)
            {
                stdout.WriteLine($"OCR pack: {info.ResolvedDependencies.OcrLanguagePack}");
            }
            if (info.Capabilities is not null)
            {
                var cap = info.Capabilities;
                stdout.WriteLine($"Capabilities: local-ocr={cap.LocalOcrReady}, local-whisper={cap.LocalWhisperReady}, diarization={cap.DiarizationReady}, yt-dlp={cap.YtDlpAvailable}, ffmpeg={cap.FfmpegAvailable}");
            }
            stdout.WriteLine("Schemas: " + string.Join(", ", info.Schemas));
            return 0;
        });
        return command;
    }

    private static Command BuildDoctorCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var command = new Command("doctor", "Reports dependency availability without installing anything.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var report = await CliDiagnostics.BuildDoctorReportAsync(cancellationToken).ConfigureAwait(false);
            if (IsJson(parseResult, outputFormat))
            {
                stdout.WriteLine(JsonSerializer.Serialize(report, CliJson.Options));
                return 0;
            }

            foreach (var dep in report.Dependencies)
            {
                if (dep.IsFound)
                {
                    var source = string.IsNullOrWhiteSpace(dep.Source) ? string.Empty : $" via {dep.Source}";
                    stdout.WriteLine(dep.IsRunnable
                        ? $"{dep.Name}: found{source} ({dep.Path})"
                        : $"{dep.Name}: found but not runnable{source} ({dep.Path}) - {dep.RunnableError}");
                }
                else
                {
                    stdout.WriteLine($"{dep.Name}: missing ({dep.Message})");
                }
            }
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------------------------------
    //  Analysis: analyze, transcribe
    // ---------------------------------------------------------------------------------------

    private static Command BuildAnalyzeCommand(TextWriter stdout, TextWriter stderr, Option<string> outputFormat)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var preset = new Option<string?>("--preset")
        {
            Description = "Opinionated defaults bundle: meeting, lecture, demo, interview, raw."
        };
        preset.AcceptOnlyFromAmong("meeting", "lecture", "demo", "interview", "raw");

        var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
        var frames = new Option<int>("--frames") { Description = "Number of representative frames to extract. Default 15 (was 500 in 0.13.2 and earlier; lower default keeps cold runs fast and bandwidth-light).", DefaultValueFactory = _ => 15 };
        var runId = new Option<string?>("--run-id") { Description = "Optional run id for the artifact folder." };

        var command = new Command("analyze", "Runs the full Zakira.Replay analysis pipeline against a video source.")
        {
            source,
            preset,
            frames,
            runId
        };
        foreach (var option in analyzeOptions)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = applyAnalyzeOptions(parseResult, parseResult.GetValue(source)!, true, parseResult.GetValue(frames), parseResult.GetValue(runId));
            request = ApplyPreset(request, parseResult.GetValue(preset));
            return await RunPipelineAsync(request, stdout, stderr, IsJson(parseResult, outputFormat), CliVerbosityHelpers.Resolve(parseResult), cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTranscribeCommand(TextWriter stdout, TextWriter stderr, Option<string> outputFormat)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
        var runId = new Option<string?>("--run-id") { Description = "Optional run id." };

        var command = new Command("transcribe", "Runs analysis with frame extraction disabled (transcript-only).")
        {
            source,
            runId
        };
        foreach (var option in analyzeOptions)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = applyAnalyzeOptions(parseResult, parseResult.GetValue(source)!, true, 0, parseResult.GetValue(runId));
            return await RunPipelineAsync(request, stdout, stderr, IsJson(parseResult, outputFormat), CliVerbosityHelpers.Resolve(parseResult), cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    // ---------------------------------------------------------------------------------------
    //  Media: frames, clip, discover
    // ---------------------------------------------------------------------------------------

    private static Command BuildFramesCommand(TextWriter stdout, TextWriter stderr, Option<string> outputFormat)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var at = new Option<string?>("--at") { Description = "Comma-separated timestamps to capture." };
        var from = new Option<string?>("--from") { Description = "Window start (with --to)." };
        var to = new Option<string?>("--to") { Description = "Window end (with --from)." };
        var count = new Option<int?>("--count") { Description = "Frame count inside window." };
        var strategy = new Option<string?>("--strategy") { Description = "Window strategy: interval (default) or scene." };
        var maxEdge = new Option<int?>("--max-edge") { Description = "Resize: max long edge in pixels." };
        var quality = new Option<int?>("--quality") { Description = "JPEG quality (1-100)." };
        var phash = new Option<bool>("--phash") { Description = "Compute 64-bit perceptual hashes." };
        var sceneCap = new Option<int?>("--scene-safety-cap") { Description = "Override frames.sceneSafetyCap for this call." };
        var runId = new Option<string?>("--run-id") { Description = "Optional run id." };
        var cookies = new Option<string?>("--cookies") { Description = "Path to a Netscape cookies file for yt-dlp." };
        var browserAuth = new Option<string?>("--browser-auth") { Description = "Browser/profile spec for yt-dlp --cookies-from-browser." };
        var framesAllowDownload = new Option<bool>("--allow-media-download") { Description = "Opt in to downloading the source video locally when neither yt-dlp nor the browser inline-media probe can resolve a direct URL. Default off." };

        var command = new Command("frames", "Ad-hoc frame capture. Pass --at OR --from/--to (mutually exclusive).")
        {
            source, at, from, to, count, strategy, maxEdge, quality, phash, sceneCap, runId, cookies, browserAuth, framesAllowDownload
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var atValue = parseResult.GetValue(at);
            var fromValue = parseResult.GetValue(from);
            var toValue = parseResult.GetValue(to);
            var hasAt = !string.IsNullOrWhiteSpace(atValue);
            var hasWindow = !string.IsNullOrWhiteSpace(fromValue) || !string.IsNullOrWhiteSpace(toValue);
            if (hasAt && hasWindow)
            {
                throw new ReplayException("`--at` and `--from`/`--to` are mutually exclusive.");
            }

            IReadOnlyList<TimeSpan>? timestamps = null;
            TimeSpan? rangeStart = null;
            TimeSpan? rangeEnd = null;

            if (hasAt)
            {
                timestamps = FrameCaptureInput.ParseTimestamps(atValue!, "at");
            }
            else if (hasWindow)
            {
                if (string.IsNullOrWhiteSpace(fromValue) || string.IsNullOrWhiteSpace(toValue))
                {
                    throw new ReplayException("Window capture requires both `--from` and `--to`.");
                }
                rangeStart = Timestamp.ParseRequired(fromValue!, "from");
                rangeEnd = Timestamp.ParseRequired(toValue!, "to");
            }
            else
            {
                // No --at, no --from/--to: treat as the analyze frames-only path for back-compat.
                var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
                var request = applyAnalyzeOptions(parseResult, parseResult.GetValue(source)!, false, parseResult.GetValue(count) ?? 15, parseResult.GetValue(runId));
                _ = analyzeOptions; // not added to command; default values are used
                return await RunPipelineAsync(request, stdout, stderr, IsJson(parseResult, outputFormat), CliVerbosityHelpers.Resolve(parseResult), cancellationToken).ConfigureAwait(false);
            }

            var captureRequest = new FrameCaptureRequest(
                Source: parseResult.GetValue(source)!,
                Timestamps: timestamps,
                RangeStart: rangeStart,
                RangeEnd: rangeEnd,
                RangeCount: parseResult.GetValue(count),
                RangeStrategy: parseResult.GetValue(strategy) ?? FrameSelectionStrategies.Interval,
                RunId: parseResult.GetValue(runId),
                MaxLongEdgePixels: parseResult.GetValue(maxEdge),
                JpegQuality: parseResult.GetValue(quality),
                ComputePerceptualHash: parseResult.GetValue(phash),
                SceneSafetyCap: parseResult.GetValue(sceneCap),
                CookiesPath: parseResult.GetValue(cookies),
                CookiesFromBrowser: parseResult.GetValue(browserAuth),
                // Flag-OR-config: passing --allow-media-download opts in for this run; config
                // capture.allowMediaDownload=true opts in globally. Default off either way.
                AllowMediaDownload: parseResult.GetValue(framesAllowDownload) || new ConfigStore().Load().Capture.AllowMediaDownload);

            var service = CreateFrameCaptureService();
            var isJsonFrames = IsJson(parseResult, outputFormat);
            // Default + Quiet: suppress in-flight progress (the final summary is the meaningful
            // output). Verbose: stream like before. JSON mode: send progress to stderr so stdout
            // stays a single parseable envelope.
            var streamFrames = !isJsonFrames && CliVerbosityHelpers.ShouldStreamProgress(CliVerbosityHelpers.Resolve(parseResult));
            var progress = isJsonFrames
                ? new SynchronousProgress<string>(stderr.WriteLine)
                : (streamFrames ? new SynchronousProgress<string>(stdout.WriteLine) : new SynchronousProgress<string>(_ => { }));
            var result = await service.CaptureAsync(captureRequest, progress, cancellationToken).ConfigureAwait(false);

            if (IsJson(parseResult, outputFormat))
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

            var framesVerbosity = CliVerbosityHelpers.Resolve(parseResult);
            if (CliVerbosityHelpers.ShouldRenderSummary(framesVerbosity))
            {
                stdout.WriteLine();
                stdout.WriteLine($"Captured {result.Manifest.Frames.Count} frame(s) into {result.Run.Directory}.");
                foreach (var frame in result.Manifest.Frames)
                {
                    stdout.WriteLine($"  {frame.TimestampLabel}  {result.Run.GetPath(frame.Path)}");
                }
            }
            foreach (var warning in result.Manifest.Warnings)
            {
                if (!CliVerbosityHelpers.ShouldRender(framesVerbosity, warning.Severity)) continue;
                stdout.WriteLine($"[{warning.Severity}] {warning.Code}: {warning.Message}");
            }
            return 0;
        });
        return command;
    }

    private static Command BuildClipCommand(TextWriter stdout, TextWriter stderr, Option<string> outputFormat)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var start = new Option<string>("--start") { Description = "Clip start timestamp.", Required = true };
        var end = new Option<string>("--end") { Description = "Clip end timestamp.", Required = true };
        var runId = new Option<string?>("--run-id") { Description = "Optional run id." };
        var outputName = new Option<string?>("--output-name") { Description = "Optional output file name." };
        var cookies = new Option<string?>("--cookies") { Description = "Cookies file path." };
        var browserAuth = new Option<string?>("--browser-auth") { Description = "cookies-from-browser spec." };
        var clipAllowDownload = new Option<bool>("--allow-media-download") { Description = "Opt in to downloading the source video locally. Clip extraction always needs a download when no direct URL is reachable (no inline-media sidestep applies); without this flag the command fails with an actionable error." };

        var command = new Command("clip", "Extracts a timestamped clip from a video source.")
        {
            source, start, end, runId, outputName, cookies, browserAuth, clipAllowDownload
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var service = CreateClipService();
            var isJson = IsJson(parseResult, outputFormat);
            // Default + Quiet: suppress in-flight progress; Verbose: stream. JSON: route to stderr.
            var streamClip = !isJson && CliVerbosityHelpers.ShouldStreamProgress(CliVerbosityHelpers.Resolve(parseResult));
            var progress = isJson
                ? new SynchronousProgress<string>(stderr.WriteLine)
                : (streamClip ? new SynchronousProgress<string>(stdout.WriteLine) : new SynchronousProgress<string>(_ => { }));
            var result = await service.ExtractAsync(new ClipExtractionRequest(
                Source: parseResult.GetValue(source)!,
                Start: Timestamp.ParseRequired(parseResult.GetValue(start)!, "start"),
                End: Timestamp.ParseRequired(parseResult.GetValue(end)!, "end"),
                RunId: parseResult.GetValue(runId),
                OutputName: parseResult.GetValue(outputName),
                CookiesPath: parseResult.GetValue(cookies),
                CookiesFromBrowser: parseResult.GetValue(browserAuth),
                AllowMediaDownload: parseResult.GetValue(clipAllowDownload) || new ConfigStore().Load().Capture.AllowMediaDownload), progress, cancellationToken).ConfigureAwait(false);

            if (isJson)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    runId = result.Run.Id,
                    artifactDirectory = result.Run.Directory,
                    clipPath = result.Run.GetPath(result.Manifest.ClipPath),
                    relativeClipPath = result.Manifest.ClipPath,
                    startSeconds = result.Manifest.Start.TotalSeconds,
                    endSeconds = result.Manifest.End.TotalSeconds,
                    durationSeconds = (result.Manifest.End - result.Manifest.Start).TotalSeconds,
                    warnings = result.Manifest.Warnings
                }, CliJson.Options));
                return 0;
            }

            stdout.WriteLine();
            stdout.WriteLine($"Completed clip: {result.Run.Id}");
            stdout.WriteLine($"Artifacts: {result.Run.Directory}");
            stdout.WriteLine($"Clip: {result.Run.GetPath(result.Manifest.ClipPath)}");
            return 0;
        });
        return command;
    }

    private static Command BuildDiscoverCommand(TextWriter stdout)
    {
        var url = new Argument<string>("url") { Description = "Page URL to inspect." };
        var browser = new Option<bool>("--browser") { Description = "Use Edge/Playwright for dynamic page discovery." };
        var output = new Option<FileInfo?>("--output") { Description = "Optional path to write the discovery JSON to." };

        var command = new Command("discover", "Discovers candidate video URLs on a web page.")
        {
            url, browser, output
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dependencies = new DependencyResolver();
            var runner = new ProcessRunner();
            var discovery = new DiscoveryService(dependencies, runner);
            var result = await discovery.DiscoverAsync(parseResult.GetValue(url)!, parseResult.GetValue(browser), cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, CliJson.Options);

            var outputFile = parseResult.GetValue(output);
            if (outputFile is null)
            {
                stdout.WriteLine(json);
            }
            else
            {
                Directory.CreateDirectory(outputFile.DirectoryName!);
                await File.WriteAllTextAsync(outputFile.FullName, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                stdout.WriteLine($"Discovered {result.Sources.Count} source(s).");
                stdout.WriteLine($"Output: {outputFile.FullName}");
            }
            return 0;
        });
        return command;
    }

    private static Command BuildBatchCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var run = new Command("run", "Runs a JSON-defined batch of analyses.");
        var manifest = new Argument<FileInfo>("manifest") { Description = "Path to the batch manifest JSON file." };
        // Mirrors `queue run --concurrency`. Nullable so we can tell "not provided" (defer to the
        // manifest's `concurrency` field, or the default of 1) apart from an explicit value.
        var concurrency = new Option<int?>("--concurrency") { Description = "Concurrent items (overrides manifest 'concurrency'; default 1)." };
        run.Arguments.Add(manifest);
        run.Options.Add(concurrency);
        run.SetAction(async (parseResult, cancellationToken) =>
        {
            var runner = new BatchRunner(CreatePipeline);
            var isJson = IsJson(parseResult, outputFormat);
            // Default + Quiet: suppress per-item progress; Verbose: stream. JSON: suppress
            // (batch progress is free-form text; mixing it with the JSON envelope or log
            // channel is worse than just dropping it).
            var streamBatch = !isJson && CliVerbosityHelpers.ShouldStreamProgress(CliVerbosityHelpers.Resolve(parseResult));
            var progress = isJson
                ? new SynchronousProgress<string>(_ => { })
                : (streamBatch ? new SynchronousProgress<string>(stdout.WriteLine) : new SynchronousProgress<string>(_ => { }));
            var result = await runner.RunAsync(
                parseResult.GetValue(manifest)!.FullName,
                progress,
                cancellationToken,
                parseResult.GetValue(concurrency)).ConfigureAwait(false);
            var succeeded = result.Items.Count(item => item.Succeeded);
            var failed = result.Items.Count(item => !item.Succeeded);

            if (isJson)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    batchId = result.BatchId,
                    batchDirectory = result.BatchDirectory,
                    completedAt = result.CompletedAt,
                    succeeded,
                    failed,
                    total = result.Items.Count,
                    items = result.Items.Select(item => new
                    {
                        source = item.Source,
                        succeeded = item.Succeeded,
                        runId = item.RunId,
                        artifactDirectory = item.ArtifactDirectory,
                        error = item.Error
                    })
                }, CliJson.Options));
                return failed > 0 ? 1 : 0;
            }

            stdout.WriteLine();
            stdout.WriteLine($"Completed batch: {result.BatchId}");
            stdout.WriteLine($"Batch artifacts: {result.BatchDirectory}");
            stdout.WriteLine($"Succeeded: {succeeded}");
            stdout.WriteLine($"Failed: {failed}");
            return failed > 0 ? 1 : 0;
        });

        var command = new Command("batch", "Run a manifest-driven batch of analyses.") { run };
        return command;
    }

    // ---------------------------------------------------------------------------------------
    //  runs (new group): list, show, delete, export
    // ---------------------------------------------------------------------------------------

    private static Command BuildRunsCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var list = new Command("list", "Lists analysis runs under the runs/ directory.");
        list.SetAction(parseResult =>
        {
            var root = ResolveRunsRoot();
            if (!Directory.Exists(root))
            {
                stdout.WriteLine($"No runs directory: {root}");
                return 0;
            }
            var runs = Directory.EnumerateDirectories(root)
                .Where(dir => !Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal))
                .Select(dir => new
                {
                    id = Path.GetFileName(dir),
                    directory = dir,
                    manifestExists = File.Exists(Path.Combine(dir, "manifest.json")),
                    lastWriteUtc = Directory.GetLastWriteTimeUtc(dir)
                })
                .OrderByDescending(r => r.lastWriteUtc)
                .ToArray();

            if (IsJson(parseResult, outputFormat))
            {
                stdout.WriteLine(JsonSerializer.Serialize(new { runs }, CliJson.Options));
                return 0;
            }
            if (runs.Length == 0)
            {
                stdout.WriteLine("(no runs)");
                return 0;
            }
            foreach (var run in runs)
            {
                stdout.WriteLine($"{run.lastWriteUtc:u}  {run.id}  manifest={(run.manifestExists ? "yes" : "no")}");
            }
            return 0;
        });

        var show = new Command("show", "Prints a run's manifest and key paths.");
        var showId = new Argument<string>("id") { Description = "Run id." };
        show.Arguments.Add(showId);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(showId)!;
            var directory = Path.Combine(ResolveRunsRoot(), id);
            if (!Directory.Exists(directory))
            {
                throw new ReplayException($"Run '{id}' not found at {directory}.");
            }
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new ReplayException($"manifest.json missing in {directory}.");
            }
            var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (IsJson(parseResult, outputFormat))
            {
                stdout.WriteLine(manifestText);
                return 0;
            }
            stdout.WriteLine($"Run id: {id}");
            stdout.WriteLine($"Directory: {directory}");
            stdout.WriteLine($"Manifest: {manifestPath}");
            foreach (var artifact in new[] { "evidence.json", "transcript.md", "chapters/chapters.json", "evidence-aligned/by-chapter.json", "evidence-aligned/by-slide.json" })
            {
                var artifactPath = Path.Combine(directory, artifact.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(artifactPath))
                {
                    stdout.WriteLine($"  {artifact}");
                }
            }
            return 0;
        });

        var delete = new Command("delete", "Deletes a run directory.");
        var deleteId = new Argument<string>("id") { Description = "Run id." };
        var force = new Option<bool>("--force") { Description = "Delete without confirmation." };
        delete.Arguments.Add(deleteId);
        delete.Options.Add(force);
        delete.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(deleteId)!;
            var directory = Path.Combine(ResolveRunsRoot(), id);
            if (!Directory.Exists(directory))
            {
                throw new ReplayException($"Run '{id}' not found at {directory}.");
            }
            if (!parseResult.GetValue(force))
            {
                throw new ReplayException("Re-run with --force to confirm deletion.");
            }
            Directory.Delete(directory, recursive: true);
            stdout.WriteLine($"Deleted {directory}");
            return 0;
        });

        var export = new Command("export", "Exports a run's transcript and evidence in a portable form.");
        var exportId = new Argument<string>("id") { Description = "Run id." };
        var format = new Option<string>("--format") { Description = "Export format.", DefaultValueFactory = _ => "md" };
        format.AcceptOnlyFromAmong("md", "jsonl");
        export.Arguments.Add(exportId);
        export.Options.Add(format);
        export.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(exportId)!;
            var directory = Path.Combine(ResolveRunsRoot(), id);
            var formatValue = parseResult.GetValue(format)!;
            if (!Directory.Exists(directory))
            {
                throw new ReplayException($"Run '{id}' not found at {directory}.");
            }

            if (formatValue == "md")
            {
                var transcriptPath = Path.Combine(directory, "transcript.md");
                if (!File.Exists(transcriptPath))
                {
                    throw new ReplayException($"transcript.md missing in {directory}.");
                }
                stdout.WriteLine(await File.ReadAllTextAsync(transcriptPath, cancellationToken).ConfigureAwait(false));
                return 0;
            }

            // jsonl: emit one JSON object per transcript segment with run id baked in.
            var evidencePath = Path.Combine(directory, "evidence.json");
            if (!File.Exists(evidencePath))
            {
                throw new ReplayException($"evidence.json missing in {directory}.");
            }
            var evidence = JsonSerializer.Deserialize<EvidenceDocument>(
                await File.ReadAllTextAsync(evidencePath, cancellationToken).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (evidence is null)
            {
                throw new ReplayException($"Could not parse {evidencePath}.");
            }
            foreach (var segment in evidence.Transcript)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    runId = id,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Timestamp,
                    segment.Text
                }));
            }
            return 0;
        });

        return new Command("runs", "Inspect, export, or delete analysis runs.") { list, show, delete, export };
    }

    // ---------------------------------------------------------------------------------------
    //  chapters, align (now both follow `chapters build`, `align build`)
    // ---------------------------------------------------------------------------------------

    private static Command BuildChaptersCommand(TextWriter stdout)
    {
        var build = new Command("build", "Builds deterministic transcript-based chapters for a completed run.");
        var runDirectory = new Argument<string>("run-directory") { Description = "Completed run directory containing transcript evidence." };
        var minDuration = new Option<double>("--min-duration") { Description = "Minimum chapter duration in seconds.", DefaultValueFactory = _ => 60 };
        var maxDuration = new Option<double>("--max-duration") { Description = "Maximum chapter duration in seconds.", DefaultValueFactory = _ => 600 };
        build.Arguments.Add(runDirectory);
        build.Options.Add(minDuration);
        build.Options.Add(maxDuration);
        build.SetAction(async (parseResult, cancellationToken) =>
        {
            var result = await new ChapterBuilder().BuildAsync(
                Path.GetFullPath(parseResult.GetValue(runDirectory)!),
                new ChapterBuildOptions(
                    MinDurationSeconds: parseResult.GetValue(minDuration),
                    MaxDurationSeconds: parseResult.GetValue(maxDuration)),
                cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Built {result.ChapterCount} chapter(s).");
            stdout.WriteLine($"Chapters: {result.JsonPath}");
            stdout.WriteLine($"Markdown: {result.MarkdownPath}");
            return 0;
        });

        return new Command("chapters", "Chapter-related commands.") { build };
    }

    private static Command BuildAlignCommand(TextWriter stdout)
    {
        var build = new Command("build", "Builds cross-modal evidence alignment views over a completed run.");
        var runDirectory = new Argument<string>("run-directory") { Description = "Completed run directory." };
        build.Arguments.Add(runDirectory);
        build.SetAction(async (parseResult, cancellationToken) =>
        {
            var result = await new EvidenceAlignmentService().BuildAsync(
                Path.GetFullPath(parseResult.GetValue(runDirectory)!),
                new EvidenceAlignmentOptions(),
                cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Aligned evidence for run {result.RunId}.");
            stdout.WriteLine($"By chapter: {result.ByChapterPath} ({result.ByChapter.Chapters.Count} chapter(s))");
            stdout.WriteLine($"By slide: {result.BySlidePath} ({result.BySlide.Slides.Count} slide(s))");
            if (!result.ChaptersLoaded)
            {
                stdout.WriteLine("Note: chapters/chapters.json was not found; by-chapter view is empty.");
            }
            return 0;
        });

        return new Command("align", "Evidence alignment commands.") { build };
    }

    // ---------------------------------------------------------------------------------------
    //  index (replaces search): build, query
    // ---------------------------------------------------------------------------------------

    private static Command BuildIndexCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var backend = new Option<string?>("--backend") { Description = "Search backend." };
        // 0.10.0: `--onnx-model` is now a known-id selector (bge-small-en-v1.5 etc.). The
        // historical path-style flag is renamed to `--onnx-model-path` to keep the surface
        // unambiguous. `--onnx-vocab` is kept as an alias for `--onnx-tokenizer-path` so
        // existing scripts pointing at vocab.txt still work for BERT-family models.
        var onnxModel = new Option<string?>("--onnx-model")
        {
            Description = "Search-embedding model id. Known: " + string.Join(", ", KnownSearchEmbeddingModels.Ids) + ". Defaults to " + KnownSearchEmbeddingModels.DefaultModel + "."
        };
        var onnxModelKind = new Option<string?>("--onnx-model-kind") { Description = "Embedding scheme override: bert, bge, or e5. Auto-derived from --onnx-model when omitted." };
        var onnxModelPath = new Option<string?>("--onnx-model-path") { Description = "Explicit path to the ONNX model file (overrides --onnx-model)." };
        var onnxTokenizerPath = new Option<string?>("--onnx-tokenizer-path") { Description = "Explicit path to the tokenizer file (vocab.txt for BERT, sentencepiece.bpe.model for XLM-R)." };
        var onnxVocab = new Option<string?>("--onnx-vocab") { Description = "Alias for --onnx-tokenizer-path; preserved for 0.9.x compatibility." };
        var onnxMaxSequenceLength = new Option<int?>("--onnx-max-sequence-length") { Description = "ONNX tokenizer sequence length." };
        var embeddingDimensions = new Option<int?>("--embedding-dimensions") { Description = "Embedding dimension hint." };

        var build = new Command("build", "Builds a search index over a completed run's evidence.json.");
        var buildRunDirectory = new Argument<string>("run-directory") { Description = "Completed run directory." };
        build.Arguments.Add(buildRunDirectory);
        build.Options.Add(backend);
        build.Options.Add(onnxModel);
        build.Options.Add(onnxModelKind);
        build.Options.Add(onnxModelPath);
        build.Options.Add(onnxTokenizerPath);
        build.Options.Add(onnxVocab);
        build.Options.Add(onnxMaxSequenceLength);
        build.Options.Add(embeddingDimensions);
        build.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new SearchIndexBuildOptions(
                Backend: parseResult.GetValue(backend) ?? SearchBackends.Json,
                OnnxModelPath: parseResult.GetValue(onnxModelPath),
                OnnxVocabularyPath: parseResult.GetValue(onnxVocab),
                OnnxTokenizerPath: parseResult.GetValue(onnxTokenizerPath),
                OnnxMaxSequenceLength: parseResult.GetValue(onnxMaxSequenceLength),
                EmbeddingDimensions: parseResult.GetValue(embeddingDimensions),
                OnnxModel: parseResult.GetValue(onnxModel),
                OnnxModelKind: parseResult.GetValue(onnxModelKind));
            var result = await new SearchIndexService().BuildAsync(
                Path.GetFullPath(parseResult.GetValue(buildRunDirectory)!),
                options,
                cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Indexed {result.DocumentCount} document(s) with {result.Backend} backend.");
            if (!string.IsNullOrWhiteSpace(result.EmbeddingModel))
            {
                stdout.WriteLine($"Embedding model: {result.EmbeddingModel} ({result.EmbeddingModelKind}, {result.EmbeddingDimensions}d)");
            }
            stdout.WriteLine($"Index: {result.IndexPath}");
            return 0;
        });

        var query = new Command("query", "Queries a search index or run directory.");
        var queryTarget = new Argument<string>("target") { Description = "Run directory, index file path, or registered conference id." };
        var queryString = new Argument<string>("query") { Description = "Query string." };
        var top = new Option<int>("--top") { Description = "Number of matches.", DefaultValueFactory = _ => 5 };
        query.Arguments.Add(queryTarget);
        query.Arguments.Add(queryString);
        query.Options.Add(top);
        query.Options.Add(backend);
        query.Options.Add(onnxModel);
        query.Options.Add(onnxModelKind);
        query.Options.Add(onnxModelPath);
        query.Options.Add(onnxTokenizerPath);
        query.Options.Add(onnxVocab);
        query.Options.Add(onnxMaxSequenceLength);
        query.Options.Add(embeddingDimensions);
        query.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new SearchIndexQueryOptions(
                Backend: parseResult.GetValue(backend) ?? SearchBackends.Auto,
                OnnxModelPath: parseResult.GetValue(onnxModelPath),
                OnnxVocabularyPath: parseResult.GetValue(onnxVocab),
                OnnxTokenizerPath: parseResult.GetValue(onnxTokenizerPath),
                OnnxMaxSequenceLength: parseResult.GetValue(onnxMaxSequenceLength),
                EmbeddingDimensions: parseResult.GetValue(embeddingDimensions),
                OnnxModel: parseResult.GetValue(onnxModel),
                OnnxModelKind: parseResult.GetValue(onnxModelKind));
            // Polymorphic target: literal file path, run directory (search/index.{json|sqlite}),
            // or a conference id under <runs-root>/.indexes/<slug>/index.json. Fall back to the
            // raw argument so the existing error path still fires for plain bad inputs.
            var rawTarget = parseResult.GetValue(queryTarget)!;
            var resolved = SearchIndexService.ResolveQueryTarget(rawTarget, ArtifactStore.GetDefaultRootDirectory())
                ?? Path.GetFullPath(rawTarget);
            var result = await new SearchIndexService().QueryAsync(
                resolved,
                parseResult.GetValue(queryString)!,
                parseResult.GetValue(top),
                options,
                cancellationToken).ConfigureAwait(false);
            stdout.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
            return 0;
        });

        // ----- index build-conference ------------------------------------------------------
        var buildConference = new Command("build-conference", "Aggregates evidence.json from multiple runs into one searchable conference index.");
        var conferenceId = new Argument<string>("conference-id") { Description = "Stable id for the conference index (e.g. 'build-2026'). Slugified on disk." };
        var conferenceRuns = new Option<string>("--runs") { Description = "Comma- or semicolon-separated list of run directory paths or globs. Use 'runs/*' to ingest every run under the default artifact root.", Required = true };
        buildConference.Arguments.Add(conferenceId);
        buildConference.Options.Add(conferenceRuns);
        buildConference.SetAction(async (parseResult, cancellationToken) =>
        {
            var raw = parseResult.GetValue(conferenceRuns)!;
            var artifactRoot = ArtifactStore.GetDefaultRootDirectory();
            var runDirs = ExpandRunDirectories(raw, artifactRoot);
            if (runDirs.Count == 0)
            {
                stdout.WriteLine($"No run directories matched '--runs {raw}'.");
                return 1;
            }

            var result = await new SearchIndexService().BuildConferenceAsync(
                parseResult.GetValue(conferenceId)!,
                runDirs,
                artifactRoot,
                cancellationToken).ConfigureAwait(false);

            stdout.WriteLine($"Conference index: {result.ConferenceId}");
            stdout.WriteLine($"Ingested {result.IncludedRuns.Count} run(s), {result.DocumentCount} documents.");
            if (result.Skipped.Count > 0)
            {
                stdout.WriteLine($"Skipped {result.Skipped.Count} run(s):");
                foreach (var s in result.Skipped)
                {
                    stdout.WriteLine($"  - {s.RunDirectory}: {s.Reason}");
                }
            }
            stdout.WriteLine($"Index: {result.IndexPath}");
            return 0;
        });

        return new Command("index", "Search index commands (replaces 0.8's `search` group).") { build, query, buildConference };
    }

    /// <summary>
    /// Expand a comma/semicolon-separated list of run-directory paths or globs into the set of
    /// matching, on-disk directories. Bare names (no glob, no separator) are resolved against
    /// <paramref name="artifactRoot"/> as a convenience so users can write
    /// <c>--runs key01,brk101</c> instead of full paths.
    /// </summary>
    private static IReadOnlyList<string> ExpandRunDirectories(string raw, string artifactRoot)
    {
        var parts = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            foreach (var dir in ExpandOne(part, artifactRoot))
            {
                var full = Path.GetFullPath(dir);
                if (seen.Add(full)) result.Add(full);
            }
        }
        return result;
    }

    private static IEnumerable<string> ExpandOne(string spec, string artifactRoot)
    {
        // Glob support is intentionally minimal: any '*' or '?' in the spec triggers a
        // Directory.GetDirectories search rooted at the parent (or artifactRoot when relative).
        if (spec.Contains('*') || spec.Contains('?'))
        {
            var (root, pattern) = SplitGlobRoot(spec, artifactRoot);
            if (!Directory.Exists(root)) yield break;
            foreach (var dir in Directory.EnumerateDirectories(root, pattern))
            {
                yield return dir;
            }
            yield break;
        }

        if (Directory.Exists(spec)) { yield return spec; yield break; }

        var resolved = Path.Combine(artifactRoot, spec);
        if (Directory.Exists(resolved)) yield return resolved;
    }

    private static (string Root, string Pattern) SplitGlobRoot(string spec, string artifactRoot)
    {
        var root = Path.GetDirectoryName(spec);
        var pattern = Path.GetFileName(spec);
        if (string.IsNullOrEmpty(root))
        {
            return (artifactRoot, pattern);
        }
        return (root!, pattern);
    }

    // ---------------------------------------------------------------------------------------
    //  queue: enqueue, run, status
    // ---------------------------------------------------------------------------------------

    private static Command BuildQueueCommand(TextWriter stdout, TextWriter stderr, Option<string> outputFormat)
    {
        var queueId = new Option<string?>("--queue-id") { Description = "Persistent queue id. Defaults to 'default'." };

        var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
        var enqueue = new Command("enqueue", "Adds an analysis request to a persistent queue.");
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var jobId = new Option<string?>("--job-id") { Description = "Optional stable job id." };
        var retries = new Option<int>("--retries") { Description = "Retry count.", DefaultValueFactory = _ => 0 };
        var noTranscript = new Option<bool>("--no-transcript") { Description = "Skip transcript extraction." };
        var frames = new Option<int>("--frames") { Description = "Frame count. Default 15.", DefaultValueFactory = _ => 15 };
        var runId = new Option<string?>("--run-id") { Description = "Optional run id." };

        enqueue.Arguments.Add(source);
        enqueue.Options.Add(queueId);
        enqueue.Options.Add(jobId);
        enqueue.Options.Add(retries);
        enqueue.Options.Add(noTranscript);
        enqueue.Options.Add(frames);
        enqueue.Options.Add(runId);
        foreach (var option in analyzeOptions)
        {
            enqueue.Options.Add(option);
        }
        enqueue.SetAction(async (parseResult, cancellationToken) =>
        {
            var queue = new AnalysisQueue(CreatePipeline);
            var request = applyAnalyzeOptions(parseResult, parseResult.GetValue(source)!, !parseResult.GetValue(noTranscript), parseResult.GetValue(frames), parseResult.GetValue(runId));
            var result = await queue.EnqueueAsync(
                parseResult.GetValue(queueId),
                request,
                parseResult.GetValue(jobId),
                parseResult.GetValue(retries),
                cancellationToken).ConfigureAwait(false);

            if (IsJson(parseResult, outputFormat))
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    queueId = result.QueueId,
                    jobId = result.JobId,
                    queueDirectory = result.QueueDirectory,
                    job = result.Job
                }, CliJson.Options));
                return 0;
            }

            stdout.WriteLine($"Enqueued job: {result.JobId}");
            stdout.WriteLine($"Queue: {result.QueueId}");
            stdout.WriteLine($"Queue directory: {result.QueueDirectory}");
            return 0;
        });

        var concurrency = new Option<int>("--concurrency") { Description = "Concurrent jobs.", DefaultValueFactory = _ => 1 };
        var runRetries = new Option<int>("--retries") { Description = "Retry count.", DefaultValueFactory = _ => 0 };
        var run = new Command("run", "Runs pending queue jobs.")
        {
            queueId, concurrency, runRetries
        };
        run.SetAction(async (parseResult, cancellationToken) =>
        {
            var queue = new AnalysisQueue(CreatePipeline);
            var isJson = IsJson(parseResult, outputFormat);
            // Default + Quiet: suppress per-job progress; Verbose: stream. JSON: route to stderr.
            var streamQueue = !isJson && CliVerbosityHelpers.ShouldStreamProgress(CliVerbosityHelpers.Resolve(parseResult));
            var progress = isJson
                ? new SynchronousProgress<string>(stderr.WriteLine)
                : (streamQueue ? new SynchronousProgress<string>(stdout.WriteLine) : new SynchronousProgress<string>(_ => { }));
            var result = await queue.RunAsync(
                parseResult.GetValue(queueId),
                new AnalysisQueueRunOptions(
                    Concurrency: parseResult.GetValue(concurrency),
                    Retries: parseResult.GetValue(runRetries)),
                progress,
                cancellationToken).ConfigureAwait(false);

            if (isJson)
            {
                stdout.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
                return result.Failed > 0 ? 1 : 0;
            }

            stdout.WriteLine();
            stdout.WriteLine($"Completed queue run: {result.QueueId}");
            stdout.WriteLine($"Queue directory: {result.QueueDirectory}");
            stdout.WriteLine($"Attempted: {result.Attempted}");
            stdout.WriteLine($"Succeeded: {result.Succeeded}");
            stdout.WriteLine($"Failed: {result.Failed}");
            stdout.WriteLine($"Pending: {result.Pending}");
            return result.Failed > 0 ? 1 : 0;
        });

        var status = new Command("status", "Reports persistent queue status.") { queueId };
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var queue = new AnalysisQueue(CreatePipeline);
            var state = await queue.GetStatusAsync(parseResult.GetValue(queueId), cancellationToken).ConfigureAwait(false);
            if (IsJson(parseResult, outputFormat))
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
        });

        return new Command("queue", "Persistent analysis queue.") { enqueue, run, status };
    }

    // ---------------------------------------------------------------------------------------
    //  llm (chat replaces ask)
    // ---------------------------------------------------------------------------------------

    private static Command BuildLlmCommand(TextWriter stdout)
    {
        var chat = new Command("chat", "Sends a one-shot prompt to the configured LLM provider.");
        var prompt = new Argument<string>("prompt") { Description = "Prompt text." };
        var model = new Option<string?>("--model") { Description = "Provider model id." };
        var attach = new Option<FileInfo?>("--attach") { Description = "Optional file to attach." };
        var provider = new Option<string?>("--llm-provider") { Description = "LLM provider name." };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { Description = "Timeout for the call.", DefaultValueFactory = _ => 180 };
        chat.Arguments.Add(prompt);
        chat.Options.Add(model);
        chat.Options.Add(attach);
        chat.Options.Add(provider);
        chat.Options.Add(timeoutSeconds);
        chat.SetAction(async (parseResult, cancellationToken) =>
        {
            var providerName = parseResult.GetValue(provider);
            var providerInstance = LlmProviderFactory.Create(providerName);
            var attachment = parseResult.GetValue(attach);
            var response = await providerInstance.CompleteAsync(new LlmRequest(
                Prompt: parseResult.GetValue(prompt)!,
                AttachmentPaths: attachment is null ? [] : [attachment.FullName],
                Model: parseResult.GetValue(model) ?? LlmProviderFactory.GetDefaultModel(providerName),
                SystemMessage: "You are a concise assistant used by Zakira.Replay smoke tests.",
                Timeout: TimeSpan.FromSeconds(parseResult.GetValue(timeoutSeconds))), cancellationToken).ConfigureAwait(false);
            stdout.WriteLine(response);
            return 0;
        });

        return new Command("llm", "LLM diagnostics (smoke-test the configured provider).") { chat };
    }

    // ---------------------------------------------------------------------------------------
    //  deps: install, status (status replaces `paths`)
    // ---------------------------------------------------------------------------------------

    private static Command BuildDepsCommand(TextWriter stdout)
    {
        var install = new Command("install", "Installs portable dependencies (yt-dlp, ffmpeg, models, …).");
        var targets = new Argument<string[]>("targets") { Description = "One or more targets (default: media)." };
        var whisperModel = new Option<string?>("--whisper-model") { Description = "Whisper model size." };
        var language = new Option<string?>("--language") { Description = "OCR language pack." };
        var mode = new Option<string?>("--mode") { Description = "Local vision mode." };
        // 0.10.0: which search-embedding model `deps install onnx` should download. Known
        // ids: bge-small-en-v1.5 (default), snowflake-arctic-embed-s, multilingual-e5-small.
        // When omitted the installer consults search.onnx.model from the user config so
        // users can switch the default once and forget. Only consumed by the onnx target.
        var searchModel = new Option<string?>("--model")
        {
            Description = "Search-embedding model id for the onnx target. Known: " + string.Join(", ", KnownSearchEmbeddingModels.Ids) + ". Defaults to the configured search.onnx.model."
        };
        var force = new Option<bool>("--force") { Description = "Re-download even if already installed." };
        targets.Arity = ArgumentArity.ZeroOrMore;
        install.Arguments.Add(targets);
        install.Options.Add(whisperModel);
        install.Options.Add(language);
        install.Options.Add(mode);
        install.Options.Add(searchModel);
        install.Options.Add(force);
        install.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            // Apply the per-call search-model override before constructing the installer so
            // Layout.OnnxModelDirectory and InstallOnnxAsync both see the chosen model id.
            var searchModelOverride = parseResult.GetValue(searchModel);
            if (!string.IsNullOrWhiteSpace(searchModelOverride))
            {
                config.Search.Onnx.Model = searchModelOverride;
            }
            var installer = new PortableDependencyInstaller(config);
            var resolvedTargets = parseResult.GetValue(targets);
            var list = resolvedTargets is null || resolvedTargets.Length == 0 ? new[] { "media" } : resolvedTargets;
            var progress = new SynchronousProgress<string>(stdout.WriteLine);
            var result = await installer.InstallAsync(
                list,
                parseResult.GetValue(force),
                progress,
                cancellationToken,
                parseResult.GetValue(whisperModel),
                parseResult.GetValue(language),
                parseResult.GetValue(mode)).ConfigureAwait(false);
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
        });

        var status = new Command("status", "Reports portable dependency paths and resolved binaries.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var installer = new PortableDependencyInstaller(config);
            stdout.WriteLine($"Portable directory: {installer.Layout.PortableDirectory}");
            stdout.WriteLine($"yt-dlp: {installer.GetPortableExecutablePath(PortableDependencyInstaller.YtDlp)}");
            stdout.WriteLine($"ffmpeg: {installer.GetPortableExecutablePath(PortableDependencyInstaller.Ffmpeg)}");
            stdout.WriteLine($"ffprobe: {installer.GetPortableExecutablePath(PortableDependencyInstaller.Ffprobe)}");
            var activeSearchModel = installer.GetActiveSearchEmbeddingModel();
            stdout.WriteLine($"Search embedding model: {activeSearchModel?.Id ?? (config.Search.Onnx.Model ?? "<custom>")} (kind={activeSearchModel?.ModelKind.ToString().ToLowerInvariant() ?? "?"})");
            stdout.WriteLine($"ONNX model directory: {installer.Layout.OnnxModelDirectory}");
            stdout.WriteLine($"ONNX model: {installer.GetOnnxModelPath()}");
            stdout.WriteLine($"ONNX tokenizer: {installer.GetOnnxTokenizerPath()}");
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
        });

        return new Command("deps", "Manage Zakira.Replay portable dependencies.") { install, status };
    }

    // ---------------------------------------------------------------------------------------
    //  auth: login, init-edge-profile, list, show, clear, path
    // ---------------------------------------------------------------------------------------

    private static Command BuildAuthCommand(TextWriter stdout)
    {
        var login = new Command("login", "Creates / refreshes a Playwright auth profile.");
        var profileName = new Argument<string>("profile-name") { Description = "Auth profile name." };
        var url = new Option<string?>("--url") { Description = "Optional start URL." };
        login.Arguments.Add(profileName);
        login.Options.Add(url);
        login.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new AuthProfileStore(config, store.ConfigPath);
            var loginService = new AuthProfileLoginService(new DependencyResolver(config), profiles);
            var result = await loginService.RunAsync(
                new AuthLoginRequest(parseResult.GetValue(profileName)!, parseResult.GetValue(url)),
                Console.In,
                stdout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Saved)
            {
                return 1;
            }
            stdout.WriteLine($"Profile slug: {AuthProfileStore.SlugifyProfileName(parseResult.GetValue(profileName)!)}");
            return 0;
        });

        var initEdge = new Command("init-edge-profile", "Initialises the dedicated Edge profile for browser capture.");
        var initUrl = new Option<string?>("--url") { Description = "Optional start URL." };
        var userDataDir = new Option<string?>("--user-data-dir") { Description = "Override Edge user-data-dir." };
        var profileDir = new Option<string?>("--profile-directory") { Description = "Override Edge profile directory." };
        initEdge.Options.Add(initUrl);
        initEdge.Options.Add(userDataDir);
        initEdge.Options.Add(profileDir);
        initEdge.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var resolvedUserDir = parseResult.GetValue(userDataDir);
            var resolvedProfileDir = parseResult.GetValue(profileDir);
            var userDir = string.IsNullOrWhiteSpace(resolvedUserDir)
                ? config.Capture.Browser.ResolveEdgeUserDataDir()
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(resolvedUserDir));
            var profile = string.IsNullOrWhiteSpace(resolvedProfileDir)
                ? config.Capture.Browser.ResolveEdgeProfileDirectory()
                : resolvedProfileDir;
            var initService = new EdgeProfileInitService(new DependencyResolver(config));
            var result = await initService.RunAsync(
                new EdgeProfileInitRequest(userDir, profile, parseResult.GetValue(initUrl)),
                stdout,
                cancellationToken).ConfigureAwait(false);
            return result.Initialized ? 0 : 1;
        });

        var list = new Command("list", "Lists saved Playwright auth profiles.");
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new AuthProfileStore(config, store.ConfigPath);
            var listed = profiles.List();
            if (listed.Count == 0)
            {
                stdout.WriteLine($"No auth profiles in {profiles.Directory}.");
                stdout.WriteLine("Create one with: zakira-replay auth login <profile-name>");
                return 0;
            }
            stdout.WriteLine($"Auth directory: {profiles.Directory}");
            stdout.WriteLine("Profile (slug)              Age      Stale  Bytes  Path");
            foreach (var profile in listed)
            {
                var stale = profile.IsStale ? "yes" : "no";
                stdout.WriteLine($"{profile.Slug,-26}  {profile.FormatAge(),-7}  {stale,-5}  {profile.ByteCount,-6}  {profile.Path}");
            }
            return 0;
        });

        var show = new Command("show", "Shows details of a single auth profile.");
        var showName = new Argument<string>("profile-name") { Description = "Profile name." };
        show.Arguments.Add(showName);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new AuthProfileStore(config, store.ConfigPath);
            var name = parseResult.GetValue(showName)!;
            var profile = profiles.TryRead(name);
            if (profile is null)
            {
                stdout.WriteLine($"Profile '{name}' not found.");
                stdout.WriteLine($"Expected at: {profiles.GetProfilePath(name)}");
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
        });

        var clear = new Command("clear", "Removes a stored auth profile.");
        var clearName = new Argument<string>("profile-name") { Description = "Profile name." };
        clear.Arguments.Add(clearName);
        clear.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new AuthProfileStore(config, store.ConfigPath);
            var name = parseResult.GetValue(clearName)!;
            if (profiles.Clear(name))
            {
                stdout.WriteLine($"Removed auth profile: {AuthProfileStore.SlugifyProfileName(name)}");
                return 0;
            }
            stdout.WriteLine($"Profile '{name}' did not exist.");
            return 1;
        });

        var path = new Command("path", "Prints the directory or a profile's path.");
        var pathName = new Argument<string?>("profile-name") { Description = "Optional profile name." };
        pathName.Arity = ArgumentArity.ZeroOrOne;
        path.Arguments.Add(pathName);
        path.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new AuthProfileStore(config, store.ConfigPath);
            var name = parseResult.GetValue(pathName);
            stdout.WriteLine(string.IsNullOrEmpty(name) ? profiles.Directory : profiles.GetProfilePath(name));
            return 0;
        });

        return new Command("auth", "Manage Playwright auth profiles for browser capture.") { login, initEdge, list, show, clear, path };
    }

    // ---------------------------------------------------------------------------------------
    //  config: path, list, get, set
    // ---------------------------------------------------------------------------------------

    private static Command BuildConfigCommand(TextWriter stdout)
    {
        var path = new Command("path", "Prints the resolved config file path.");
        path.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            stdout.WriteLine(store.ConfigPath);
            return 0;
        });

        var list = new Command("list", "Prints all config key=value pairs.");
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var config = await store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in ConfigStore.ToFlatDictionary(config))
            {
                stdout.WriteLine($"{item.Key}={item.Value}");
            }
            return 0;
        });

        var get = new Command("get", "Prints the value for a config key.");
        var key = new Argument<string>("key") { Description = "Config key path." };
        get.Arguments.Add(key);
        get.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            stdout.WriteLine(await store.GetAsync(parseResult.GetValue(key)!, cancellationToken).ConfigureAwait(false));
            return 0;
        });

        var set = new Command("set", "Sets a config key.");
        var setKey = new Argument<string>("key") { Description = "Config key path." };
        var setValue = new Argument<string>("value") { Description = "Value." };
        set.Arguments.Add(setKey);
        set.Arguments.Add(setValue);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new ConfigStore();
            var keyValue = parseResult.GetValue(setKey)!;
            var valueValue = parseResult.GetValue(setValue)!;
            await store.SetAsync(keyValue, valueValue, cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Set {keyValue}={await store.GetAsync(keyValue, cancellationToken).ConfigureAwait(false)}");
            stdout.WriteLine($"Config: {store.ConfigPath}");
            return 0;
        });

        return new Command("config", "Read / write Zakira.Replay configuration.") { path, list, get, set };
    }

    // ---------------------------------------------------------------------------------------
    //  vision: generate-clip-embeddings (single advanced sub-command)
    // ---------------------------------------------------------------------------------------

    private static Command BuildVisionCommand(TextWriter stdout)
    {
        var generate = new Command("generate-clip-embeddings", "Generates CLIP zero-shot embeddings for the local vision provider.");
        // Delegate to the existing implementation by re-parsing raw args; details live in
        // VisionGenerateClipEmbeddingsCommand which has its own option set.
        var rest = new Argument<string[]>("args") { Description = "Forwarded args." };
        rest.Arity = ArgumentArity.ZeroOrMore;
        generate.Arguments.Add(rest);
        generate.SetAction(async (parseResult, cancellationToken) =>
        {
            var forwarded = parseResult.GetValue(rest) ?? [];
            return await VisionGenerateClipEmbeddingsCommand.RunAsync(forwarded, stdout, cancellationToken).ConfigureAwait(false);
        });

        return new Command("vision", "Local-vision advanced utilities.") { generate };
    }

    // ---------------------------------------------------------------------------------------
    //  mcp serve
    // ---------------------------------------------------------------------------------------

    private static Command BuildMcpCommand(TextWriter stderr)
    {
        var serve = new Command("serve", "Starts the Zakira.Replay MCP server.");
        var transport = new Option<string>("--transport")
        {
            Description = "MCP transport: stdio (default), http, or sse.",
            DefaultValueFactory = _ => McpHost.TransportStdio
        };
        transport.AcceptOnlyFromAmong(McpHost.TransportStdio, McpHost.TransportHttp, McpHost.TransportSse);
        var port = new Option<int>("--port")
        {
            Description = "Listen port for the http / sse transports.",
            DefaultValueFactory = _ => McpHost.DefaultHttpPort
        };
        serve.Options.Add(transport);
        serve.Options.Add(port);
        serve.SetAction(async (parseResult, cancellationToken) =>
        {
            return await McpHost.RunAsync(
                parseResult.GetValue(transport)!,
                parseResult.GetValue(port),
                cancellationToken).ConfigureAwait(false);
        });

        return new Command("mcp", "Model Context Protocol server.") { serve };
    }

    // ---------------------------------------------------------------------------------------
    //  completion (new): emits shell-completion scripts
    // ---------------------------------------------------------------------------------------

    private static Command BuildCompletionCommand(TextWriter stdout)
    {
        var shell = new Argument<string>("shell") { Description = "Shell: bash, zsh, pwsh, or fish." };
        shell.AcceptOnlyFromAmong("bash", "zsh", "pwsh", "fish");
        var command = new Command("completion", "Emits shell completion scripts for `zakira-replay`.")
        {
            shell
        };
        command.SetAction(parseResult =>
        {
            var shellName = parseResult.GetValue(shell)!;
            // System.CommandLine ships a dotnet-suggest based completion script; the helper
            // emits the relevant snippet here. Keep this stub so the contract surface is
            // documented and discoverable; full installation is documented per shell:
            //   https://learn.microsoft.com/dotnet/standard/commandline/tab-completion
            stdout.WriteLine(shellName switch
            {
                "bash" => "# Add the following to your ~/.bashrc:\nsource <(dotnet-suggest script bash)",
                "zsh" => "# Add the following to your ~/.zshrc:\nsource <(dotnet-suggest script zsh)",
                "pwsh" => "# Add the following to your $PROFILE:\ndotnet-suggest script pwsh | Out-String | Invoke-Expression",
                "fish" => "# Add the following to your ~/.config/fish/config.fish:\ndotnet-suggest script fish | source",
                _ => $"# Unknown shell: {shellName}"
            });
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------------------------------
    //  Shared option builder for analyze / transcribe / queue enqueue.
    // ---------------------------------------------------------------------------------------

    private static (IReadOnlyList<Option> Options, Func<ParseResult, string, bool, int, string?, AnalyzeRequest> Apply) BuildAnalyzeOptions()
    {
        var visionInstruction = new Option<string?>("--vision-instruction") { Description = "Optional vision focus instruction." };
        var ocrInstruction = new Option<string?>("--ocr-instruction") { Description = "Optional OCR focus instruction." };
        var stt = new Option<bool>("--stt") { Description = "Use LLM-based STT." };
        var audio = new Option<bool>("--audio") { Description = "Extract audio." };
        var ocr = new Option<bool>("--ocr") { Description = "Run OCR over selected frames." };
        var vision = new Option<bool>("--vision") { Description = "Run vision over selected frames." };
        var diarize = new Option<bool>("--diarize") { Description = "Run sherpa-onnx diarization." };
        var numSpeakers = new Option<int?>("--num-speakers") { Description = "Speaker hint." };
        var diarizeThreshold = new Option<double?>("--diarize-threshold") { Description = "Diarization threshold." };
        var maxAiFrames = new Option<int>("--max-ai-frames") { Description = "Max frames sent to AI providers.", DefaultValueFactory = _ => 50 };
        var llmProvider = new Option<string?>("--llm-provider") { Description = "LLM provider." };
        var model = new Option<string?>("--model") { Description = "Model id." };
        var ocrProvider = new Option<string?>("--ocr-provider") { Description = "OCR provider." };
        var visionProvider = new Option<string?>("--vision-provider") { Description = "Vision provider." };
        var localVisionMode = new Option<string?>("--local-vision-mode") { Description = "Local vision sub-mode." };
        var smartCrop = new Option<bool?>("--smart-crop") { Description = "Toggle smart-crop preprocessing." };
        var smartCropProfile = new Option<string?>("--smart-crop-profile") { Description = "Smart-crop profile." };
        var captureMode = new Option<string?>("--capture-mode") { Description = "Capture mode." };
        var authProfile = new Option<string?>("--auth-profile") { Description = "Auth profile." };
        var frameStrategy = new Option<string?>("--frame-strategy") { Description = "Frame selection strategy." };
        var everyFrame = new Option<bool>("--every-frame") { Description = "Alias for --frame-strategy every-frame." };
        var cookies = new Option<string?>("--cookies") { Description = "Cookies file path." };
        var cookiesFromBrowser = new Option<string?>("--cookies-from-browser") { Description = "cookies-from-browser spec." };
        var browserAuth = new Option<string?>("--browser-auth") { Description = "Alias for --cookies-from-browser." };
        var captionLanguages = new Option<string?>("--caption-languages") { Description = "Caption languages, comma-separated." };
        var secondaryTranscripts = new Option<string?>("--secondary-transcripts") { Description = "Comma-separated languages to also persist as transcript.<lang>.md (default: none)." };
        var preferInlineMedia = new Option<bool>("--prefer-inline-media") { Description = "Skip in-browser play+duration probe; resolve the source's inline media URL (e.g. Medius HLS) and seek via ffmpeg. Fast path for MSE players that don't boot headlessly." };
        var autoplayPolicy = new Option<string?>("--autoplay-policy") { Description = "Override Chromium autoplay policy for this run. Values: default | no-user-gesture-required. Per-host map in capture.browser.autoplayPolicyByHost still applies when this is unset." };
        var allowMediaDownload = new Option<bool>("--allow-media-download") { Description = "Opt in to downloading the source video locally when no direct or inline URL is reachable. Default off: the run fails with MEDIA_DOWNLOAD_DECLINED rather than silently consuming bandwidth + disk." };
        var noSlideGrouping = new Option<bool>("--no-slide-grouping") { Description = "Disable slide grouping." };
        var slideHashDistance = new Option<int?>("--slide-hash-distance") { Description = "Slide hash Hamming distance." };
        var framesPerMinute = new Option<int?>("--frames-per-minute") { Description = "Frames per minute for interval sampling." };
        var sceneSafetyCap = new Option<int?>("--scene-safety-cap") { Description = "Per-run scene safety cap." };
        var cache = new Option<bool>("--cache") { Description = "Reuse cached run." };
        var force = new Option<bool>("--force") { Description = "Force recompute." };

        var options = new Option[]
        {
            visionInstruction, ocrInstruction, stt, audio, ocr, vision, diarize, numSpeakers, diarizeThreshold,
            maxAiFrames, llmProvider, model, ocrProvider, visionProvider, localVisionMode, smartCrop, smartCropProfile,
            captureMode, authProfile, frameStrategy, everyFrame, cookies, cookiesFromBrowser, browserAuth,
            captionLanguages, secondaryTranscripts, preferInlineMedia, autoplayPolicy, allowMediaDownload, noSlideGrouping, slideHashDistance, framesPerMinute, sceneSafetyCap, cache, force
        };

        Func<ParseResult, string, bool, int, string?, AnalyzeRequest> apply = (parseResult, source, includeTranscript, frameCount, runId) =>
        {
            var sttValue = parseResult.GetValue(stt);
            var extractAudio = parseResult.GetValue(audio) || sttValue;
            var config = new ConfigStore().Load();
            var resolvedLlmProvider = LlmProviderFactory.Normalize(parseResult.GetValue(llmProvider) ?? LlmProviderFactory.GetConfiguredProvider(config));
            var resolvedOcrProvider = OcrProviderFactory.Normalize(parseResult.GetValue(ocrProvider) ?? OcrProviderFactory.GetConfiguredProvider(config));
            var resolvedVisionProvider = VisionProviderFactory.Normalize(parseResult.GetValue(visionProvider) ?? VisionProviderFactory.GetConfiguredProvider(config));
            var resolvedFrameStrategy = parseResult.GetValue(everyFrame)
                ? FrameSelectionStrategies.EveryFrame
                : (parseResult.GetValue(frameStrategy) ?? FrameSelectionStrategies.Interval);
            var resolvedCookiesFromBrowser = parseResult.GetValue(browserAuth) ?? parseResult.GetValue(cookiesFromBrowser);
            var captionLangs = ParseCaptionLanguages(parseResult.GetValue(captionLanguages));
            var secondaryLangs = ParseCaptionLanguages(parseResult.GetValue(secondaryTranscripts));
            bool? slideGroupingValue = parseResult.GetValue(noSlideGrouping) ? false : null;
            float? diarizationThreshold = null;
            var threshold = parseResult.GetValue(diarizeThreshold);
            if (threshold.HasValue && threshold.Value > 0)
            {
                diarizationThreshold = (float)threshold.Value;
            }

            return new AnalyzeRequest(
                Source: source,
                VisionInstruction: parseResult.GetValue(visionInstruction) ?? string.Empty,
                OcrInstruction: parseResult.GetValue(ocrInstruction) ?? string.Empty,
                IncludeTranscript: includeTranscript,
                FrameCount: frameCount,
                RunId: runId,
                ExtractAudio: extractAudio,
                UseSpeechToText: sttValue,
                UseOcr: parseResult.GetValue(ocr),
                UseVision: parseResult.GetValue(vision),
                MaxAiFrames: parseResult.GetValue(maxAiFrames),
                Model: parseResult.GetValue(model) ?? LlmProviderFactory.GetDefaultModel(resolvedLlmProvider, config),
                LlmProvider: resolvedLlmProvider,
                Force: parseResult.GetValue(force),
                UseCache: parseResult.GetValue(cache),
                FrameStrategy: resolvedFrameStrategy,
                CookiesPath: parseResult.GetValue(cookies),
                CookiesFromBrowser: resolvedCookiesFromBrowser,
                CaptionLanguages: captionLangs,
                SlideGrouping: slideGroupingValue,
                SlideHashDistance: parseResult.GetValue(slideHashDistance),
                FramesPerMinute: parseResult.GetValue(framesPerMinute),
                SceneSafetyCap: parseResult.GetValue(sceneSafetyCap),
                OcrProvider: resolvedOcrProvider,
                SmartCrop: parseResult.GetValue(smartCrop),
                SmartCropProfile: parseResult.GetValue(smartCropProfile),
                CaptureMode: parseResult.GetValue(captureMode),
                AuthProfile: parseResult.GetValue(authProfile),
                UseDiarization: parseResult.GetValue(diarize),
                NumSpeakers: parseResult.GetValue(numSpeakers),
                DiarizationThreshold: diarizationThreshold,
                VisionProvider: resolvedVisionProvider,
                LocalVisionMode: parseResult.GetValue(localVisionMode),
                SecondaryCaptionLanguages: secondaryLangs,
                PreferInlineMedia: parseResult.GetValue(preferInlineMedia),
                AutoplayPolicy: parseResult.GetValue(autoplayPolicy),
                // Flag present → true (opt in); flag absent → null so the pipeline can
                // fall back to capture.allowMediaDownload from config (defaults to false).
                AllowMediaDownload: parseResult.GetValue(allowMediaDownload) ? true : (bool?)null);
        };

        return (options, apply);
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

    /// <summary>
    /// Applies preset defaults onto an analyze request. Presets are opinionated bundles
    /// (meeting / lecture / demo / interview / raw) so agents don't need to pass 10 flags
    /// for the most common scenarios. Explicit flags always win because they were already
    /// applied when the request was built.
    /// </summary>
    private static AnalyzeRequest ApplyPreset(AnalyzeRequest request, string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            return request;
        }
        return preset.ToLowerInvariant() switch
        {
            "meeting" => request with
            {
                UseOcr = true,
                UseVision = true,
                UseDiarization = true,
                ExtractAudio = true,
                UseSpeechToText = true
            },
            "lecture" => request with
            {
                UseOcr = true,
                UseVision = true,
                ExtractAudio = true
            },
            "demo" => request with
            {
                UseOcr = true,
                UseVision = true,
                FrameStrategy = FrameSelectionStrategies.Scene
            },
            "interview" => request with
            {
                UseDiarization = true,
                ExtractAudio = true,
                UseSpeechToText = true,
                FrameCount = 0
            },
            _ => request
        };
    }

    private static bool IsJson(ParseResult parseResult, Option<string> outputFormat)
    {
        var value = parseResult.GetValue(outputFormat);
        return value is "json" or "ndjson";
    }

    private static async Task<int> RunPipelineAsync(AnalyzeRequest request, TextWriter stdout, TextWriter stderr, bool isJson, CancellationToken cancellationToken)
    {
        // Back-compat overload (in-process callers + tests that predate verbosity); resolves
        // to the Default verbosity which matches the post-0.14 "quiet but informative" baseline.
        return await RunPipelineAsync(request, stdout, stderr, isJson, verbosity: CliVerbosity.Default, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunPipelineAsync(AnalyzeRequest request, TextWriter stdout, TextWriter stderr, bool isJson, CliVerbosity verbosity, CancellationToken cancellationToken)
    {
        var pipeline = CreatePipeline();

        // Progress sink routing:
        //   JSON mode  → always stream to stderr (unless Quiet) so stdout stays a single parseable
        //               envelope. Orchestrators expect breadcrumbs on stderr regardless of -v / -q.
        //   Text mode  → stream only in Verbose; Default/Quiet swallow the stream so the user
        //               sees just the compact start/done summary (or nothing in Quiet).
        var streamProgress = verbosity != CliVerbosity.Quiet && (isJson || verbosity == CliVerbosity.Verbose);
        var progressTarget = isJson ? stderr : stdout;
        var progress = streamProgress
            ? new SynchronousProgress<string>(progressTarget.WriteLine)
            : new SynchronousProgress<string>(_ => { });

        // Compact start line in Default mode so a long-running run doesn't look hung. JSON and
        // Verbose modes get their own surface (JSON envelope / streamed progress); Quiet stays
        // silent.
        var renderSummary = !isJson && CliVerbosityHelpers.ShouldRenderSummary(verbosity);
        if (renderSummary && verbosity == CliVerbosity.Default)
        {
            stdout.WriteLine($"Analyzing {TruncateForDisplay(request.Source, 80)}...");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        AnalyzeResult result;
        try
        {
            result = await pipeline.AnalyzeAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (MissingDependencyException ex)
        {
            // Render dependency / pipeline errors cleanly. Without this catch the exception
            // escapes to System.CommandLine's default handler, which prints a raw stack trace
            // ("Unhandled exception: ...") rather than the actionable message and exit code we
            // surface elsewhere (Program.cs only sees exceptions thrown OUTSIDE the command action).
            stderr.WriteLine(ex.ToDisplayString());
            return 2;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("Error: operation cancelled.");
            return 130;
        }
        catch (ReplayException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        stopwatch.Stop();

        if (isJson)
        {
            // Single-line JSON envelope on stdout: everything an orchestrator needs to pick
            // up the run without having to re-read manifest.json. Absolute paths are emitted
            // for the artifacts that are present so callers can `File.Exists` / open them
            // directly; relative paths are preserved on `manifestPaths` for use cases that
            // archive the run directory and need portable paths.
            stdout.WriteLine(JsonSerializer.Serialize(BuildAnalyzeJsonEnvelope(result), CliJson.Options));
            return 0;
        }

        if (renderSummary)
        {
            stdout.WriteLine();
            stdout.WriteLine(BuildPipelineSummaryLine(result, stopwatch.Elapsed));
            stdout.WriteLine($"  Artifacts: {result.Run.Directory}");
            stdout.WriteLine($"  Manifest:  {result.Run.GetPath("manifest.json")}");
        }

        RenderWarnings(stdout, result.Manifest.Warnings, verbosity);
        return 0;
    }

    /// <summary>
    /// One-line summary printed after a successful analyze/transcribe/frames run. Format:
    /// <c>Done in 54s. brk230-1ccc2f93 — 15 frames, transcript: en-US (90 KB).</c> Designed
    /// to be the only line a Default-mode user sees so it carries every signal that's both
    /// universally useful and cheap to compute from the result alone.
    /// </summary>
    internal static string BuildPipelineSummaryLine(AnalyzeResult result, TimeSpan elapsed)
    {
        var verb = result.Reused ? "Reused" : "Done";
        var pieces = new List<string>();
        var frameCount = result.Manifest.Frames.Count;
        if (frameCount > 0)
        {
            pieces.Add($"{frameCount} frame{(frameCount == 1 ? "" : "s")}");
        }
        if (!string.IsNullOrWhiteSpace(result.Manifest.TranscriptPath))
        {
            pieces.Add($"transcript: {result.Manifest.TranscriptPath}");
        }
        if (!string.IsNullOrWhiteSpace(result.Manifest.AudioPath))
        {
            pieces.Add($"audio: {result.Manifest.AudioPath}");
        }

        var tail = pieces.Count == 0 ? string.Empty : " \u2014 " + string.Join(", ", pieces);
        return $"{verb} in {FormatElapsed(elapsed)}. {result.Run.Id}{tail}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1) return $"{elapsed.TotalMilliseconds:F0}ms";
        if (elapsed.TotalSeconds < 60) return $"{elapsed.TotalSeconds:F1}s";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s";
        return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
    }

    private static string TruncateForDisplay(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value[..(max - 1)] + "\u2026";
    }

    /// <summary>
    /// Renders warnings to <paramref name="stdout"/> filtered by <paramref name="verbosity"/>:
    /// Verbose shows everything (matches pre-0.14 behaviour); Default suppresses
    /// <c>info</c> severities; Quiet suppresses everything except <c>error</c>. The block is
    /// omitted entirely if no warning passes the filter so the success path stays clean.
    /// </summary>
    internal static void RenderWarnings(TextWriter stdout, IReadOnlyList<ReplayWarning> warnings, CliVerbosity verbosity)
    {
        if (warnings.Count == 0) return;
        var visible = warnings.Where(w => CliVerbosityHelpers.ShouldRender(verbosity, w.Severity)).ToList();
        if (visible.Count == 0) return;

        stdout.WriteLine();
        stdout.WriteLine("Warnings:");
        foreach (var warning in visible)
        {
            stdout.WriteLine($"- [{warning.Severity}] {warning.Code}: {warning.Message}");
        }
    }

    /// <summary>
    /// Builds the JSON envelope emitted by <c>analyze</c> / <c>transcribe</c> / the
    /// <c>frames</c> back-compat path when <c>--output-format json|ndjson</c> is set.
    /// Shape is documented in README.md ("analyze --output-format json"); keep this in sync
    /// with that documentation. `null` fields are preserved (not omitted) so consumers can
    /// rely on a stable property set.
    /// </summary>
    internal static object BuildAnalyzeJsonEnvelope(AnalyzeResult result)
    {
        return new
        {
            runId = result.Run.Id,
            reused = result.Reused,
            artifactDirectory = result.Run.Directory,
            manifestPath = result.Run.GetPath("manifest.json"),
            evidencePath = result.Manifest.EvidencePath is null ? null : result.Run.GetPath(result.Manifest.EvidencePath),
            transcriptPath = result.Manifest.TranscriptPath is null ? null : result.Run.GetPath(result.Manifest.TranscriptPath),
            audioPath = result.Manifest.AudioPath is null ? null : result.Run.GetPath(result.Manifest.AudioPath),
            ocrPath = result.Manifest.OcrPath is null ? null : result.Run.GetPath(result.Manifest.OcrPath),
            visionPath = result.Manifest.VisionPath is null ? null : result.Run.GetPath(result.Manifest.VisionPath),
            frameCount = result.Manifest.Frames.Count,
            title = result.Manifest.Title,
            webpageUrl = result.Manifest.WebpageUrl,
            duration = result.Manifest.Duration,
            source = result.Manifest.Source,
            warnings = result.Manifest.Warnings
        };
    }

    private static AnalysisPipeline CreatePipeline()
    {
        var dependencies = new DependencyResolver();
        var runner = new ProcessRunner();
        var store = new ArtifactStore(ResolveRunsRoot());
        var ytDlp = new YtDlpClient(dependencies, runner);
        var ffmpeg = new FfmpegClient(dependencies, runner);
        var browser = new PlaywrightVideoCaptureClient(dependencies);
        return new AnalysisPipeline(store, ytDlp, ffmpeg, (string? name) => LlmProviderFactory.TryCreate(name), browser);
    }

    private static ClipExtractionService CreateClipService()
    {
        var dependencies = new DependencyResolver();
        var runner = new ProcessRunner();
        var store = new ArtifactStore(ResolveRunsRoot());
        return new ClipExtractionService(store, new YtDlpClient(dependencies, runner), new FfmpegClient(dependencies, runner));
    }

    private static FrameCaptureService CreateFrameCaptureService()
    {
        var dependencies = new DependencyResolver();
        var runner = new ProcessRunner();
        var store = new ArtifactStore(ResolveRunsRoot());
        // Wire the browser client so spot-frame requests against sources yt-dlp can't resolve
        // (Medius / Microsoft Build, custom MSE players) fall back to a fast metadata-only
        // browser probe that extracts the inline HLS URL and hands it to ffmpeg.
        var browser = new PlaywrightVideoCaptureClient(dependencies);
        return new FrameCaptureService(store, new YtDlpClient(dependencies, runner), new FfmpegClient(dependencies, runner), browser);
    }

    /// <summary>
    /// Resolves the runs output directory in this precedence: env var
    /// <c>ZAKIRA_REPLAY_RUNS_DIRECTORY</c> → <c>runs.directory</c> in the user config →
    /// <c>&lt;cwd&gt;/runs</c>. Config load is best-effort so the CLI still works when the
    /// config file is missing or unparseable; the env var still wins in those cases.
    /// </summary>
    private static string ResolveRunsRoot()
    {
        ReplayConfig? config = null;
        try
        {
            config = new ConfigStore().Load();
        }
        catch
        {
            // Best-effort: env-var or default still applies.
        }
        return ArtifactStore.ResolveRootDirectory(config);
    }

    /// <summary>
    /// Inline-invoking <see cref="IProgress{T}"/> implementation used by every CLI command
    /// surface in place of <see cref="Progress{T}"/>. The framework's
    /// <see cref="Progress{T}"/> captures the ambient <see cref="SynchronizationContext"/>
    /// at construction and posts each <c>Report</c> callback through it — useful for UI
    /// thread marshalling, but a footgun in a CLI / server process where the captured
    /// context is null and callbacks therefore land on the thread pool. That posts a race
    /// against the call site's stream: a callback fires after <c>AnalyzeAsync</c> has
    /// returned and after the caller has disposed the <see cref="TextWriter"/> it handed
    /// us, producing <see cref="ObjectDisposedException"/> on a background thread which
    /// crashes the host (xUnit test host on CI; would surface as an unhandled exception in
    /// production too if anyone passed a disposable writer to <see cref="RunAsync"/>).
    /// This implementation invokes the handler synchronously on the thread that called
    /// <see cref="Report"/>, which in our pipeline is the awaiting thread, so callbacks
    /// always complete before <c>AnalyzeAsync</c> returns. <c>Console.Out</c>/
    /// <c>Console.Error</c> are never disposed in production so the prior Progress-based
    /// code was correct there; the bug only surfaced once tests injected a
    /// <see cref="StringWriter"/>.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> handler;

        public SynchronousProgress(Action<T> handler)
        {
            this.handler = handler;
        }

        public void Report(T value) => handler(value);
    }
}
