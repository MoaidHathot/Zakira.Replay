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

        var root = new RootCommand("Zakira.Replay — turns video sources into agent-consumable evidence.")
        {
            outputFormat,
            logFile,
            logLevel,
            correlationId,

            BuildVersionCommand(stdout),
            BuildInfoCommand(stdout, outputFormat),
            BuildDoctorCommand(stdout, outputFormat),
            BuildAnalyzeCommand(stdout),
            BuildTranscribeCommand(stdout),
            BuildFramesCommand(stdout, outputFormat),
            BuildClipCommand(stdout),
            BuildDiscoverCommand(stdout),
            BuildBatchCommand(stdout),
            BuildRunsCommand(stdout, outputFormat),
            BuildChaptersCommand(stdout),
            BuildAlignCommand(stdout),
            BuildIndexCommand(stdout, outputFormat),
            BuildQueueCommand(stdout, outputFormat),
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

    private static Command BuildAnalyzeCommand(TextWriter stdout)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var preset = new Option<string?>("--preset")
        {
            Description = "Opinionated defaults bundle: meeting, lecture, demo, interview, raw."
        };
        preset.AcceptOnlyFromAmong("meeting", "lecture", "demo", "interview", "raw");

        var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
        var frames = new Option<int>("--frames") { Description = "Number of representative frames to extract.", DefaultValueFactory = _ => 500 };
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
            return await RunPipelineAsync(request, stdout, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTranscribeCommand(TextWriter stdout)
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
            return await RunPipelineAsync(request, stdout, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    // ---------------------------------------------------------------------------------------
    //  Media: frames, clip, discover
    // ---------------------------------------------------------------------------------------

    private static Command BuildFramesCommand(TextWriter stdout, Option<string> outputFormat)
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

        var command = new Command("frames", "Ad-hoc frame capture. Pass --at OR --from/--to (mutually exclusive).")
        {
            source, at, from, to, count, strategy, maxEdge, quality, phash, sceneCap, runId, cookies, browserAuth
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
                var request = applyAnalyzeOptions(parseResult, parseResult.GetValue(source)!, false, parseResult.GetValue(count) ?? 500, parseResult.GetValue(runId));
                _ = analyzeOptions; // not added to command; default values are used
                return await RunPipelineAsync(request, stdout, cancellationToken).ConfigureAwait(false);
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
                CookiesFromBrowser: parseResult.GetValue(browserAuth));

            var service = CreateFrameCaptureService();
            var progress = new Progress<string>(stdout.WriteLine);
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
        });
        return command;
    }

    private static Command BuildClipCommand(TextWriter stdout)
    {
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var start = new Option<string>("--start") { Description = "Clip start timestamp.", Required = true };
        var end = new Option<string>("--end") { Description = "Clip end timestamp.", Required = true };
        var runId = new Option<string?>("--run-id") { Description = "Optional run id." };
        var outputName = new Option<string?>("--output-name") { Description = "Optional output file name." };
        var cookies = new Option<string?>("--cookies") { Description = "Cookies file path." };
        var browserAuth = new Option<string?>("--browser-auth") { Description = "cookies-from-browser spec." };

        var command = new Command("clip", "Extracts a timestamped clip from a video source.")
        {
            source, start, end, runId, outputName, cookies, browserAuth
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var service = CreateClipService();
            var progress = new Progress<string>(stdout.WriteLine);
            var result = await service.ExtractAsync(new ClipExtractionRequest(
                Source: parseResult.GetValue(source)!,
                Start: Timestamp.ParseRequired(parseResult.GetValue(start)!, "start"),
                End: Timestamp.ParseRequired(parseResult.GetValue(end)!, "end"),
                RunId: parseResult.GetValue(runId),
                OutputName: parseResult.GetValue(outputName),
                CookiesPath: parseResult.GetValue(cookies),
                CookiesFromBrowser: parseResult.GetValue(browserAuth)), progress, cancellationToken).ConfigureAwait(false);

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

    private static Command BuildBatchCommand(TextWriter stdout)
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
            var progress = new Progress<string>(stdout.WriteLine);
            var result = await runner.RunAsync(
                parseResult.GetValue(manifest)!.FullName,
                progress,
                cancellationToken,
                parseResult.GetValue(concurrency)).ConfigureAwait(false);
            stdout.WriteLine();
            stdout.WriteLine($"Completed batch: {result.BatchId}");
            stdout.WriteLine($"Batch artifacts: {result.BatchDirectory}");
            stdout.WriteLine($"Succeeded: {result.Items.Count(item => item.Succeeded)}");
            stdout.WriteLine($"Failed: {result.Items.Count(item => !item.Succeeded)}");
            return result.Items.Any(item => !item.Succeeded) ? 1 : 0;
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
        var queryTarget = new Argument<string>("target") { Description = "Run directory or index path." };
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
            var result = await new SearchIndexService().QueryAsync(
                Path.GetFullPath(parseResult.GetValue(queryTarget)!),
                parseResult.GetValue(queryString)!,
                parseResult.GetValue(top),
                options,
                cancellationToken).ConfigureAwait(false);
            stdout.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
            return 0;
        });

        return new Command("index", "Search index commands (replaces 0.8's `search` group).") { build, query };
    }

    // ---------------------------------------------------------------------------------------
    //  queue: enqueue, run, status
    // ---------------------------------------------------------------------------------------

    private static Command BuildQueueCommand(TextWriter stdout, Option<string> outputFormat)
    {
        var queueId = new Option<string?>("--queue-id") { Description = "Persistent queue id. Defaults to 'default'." };

        var (analyzeOptions, applyAnalyzeOptions) = BuildAnalyzeOptions();
        var enqueue = new Command("enqueue", "Adds an analysis request to a persistent queue.");
        var source = new Argument<string>("source") { Description = "Video URL or local media path." };
        var jobId = new Option<string?>("--job-id") { Description = "Optional stable job id." };
        var retries = new Option<int>("--retries") { Description = "Retry count.", DefaultValueFactory = _ => 0 };
        var noTranscript = new Option<bool>("--no-transcript") { Description = "Skip transcript extraction." };
        var frames = new Option<int>("--frames") { Description = "Frame count.", DefaultValueFactory = _ => 500 };
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
            var progress = new Progress<string>(stdout.WriteLine);
            var result = await queue.RunAsync(
                parseResult.GetValue(queueId),
                new AnalysisQueueRunOptions(
                    Concurrency: parseResult.GetValue(concurrency),
                    Retries: parseResult.GetValue(runRetries)),
                progress,
                cancellationToken).ConfigureAwait(false);
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
            var progress = new Progress<string>(stdout.WriteLine);
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
    //  Shared option builder for analyze / transcribe / queue.enqueue.
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
            captionLanguages, noSlideGrouping, slideHashDistance, framesPerMinute, sceneSafetyCap, cache, force
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
                : (parseResult.GetValue(frameStrategy) ?? FrameSelectionStrategies.Scene);
            var resolvedCookiesFromBrowser = parseResult.GetValue(browserAuth) ?? parseResult.GetValue(cookiesFromBrowser);
            var captionLangs = ParseCaptionLanguages(parseResult.GetValue(captionLanguages));
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
                LocalVisionMode: parseResult.GetValue(localVisionMode));
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

    private static async Task<int> RunPipelineAsync(AnalyzeRequest request, TextWriter stdout, CancellationToken cancellationToken)
    {
        var pipeline = CreatePipeline();
        var progress = new Progress<string>(stdout.WriteLine);
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
            Console.Error.WriteLine(ex.ToDisplayString());
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Error: operation cancelled.");
            return 130;
        }
        catch (ReplayException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

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
        return new FrameCaptureService(store, new YtDlpClient(dependencies, runner), new FfmpegClient(dependencies, runner));
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
}
