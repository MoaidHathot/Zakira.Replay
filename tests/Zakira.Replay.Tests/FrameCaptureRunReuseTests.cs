using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Tests;

/// <summary>
/// <c>frames</c> against a completed run. The tool used to re-resolve the video from scratch on
/// every call (a fresh yt-dlp extraction, a remote ffprobe, then one remote seek per frame) even
/// though the analyze run next to it already held the downloaded media, the yt-dlp dump and the
/// duration. It also only existed as a blocking MCP call, and the MCP variant had no
/// allowMediaDownload switch and no browser fallback (DI omitted the browser client).
/// </summary>
public sealed class FrameCaptureRunReuseTests
{
    [Fact]
    public async Task RunIdAloneResolvesTheSourceAndReusesDownloadedMedia()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://www.youtube.com/watch?v=abc", "analyzed");
        await store.WriteJsonAsync(run, "request.json", AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "analyzed"), CancellationToken.None);
        await store.WriteJsonAsync(run, "metadata.json", new YtDlpInfo { Id = "abc", DurationSeconds = 90 }, CancellationToken.None);
        var downloaded = run.GetPath(Path.Combine("media", "source.mp4"));
        await File.WriteAllTextAsync(downloaded, "fake media", CancellationToken.None);
        var ytDlp = new ThrowingYtDlp();
        var ffmpeg = new RecordingFfmpeg { ProbedDuration = 90 };
        var service = new FrameCaptureService(store, ytDlp, ffmpeg);

        var result = await service.CaptureAsync(new FrameCaptureRequest(Source: "", RunId: "analyzed", Timestamps: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(50)]), progress: null, CancellationToken.None);

        Assert.Equal("analyzed", result.Run.Id);
        Assert.Equal("https://www.youtube.com/watch?v=abc", result.Manifest.Source);
        Assert.Equal(downloaded, ffmpeg.LastMediaSource);
        Assert.Equal(2, result.Manifest.Frames.Count);
        Assert.False(ytDlp.WasCalled, "yt-dlp must not run when the run already holds the media");
        Assert.DoesNotContain(result.Manifest.Warnings, w => w.Severity == ReplayWarningSeverities.Error);
    }

    [Fact]
    public async Task RunWithDumpUsesItForTheUrlAndTheRecordedDurationInsteadOfProbing()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://www.youtube.com/watch?v=abc", "analyzed-dump");
        await store.WriteJsonAsync(run, "request.json", AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "analyzed-dump"), CancellationToken.None);
        await store.WriteJsonAsync(run, "metadata.json", new YtDlpInfo { Id = "abc", DurationSeconds = 30 }, CancellationToken.None);
        await File.WriteAllTextAsync(run.GetPath(YtDlpInfoJson.FileName), """{"id":"abc","formats":[{"url":"https://cdn.example/v.mp4"}]}""", CancellationToken.None);
        var ytDlp = new RecordingYtDlp { BestMediaUrl = "https://cdn.example/v.mp4" };
        var ffmpeg = new RecordingFfmpeg { ProbedDuration = null, ThrowOnProbe = true };
        var service = new FrameCaptureService(store, ytDlp, ffmpeg);

        var result = await service.CaptureAsync(new FrameCaptureRequest(Source: "", RunId: "analyzed-dump", Timestamps: [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(500)]), progress: null, CancellationToken.None);

        Assert.Equal(run.GetPath(YtDlpInfoJson.FileName), ytDlp.LastInfoJsonPath);
        Assert.Equal("https://cdn.example/v.mp4", ffmpeg.LastMediaSource);
        // Duration 30 s came from metadata.json (ffprobe would have thrown): the 500 s timestamp is clamped away.
        Assert.Single(result.Manifest.Frames);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureTimestampOutOfRange);
    }

    [Fact]
    public async Task UnknownRunWithoutSourceFailsWithGuidance()
    {
        using var temp = new TestTempDirectory();
        var service = new FrameCaptureService(new ArtifactStore(temp.GetPath("runs")), new ThrowingYtDlp(), new RecordingFfmpeg());

        var ex = await Assert.ThrowsAsync<ReplayException>(() => service.CaptureAsync(new FrameCaptureRequest(Source: "", RunId: "nope", Timestamps: [TimeSpan.FromSeconds(1)]), progress: null, CancellationToken.None));

        Assert.Contains("nope", ex.Message);
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void DiWiresTheBrowserFallbackIntoTheMcpFrameCaptureService()
    {
        var services = new ServiceCollection();
        services.AddReplay();
        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<FrameCaptureService>().HasBrowserFallback);
    }

    [Fact]
    public async Task FrameJobsRunInTheBackgroundPersistAndRestore()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "fake", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var jobsDirectory = temp.GetPath("frame-jobs");
        var manager = new McpFrameJobManager(() => new FrameCaptureService(store, new ThrowingYtDlp(), new RecordingFfmpeg { ProbedDuration = 60 }), jobsDirectory);

        var job = manager.Create(new FrameCaptureRequest(Source: sourcePath, Timestamps: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], RunId: "bg"));
        var snapshot = await WaitForTerminalAsync(job);

        Assert.Equal("succeeded", snapshot.Status);
        Assert.NotNull(snapshot.Manifest);
        Assert.Equal(2, snapshot.Manifest.Frames.Count);
        Assert.Equal("bg", snapshot.RunId);
        Assert.True(File.Exists(Path.Combine(jobsDirectory, job.JobId + ".json")));

        var restored = new McpFrameJobManager(() => throw new InvalidOperationException("no rerun"), jobsDirectory).Get(job.JobId)?.Snapshot(includeLogs: false);
        Assert.NotNull(restored);
        Assert.Equal("succeeded", restored.Status);
        Assert.Equal(2, restored.Manifest!.Frames.Count);
        Assert.False(manager.Cancel("unknown"));
    }

    [Fact]
    public async Task FrameJobFailureIsReportedNotThrown()
    {
        using var temp = new TestTempDirectory();
        var manager = new McpFrameJobManager(() => new ExplodingFrameCapture(), temp.GetPath("frame-jobs"));

        var job = manager.Create(new FrameCaptureRequest(Source: "https://example.test/v", Timestamps: [TimeSpan.FromSeconds(1)]));
        var snapshot = await WaitForTerminalAsync(job);

        Assert.Equal("failed", snapshot.Status);
        Assert.Contains("boom", snapshot.Error);
    }

    [Fact]
    public async Task McpExposesAsyncFrameToolsAndTheBlockingToolAcceptsRunIdOnly()
    {
        await using var harness = await McpTestHarness.CreateAsync();
        var tools = await harness.Client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("frames-start", names);
        Assert.Contains("frames-status", names);
        Assert.Contains("frames-result", names);
        Assert.Contains("frames-cancel", names);

        var frames = tools.Single(tool => tool.Name == "frames");
        var schema = frames.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("allowMediaDownload", out _), "frames must expose allowMediaDownload like the CLI");
        Assert.True(properties.TryGetProperty("runId", out _));
        var required = schema.TryGetProperty("required", out var requiredElement) ? requiredElement.EnumerateArray().Select(e => e.GetString()).ToArray() : [];
        Assert.DoesNotContain("source", required);
    }

    private static async Task<McpFrameJobSnapshot> WaitForTerminalAsync(McpFrameJob job)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = job.Snapshot(includeLogs: true);
            if (snapshot.Status is "succeeded" or "failed" or "cancelled")
            {
                return snapshot;
            }

            await Task.Delay(25, CancellationToken.None);
        }

        throw new TimeoutException("frame job did not finish");
    }

    private sealed class ThrowingYtDlp : IYtDlpClient
    {
        public bool WasCalled { get; private set; }
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken) { WasCalled = true; throw new ReplayException("yt-dlp should not run"); }
        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) { WasCalled = true; throw new ReplayException("yt-dlp should not run"); }
        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) { WasCalled = true; throw new ReplayException("yt-dlp should not run"); }
        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) { WasCalled = true; throw new ReplayException("yt-dlp should not run"); }
    }

    private sealed class RecordingYtDlp : IYtDlpClient
    {
        public string? BestMediaUrl { get; init; }
        public string? LastInfoJsonPath { get; private set; }
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult(new YtDlpInfo());
        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => Task.FromResult<TranscriptArtifact?>(null);
        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => GetBestMediaUrlAsync(request, null, cancellationToken);
        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, string? infoJsonPath, CancellationToken cancellationToken)
        {
            LastInfoJsonPath = infoJsonPath;
            return Task.FromResult(BestMediaUrl);
        }
        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class ExplodingFrameCapture : IFrameCaptureService
    {
        public Task<FrameCaptureResult> CaptureAsync(FrameCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken) => throw new ReplayException("boom");
    }

    private sealed class RecordingFfmpeg : IFfmpegClient
    {
        public double? ProbedDuration { get; init; }
        public bool ThrowOnProbe { get; init; }
        public string? LastMediaSource { get; private set; }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            LastMediaSource = mediaSource;
            var frames = timestamps.Select((t, i) =>
            {
                var relative = $"frames/frame-{i + 1:000}.jpg";
                var absolute = run.GetPath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.WriteAllText(absolute, "jpg");
                return new FrameArtifact($"frame-{i + 1:000}", relative, t.TotalSeconds, Timestamp.Format(t.TotalSeconds));
            }).ToList();
            return Task.FromResult<IReadOnlyList<FrameArtifact>>(frames);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => Task.FromResult("audio/audio.wav");
        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => Task.FromResult("clips/clip.mp4");
        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
            => ThrowOnProbe ? throw new ReplayException("ffprobe should not run for a remote source when the run recorded the duration") : Task.FromResult(ProbedDuration);
        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
