using System.Text.Json;
using NJsonSchema;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Tests;

/// <summary>
/// A pipeline exception used to mean "no manifest.json": three user-reported "failed" runs had a
/// usable transcript.md + metadata.json that could only be found by browsing the filesystem.
/// Now every early stop writes a manifest with <c>status</c> partial/failed and a
/// <c>failure</c> block, and callers receive it via <see cref="AnalysisPartialException"/>.
/// </summary>
public sealed class PartialRunTests
{
    private const string Vtt = """
        WEBVTT

        00:00:01.000 --> 00:00:03.000
        first sentence of the recipe.

        00:00:04.000 --> 00:00:06.000
        second sentence of the recipe.
        """;

    [Fact]
    public async Task MediaUrlTimeoutLeavesPartialManifestWithTranscriptAndFailureDetails()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { Captions = Vtt, MediaUrlFailure = new ProcessTimeoutException("yt-dlp", ["-g", "u"], TimeSpan.FromMinutes(5), "", "ERROR: [youtube] abc: Sign in to confirm you're not a bot") };
        var pipeline = new AnalysisPipeline(store, ytDlp, new ScriptedFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "partial-media-url") with { IncludeTranscript = true, FrameCount = 3 };

        var ex = await Assert.ThrowsAsync<AnalysisPartialException>(() => pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None));

        Assert.True(ex.HasUsableArtifacts);
        Assert.IsType<ProcessTimeoutException>(ex.InnerException);
        Assert.Contains("media-url", ex.Message);
        Assert.Contains("transcript (transcript.md)", ex.Message);

        var manifest = await store.ReadJsonAsync<ArtifactManifest>(ex.Run, "manifest.json", CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(RunStatuses.Partial, manifest.Status);
        Assert.False(manifest.IsCompleted);
        Assert.Equal("0.9", manifest.SchemaVersion);
        Assert.Equal("transcript.md", manifest.TranscriptPath);
        Assert.Null(manifest.EvidencePath);
        Assert.Empty(manifest.Frames);
        Assert.Equal("Yogurt", manifest.Title);
        Assert.NotNull(manifest.Failure);
        Assert.Equal(RunStages.MediaUrl, manifest.Failure.Stage);
        Assert.Equal(nameof(ProcessTimeoutException), manifest.Failure.ExceptionType);
        Assert.Contains("Sign in to confirm", manifest.Failure.OutputTail);
        Assert.Contains("--cookies", manifest.Failure.Hint);
        Assert.False(manifest.Failure.Cancelled);
        Assert.NotNull(manifest.Timings);

        var schema = await JsonSchema.FromFileAsync(SchemaPath("manifest.schema.json"));
        Assert.Empty(schema.Validate(await File.ReadAllTextAsync(ex.Run.GetPath("manifest.json"))).Select(e => e.ToString() + " @ " + e.Path));

        // The normalized transcript is on disk (not the raw cues) even though the run stopped.
        var transcript = await File.ReadAllTextAsync(ex.Run.GetPath("transcript.md"));
        Assert.Contains("first sentence of the recipe.", transcript);
    }

    [Fact]
    public async Task ProbeFailureWritesFailedManifestWithoutUsableArtifacts()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { InfoFailure = new ProcessFailedException("yt-dlp", ["-J"], 1, "ERROR: [youtube] abc: Video unavailable") };
        var pipeline = new AnalysisPipeline(store, ytDlp, new ScriptedFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "failed-probe") with { IncludeTranscript = true, CaptureMode = CaptureModes.YtDlp };

        var ex = await Assert.ThrowsAsync<AnalysisPartialException>(() => pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None));

        Assert.False(ex.HasUsableArtifacts);
        var manifest = await store.ReadJsonAsync<ArtifactManifest>(ex.Run, "manifest.json", CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(RunStatuses.Failed, manifest.Status);
        Assert.Equal(RunStages.Probe, manifest.Failure!.Stage);
        Assert.Null(manifest.TranscriptPath);
        Assert.Null(manifest.Title);
        Assert.Contains("Video unavailable", manifest.Failure.OutputTail);
        Assert.Equal(nameof(ProcessFailedException), manifest.Failure.ExceptionType);
        Assert.Contains("unavailable", manifest.Failure.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Video unavailable", ex.Message);
        Assert.Contains("no usable artifacts", ex.Message);
    }

    [Fact]
    public async Task PartialManifestIsNotReusedOnRetryButCompletedOneIs()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { Captions = Vtt, MediaUrlFailure = new ReplayException("boom") };
        var pipeline = new AnalysisPipeline(store, ytDlp, new ScriptedFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "retry-partial") with { IncludeTranscript = true, FrameCount = 2 };

        await Assert.ThrowsAsync<AnalysisPartialException>(() => pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None));
        Assert.Equal(1, ytDlp.InfoCalls);

        // Second attempt: the failure is gone; the partial manifest must not short-circuit the run.
        ytDlp.MediaUrlFailure = null;
        var result = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);
        Assert.False(result.Reused);
        Assert.Equal(2, ytDlp.InfoCalls);
        Assert.Equal(RunStatuses.Completed, result.Manifest.Status);
        Assert.True(result.Manifest.IsCompleted);
        Assert.Null(result.Manifest.Failure);

        // Third attempt: the completed manifest is reused as before.
        var reused = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);
        Assert.True(reused.Reused);
        Assert.Equal(2, ytDlp.InfoCalls);
    }

    [Fact]
    public async Task MissingDependencyIsNotWrappedButStillWritesManifest()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { InfoFailure = new MissingDependencyException("yt-dlp", "extracting media metadata") };
        var pipeline = new AnalysisPipeline(store, ytDlp, new ScriptedFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://example.test/v", "missing-dep") with { CaptureMode = CaptureModes.YtDlp };

        await Assert.ThrowsAsync<MissingDependencyException>(() => pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None));

        var run = store.CreateRun(request.Source, request.RunId);
        var manifest = await store.ReadJsonAsync<ArtifactManifest>(run, "manifest.json", CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(RunStatuses.Failed, manifest.Status);
        Assert.Equal(nameof(MissingDependencyException), manifest.Failure!.ExceptionType);
    }

    [Fact]
    public async Task McpJobBecomesPartialWithManifestAttached()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { Captions = Vtt, MediaUrlFailure = new ProcessTimeoutException("yt-dlp", ["-g"], TimeSpan.FromMinutes(5), "", "ERROR: HTTP Error 429: Too Many Requests") };
        var manager = new McpJobManager(() => new AnalysisPipeline(store, ytDlp, new ScriptedFfmpeg(), _ => null), temp.GetPath("jobs"));
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "mcp-partial") with { IncludeTranscript = true, FrameCount = 2 };

        var job = manager.Create(request);
        McpJobSnapshot? snapshot = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            snapshot = job.Snapshot(includeLogs: true);
            if (snapshot.Status is "partial" or "failed" or "succeeded")
            {
                break;
            }

            await Task.Delay(50, CancellationToken.None);
        }

        Assert.NotNull(snapshot);
        Assert.Equal("partial", snapshot.Status);
        Assert.NotNull(snapshot.Manifest);
        Assert.Equal(RunStatuses.Partial, snapshot.Manifest.Status);
        Assert.Equal("transcript.md", snapshot.Manifest.TranscriptPath);
        Assert.NotNull(snapshot.ArtifactDirectory);
        Assert.Contains("HTTP Error 429", snapshot.Error);
        Assert.Contains("rate-limiting", snapshot.Error);
        Assert.Contains("media-url", snapshot.Error);

        // Persisted snapshots restore with the same status.
        var restored = new McpJobManager(() => throw new InvalidOperationException("no rerun"), temp.GetPath("jobs")).Get(job.JobId)?.Snapshot(includeLogs: false);
        Assert.NotNull(restored);
        Assert.Equal("partial", restored.Status);
        Assert.NotNull(restored.Manifest);
    }

    [Fact]
    public async Task BatchAndQueueRecordThePartialRunDirectoryOnFailedItems()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { Captions = Vtt, MediaUrlFailure = new ReplayException("boom") };
        AnalysisPipeline PipelineFactory() => new(store, ytDlp, new ScriptedFfmpeg(), _ => null);

        var manifestPath = temp.GetPath("batch.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BatchManifest
        {
            BatchId = "partial-batch",
            Frames = 2,
            IncludeTranscript = true,
            ContinueOnError = true,
            Items = [new BatchItem { Source = "https://www.youtube.com/watch?v=abc", RunId = "partial-batch-item" }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var batch = await new BatchRunner(PipelineFactory, store.RootDirectory).RunAsync(manifestPath, progress: null, CancellationToken.None);
        var item = Assert.Single(batch.Items);
        Assert.False(item.Succeeded);
        Assert.Equal("partial-batch-item", item.RunId);
        Assert.NotNull(item.ArtifactDirectory);
        Assert.Contains("media-url", item.Error);

        var queue = new AnalysisQueue(PipelineFactory, temp.GetPath("queues"));
        await queue.EnqueueAsync("q", AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "partial-queue-item") with { IncludeTranscript = true, FrameCount = 2 }, "job", retries: 0, CancellationToken.None);
        await queue.RunAsync("q", new AnalysisQueueRunOptions(Concurrency: 1, Retries: 0), progress: null, CancellationToken.None);
        var status = await queue.GetStatusAsync("q", CancellationToken.None);
        var attempt = Assert.Single(status.Jobs.Single().AttemptHistory);
        Assert.Equal(AnalysisQueueJobStatuses.Failed, attempt.Status);
        Assert.Equal("partial-queue-item", attempt.RunId);
        Assert.NotNull(attempt.ArtifactDirectory);
    }

    [Fact]
    public void JsonEnvelopeCarriesStatusAndFailure()
    {
        var run = new VideoRun("r", Path.Combine(Path.GetTempPath(), "r"));
        var manifest = AnalysisPipelineTests.CreateManifest("src", "r", DateTimeOffset.UnixEpoch) with
        {
            Status = RunStatuses.Partial,
            TranscriptPath = "transcript.md",
            Failure = new RunFailure(RunStages.Frames, "ffmpeg died", "ProcessFailedException")
        };

        var json = JsonSerializer.Serialize(Cli.CliApp.BuildAnalyzeJsonEnvelope(new AnalyzeResult(run, manifest), "ffmpeg died"), Cli.CliJson.Options);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("frames", document.RootElement.GetProperty("failure").GetProperty("stage").GetString());
        Assert.Equal("ffmpeg died", document.RootElement.GetProperty("error").GetString());
        Assert.EndsWith("transcript.md", document.RootElement.GetProperty("transcriptPath").GetString());
    }

    private static string SchemaPath(string fileName)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", fileName));

    private sealed class ScriptedYtDlp : IYtDlpClient
    {
        public string? Captions { get; init; }
        public Exception? InfoFailure { get; init; }
        public Exception? MediaUrlFailure { get; set; }
        public int InfoCalls { get; private set; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            InfoCalls++;
            if (InfoFailure is not null)
            {
                throw InfoFailure;
            }

            return Task.FromResult(new YtDlpInfo { Id = "abc", Title = "Yogurt", WebpageUrl = request.Source, DurationSeconds = 120 });
        }

        public async Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        {
            if (Captions is null)
            {
                return null;
            }

            var captionPath = run.GetPath(Path.Combine("captions", "subtitle.en.vtt"));
            await File.WriteAllTextAsync(captionPath, Captions, cancellationToken);
            var segments = SubtitleConverter.ParseSegments(Captions);
            var markdownPath = run.GetPath("transcript.md");
            await File.WriteAllTextAsync(markdownPath, SubtitleConverter.ToMarkdown(segments), cancellationToken);
            return new TranscriptArtifact(captionPath, markdownPath, "yt-dlp-subtitle", segments);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            if (MediaUrlFailure is not null)
            {
                throw MediaUrlFailure;
            }

            return Task.FromResult<string?>("https://cdn.example/video.mp4");
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class ScriptedFfmpeg : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([new FrameArtifact("frame-001", "frames/frame-001.jpg", 1, "00:01")]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("no audio");
        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => throw new ReplayException("no clip");
        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult<double?>(120);
        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
