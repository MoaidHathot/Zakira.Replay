using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Short sources are fetched once and processed locally. Every frame capture is its own ffmpeg
/// process that re-opens the remote URL (input-side -ss), so a two-minute short analysed with
/// 15 frames meant 15 remote opens against a signed googlevideo URL - each a fresh chance for a
/// 403/429 - while the whole file is a few megabytes. The threshold lives in
/// <c>capture.autoDownloadMaxDurationSeconds</c> (default 15 min, 0 disables).
/// </summary>
public sealed class AutoDownloadTests
{
    [Theory]
    [InlineData(900, 4, 60.0, 15, true)]
    [InlineData(900, 4, 900.0, 4, true)]
    [InlineData(900, 4, 901.0, 15, false)]
    [InlineData(900, 4, 60.0, 3, false)]
    [InlineData(0, 4, 60.0, 15, false)]
    [InlineData(900, 4, null, 15, false)]
    [InlineData(900, 1, 60.0, 1, true)]
    public void PolicyFollowsDurationAndFrameCount(int maxDuration, int minFrames, double? duration, int frames, bool expected)
    {
        var capture = new CaptureConfig { AutoDownloadMaxDurationSeconds = maxDuration, AutoDownloadMinFrames = minFrames };
        Assert.Equal(expected, AutoDownloadPolicy.ShouldDownload(capture, duration, frames));
    }

    [Fact]
    public async Task ShortRemoteSourceIsDownloadedOnceAndFramesComeFromTheLocalFile()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { DurationSeconds = 90, BestMediaUrl = "https://cdn.example/short.mp4", ProvideDownload = true };
        var ffmpeg = new RecordingFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest("https://www.youtube.com/shorts/abc", "short") with { FrameCount = 15 }, progress: null, CancellationToken.None);

        Assert.Equal(1, ytDlp.DownloadCalls);
        Assert.EndsWith(Path.Combine("media", "source.mp4"), ffmpeg.LastFrameSource);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaAutoDownloaded && w.Severity == ReplayWarningSeverities.Info);
        Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaDownloadDeclined);
        Assert.Equal(RunStatuses.Completed, result.Manifest.Status);
    }

    [Fact]
    public async Task LongRemoteSourceStillSeeksTheRemoteUrl()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { DurationSeconds = 3600, BestMediaUrl = "https://cdn.example/long.mp4", ProvideDownload = true };
        var ffmpeg = new RecordingFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=long", "long") with { FrameCount = 15 }, progress: null, CancellationToken.None);

        Assert.Equal(0, ytDlp.DownloadCalls);
        Assert.Equal("https://cdn.example/long.mp4", ffmpeg.LastFrameSource);
        Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaAutoDownloaded);
    }

    [Fact]
    public async Task FailedAutoDownloadFallsBackToTheRemoteUrlWithAnInfoWarning()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { DurationSeconds = 90, BestMediaUrl = "https://cdn.example/short.mp4", ProvideDownload = false };
        var ffmpeg = new RecordingFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest("https://www.youtube.com/shorts/abc", "short-nodl") with { FrameCount = 15 }, progress: null, CancellationToken.None);

        Assert.Equal(1, ytDlp.DownloadCalls);
        Assert.Equal("https://cdn.example/short.mp4", ffmpeg.LastFrameSource);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaAutoDownloadFailed && w.Severity == ReplayWarningSeverities.Info);
        Assert.Single(result.Manifest.Frames);
    }

    [Fact]
    public async Task ZeroThresholdDisablesAutoDownload()
    {
        using var temp = new TestTempDirectory();
        var configFile = temp.GetPath("Zakira.Replay.json");
        await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new { capture = new { autoDownloadMaxDurationSeconds = 0 } }));
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            var store = new ArtifactStore(temp.GetPath("runs"));
            var ytDlp = new ScriptedYtDlp { DurationSeconds = 90, BestMediaUrl = "https://cdn.example/short.mp4", ProvideDownload = true };
            var ffmpeg = new RecordingFfmpeg();
            var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

            await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest("https://www.youtube.com/shorts/abc", "short-disabled") with { FrameCount = 15 }, progress: null, CancellationToken.None);

            Assert.Equal(0, ytDlp.DownloadCalls);
            Assert.Equal("https://cdn.example/short.mp4", ffmpeg.LastFrameSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previous);
        }
    }

    [Fact]
    public async Task FrameCaptureDownloadsShortSourcesForWindowsButNotForAFewSpotFrames()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new ScriptedYtDlp { DurationSeconds = 60, BestMediaUrl = "https://cdn.example/short.mp4", ProvideDownload = true };
        var ffmpeg = new RecordingFfmpeg { ProbedDuration = 60 };
        var service = new FrameCaptureService(store, ytDlp, ffmpeg);

        var window = await service.CaptureAsync(new FrameCaptureRequest(Source: "https://www.youtube.com/shorts/abc", RangeStart: TimeSpan.Zero, RangeEnd: TimeSpan.FromSeconds(30), RangeCount: 12, RunId: "fc-window"), progress: null, CancellationToken.None);
        Assert.Equal(1, ytDlp.DownloadCalls);
        Assert.EndsWith(Path.Combine("media", "source.mp4"), ffmpeg.LastFrameSource);
        Assert.Contains(window.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaAutoDownloaded);

        var spot = await service.CaptureAsync(new FrameCaptureRequest(Source: "https://www.youtube.com/shorts/abc", Timestamps: [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)], RunId: "fc-spot"), progress: null, CancellationToken.None);
        Assert.Equal(1, ytDlp.DownloadCalls);
        Assert.Equal("https://cdn.example/short.mp4", ffmpeg.LastFrameSource);
        Assert.DoesNotContain(spot.Manifest.Warnings, w => w.Code == ReplayWarningCodes.MediaAutoDownloaded);
    }

    [Fact]
    public async Task CaptureConfigKeysRoundTrip()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("capture.allowMediaDownload", "true", CancellationToken.None);
        await store.SetAsync("capture.autoDownloadMaxDurationSeconds", "0", CancellationToken.None);
        await store.SetAsync("capture.auto-download-min-frames", "2", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.True(config.Capture.AllowMediaDownload);
        Assert.Equal(0, config.Capture.AutoDownloadMaxDurationSeconds);
        Assert.Equal(2, config.Capture.AutoDownloadMinFrames);
        Assert.Equal("True", await store.GetAsync("capture.allowMediaDownload", CancellationToken.None));
        Assert.Equal("0", await store.GetAsync("capture.autoDownloadMaxDurationSeconds", CancellationToken.None));
        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("capture.autoDownloadMaxDurationSeconds", "-5", CancellationToken.None));
    }

    private sealed class ScriptedYtDlp : IYtDlpClient
    {
        public double DurationSeconds { get; init; }
        public string? BestMediaUrl { get; init; }
        public bool ProvideDownload { get; init; }
        public int DownloadCalls { get; private set; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new YtDlpInfo { Id = "abc", Title = "Short", WebpageUrl = request.Source, DurationSeconds = DurationSeconds });

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => Task.FromResult<TranscriptArtifact?>(null);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult(BestMediaUrl);

        public async Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (!ProvideDownload)
            {
                return null;
            }

            var path = run.GetPath(Path.Combine("media", "source.mp4"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, new string('x', 2048), cancellationToken);
            return path;
        }
    }

    private sealed class RecordingFfmpeg : IFfmpegClient
    {
        public double? ProbedDuration { get; init; }
        public string? LastFrameSource { get; private set; }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
        {
            LastFrameSource = mediaSource;
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([new FrameArtifact("frame-001", "frames/frame-001.jpg", 1, "00:01")]);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            LastFrameSource = mediaSource;
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
        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("no audio");
        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => throw new ReplayException("no clip");
        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult(ProbedDuration);
        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
