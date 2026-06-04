using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class FrameCaptureTests
{
    [Fact]
    public async Task TimestampModeWritesFramesAndManifest()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient();
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            Timestamps:
            [
                TimeSpan.FromSeconds(2.5),
                TimeSpan.FromSeconds(7),
                TimeSpan.FromSeconds(11.25)
            ],
            RunId: "ts-mode",
            MaxLongEdgePixels: 640,
            JpegQuality: 85);

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(3, result.Manifest.Frames.Count);
        Assert.Equal("frame-capture", result.Manifest.Kind);
        Assert.Equal(FrameSelectionStrategies.Timestamps, result.Manifest.Request.Mode);
        Assert.True(File.Exists(result.Run.GetPath("frame-capture.json")));
        // Round-trip the manifest to verify it is JSON-serialisable.
        var serialised = JsonSerializer.Serialize(result.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"kind\":\"frame-capture\"", serialised);
        Assert.Equal(640, ffmpeg.LastOptions?.MaxLongEdgePixels);
        Assert.Equal(85, ffmpeg.LastOptions?.JpegQuality);
        Assert.Single(ffmpeg.TimestampsCalls);
        Assert.Equal(new[] { 2.5, 7, 11.25 }, ffmpeg.TimestampsCalls[0].Select(t => t.TotalSeconds).ToArray());
    }

    [Fact]
    public async Task TimestampOutOfRangeIsDroppedWithWarning()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 10.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            Timestamps:
            [
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(99)
            ],
            RunId: "ts-clamp");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Single(ffmpeg.TimestampsCalls);
        Assert.Single(ffmpeg.TimestampsCalls[0]);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureTimestampOutOfRange);
    }

    [Fact]
    public async Task ExcessTimestampsAreTrimmedWithWarning()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 1000.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var manyTimestamps = Enumerable.Range(1, FrameCaptureService.MaxTimestampsPerRequest + 5)
            .Select(i => TimeSpan.FromSeconds(i))
            .ToList();
        var request = new FrameCaptureRequest(
            Source: sourcePath,
            Timestamps: manyTimestamps,
            RunId: "ts-overflow");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureTooManyTimestamps);
        Assert.Equal(FrameCaptureService.MaxTimestampsPerRequest, ffmpeg.TimestampsCalls[0].Count);
    }

    [Fact]
    public async Task RangeIntervalModePassesEvenlySpacedTimestampsInclusiveOfEndpoints()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 120.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            RangeStart: TimeSpan.FromSeconds(10),
            RangeEnd: TimeSpan.FromSeconds(30),
            RangeCount: 5,
            RangeStrategy: FrameSelectionStrategies.Interval,
            RunId: "range-interval");

        await service.CaptureAsync(request, progress: null, CancellationToken.None);

        var ts = ffmpeg.TimestampsCalls[0].Select(t => t.TotalSeconds).ToArray();
        Assert.Equal(new[] { 10.0, 15.0, 20.0, 25.0, 30.0 }, ts);
    }

    [Fact]
    public async Task RangeSceneModeDelegatesToScopedSceneExtractor()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient
        {
            ProbedDuration = 200.0,
            SceneFramesProducer = (start, _, _) =>
            [
                new FrameArtifact("scene-0001", "frames/range-scene-0001.jpg", start.TotalSeconds + 1, "00:00")
            ]
        };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            RangeStart: TimeSpan.FromSeconds(60),
            RangeEnd: TimeSpan.FromSeconds(90),
            RangeStrategy: FrameSelectionStrategies.Scene,
            RangeCount: 5,
            RunId: "range-scene");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Single(ffmpeg.SceneInRangeCalls);
        Assert.Equal(TimeSpan.FromSeconds(60), ffmpeg.SceneInRangeCalls[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(90), ffmpeg.SceneInRangeCalls[0].End);
        Assert.Single(result.Manifest.Frames);
    }

    [Fact]
    public async Task RangeEndClampsToSourceDurationWithWarning()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 30.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            RangeStart: TimeSpan.FromSeconds(5),
            RangeEnd: TimeSpan.FromSeconds(120),
            RangeCount: 2,
            RangeStrategy: FrameSelectionStrategies.Interval,
            RunId: "range-clamp");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureRangeOutOfBounds);
        var ts = ffmpeg.TimestampsCalls[0].Select(t => t.TotalSeconds).ToArray();
        Assert.Equal(new[] { 5.0, 30.0 }, ts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ValidationRejectsMutuallyExclusiveOrEmptyInputs(bool withTimestamps, bool withRange)
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient();
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        FrameCaptureRequest request;
        if (withTimestamps && withRange)
        {
            request = new FrameCaptureRequest(
                Source: sourcePath,
                Timestamps: [TimeSpan.FromSeconds(1)],
                RangeStart: TimeSpan.FromSeconds(0),
                RangeEnd: TimeSpan.FromSeconds(5));
        }
        else if (withTimestamps)
        {
            request = new FrameCaptureRequest(Source: sourcePath, Timestamps: []);
        }
        else
        {
            request = new FrameCaptureRequest(Source: sourcePath);
        }

        await Assert.ThrowsAsync<ReplayException>(() => service.CaptureAsync(request, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task RangeStrategyDefaultsToIntervalWhenUnspecified()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 60.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            RangeStart: TimeSpan.FromSeconds(0),
            RangeEnd: TimeSpan.FromSeconds(10),
            RangeCount: 3,
            RunId: "default-strategy");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(FrameSelectionStrategies.Interval, result.Manifest.Request.RangeStrategy);
        Assert.Single(ffmpeg.TimestampsCalls);
        Assert.Empty(ffmpeg.SceneInRangeCalls);
    }

    [Fact]
    public async Task ComputePerceptualHashEnrichesEachFrame()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 30.0 };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg);

        var request = new FrameCaptureRequest(
            Source: sourcePath,
            Timestamps: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            ComputePerceptualHash: true,
            RunId: "phash-on");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.All(result.Manifest.Frames, f => Assert.False(string.IsNullOrEmpty(f.PerceptualHash)));
    }

    [Fact]
    public async Task RemoteSourceFallsBackToDownloadWhenDirectUrlMissing()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var downloadedPath = temp.GetPath("downloaded.mp4");
        await File.WriteAllTextAsync(downloadedPath, "not real video", CancellationToken.None);
        var ytDlp = new FakeYtDlpClient { DownloadedMediaPath = downloadedPath };
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 5.0 };
        var service = new FrameCaptureService(store, ytDlp, ffmpeg);

        var request = new FrameCaptureRequest(
            Source: "https://example.com/video",
            Timestamps: [TimeSpan.FromSeconds(1)],
            RunId: "remote-download");

        var result = await service.CaptureAsync(request, progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureMediaUrlUnresolved && w.Severity == ReplayWarningSeverities.Info);
        Assert.Equal(downloadedPath, ffmpeg.LastMediaSource);
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(85, 6)]
    [InlineData(1, 31)]
    [InlineData(null, 2)]
    public void MapJpegQualityToQscaleMatchesExpectedRange(int? quality, int expected)
    {
        Assert.Equal(expected, FfmpegClient.MapJpegQualityToQscale(quality));
    }

    [Fact]
    public void ParseTimestampsHandlesMixedFormats()
    {
        var parsed = FrameCaptureInput.ParseTimestamps("02:34, 154.5, 01:02:03", "at");

        Assert.Equal(3, parsed.Count);
        Assert.Equal(154.0, parsed[0].TotalSeconds);
        Assert.Equal(154.5, parsed[1].TotalSeconds);
        Assert.Equal(3723.0, parsed[2].TotalSeconds);
    }

    private sealed class FakeYtDlpClient : IYtDlpClient
    {
        public string? BestMediaUrl { get; init; }

        public string? DownloadedMediaPath { get; init; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new YtDlpInfo());

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
            => Task.FromResult<TranscriptArtifact?>(null);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(BestMediaUrl);

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
            => Task.FromResult(DownloadedMediaPath);
    }

    /// <summary>
    /// Stand-in browser client. Returns whatever <see cref="InlineMediaUrl"/> is configured and
    /// records the request it received so tests can assert MetadataOnly is set (the spot-frame
    /// fast path) instead of falling back to a full capture.
    /// </summary>
    private sealed class FakeBrowserCaptureClientForSpotFrames : IBrowserVideoCaptureClient
    {
        public string? InlineMediaUrl { get; init; }
        public BrowserCaptureRequest? LastRequest { get; private set; }

        public Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new BrowserCaptureResult(
                Frames: [],
                DurationSeconds: null,
                Warnings: [],
                Captions: [],
                InlineMediaUrl: InlineMediaUrl));
        }
    }

    [Fact]
    public async Task BrowserProbeResolvesMediaUrlWhenYtDlpFails()
    {
        // Simulates the Build/Medius case: yt-dlp returns null for the source, the registered
        // browser client comes back with an inline HLS URL, and ffmpeg gets that URL — no local
        // download fallback, no FrameCaptureMediaUrlUnresolved error.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient();
        var browser = new FakeBrowserCaptureClientForSpotFrames
        {
            InlineMediaUrl = "https://stream.event.microsoft.com/prodwe/Content/HLS/VOD/x/y/master.m3u8"
        };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg, browser);

        var result = await service.CaptureAsync(new FrameCaptureRequest(
            Source: "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions",
            Timestamps: [TimeSpan.FromSeconds(120)],
            RunId: "browser-probe-fallback"), progress: null, CancellationToken.None);

        // Browser was invoked in MetadataOnly mode (fast path: ~3-5s for Medius vs ~25s full).
        Assert.NotNull(browser.LastRequest);
        Assert.True(browser.LastRequest!.MetadataOnly);
        // ffmpeg got the HLS URL straight from the inline interceptor — no local download.
        Assert.Equal("https://stream.event.microsoft.com/prodwe/Content/HLS/VOD/x/y/master.m3u8", ffmpeg.LastMediaSource);
        Assert.Single(result.Manifest.Frames);
        // No "media URL unresolved" since the browser path succeeded.
        Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureMediaUrlUnresolved);
    }

    [Fact]
    public async Task BrowserProbeFallsThroughToLocalDownloadWhenNoInlineUrlFound()
    {
        // When yt-dlp can't resolve AND the browser probe also returns no inline URL, the
        // service falls through to the local-download path with the historical info warning.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient();
        var browser = new FakeBrowserCaptureClientForSpotFrames { InlineMediaUrl = null };
        var ytDlp = new FakeYtDlpClient { DownloadedMediaPath = "media/downloaded.mp4" };
        var service = new FrameCaptureService(store, ytDlp, ffmpeg, browser);

        var result = await service.CaptureAsync(new FrameCaptureRequest(
            Source: "https://somewhere.test/private-thing",
            Timestamps: [TimeSpan.FromSeconds(5)],
            RunId: "browser-probe-noop"), progress: null, CancellationToken.None);

        Assert.Equal("media/downloaded.mp4", ffmpeg.LastMediaSource);
        // The info warning explains the fallback to local download.
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.FrameCaptureMediaUrlUnresolved);
    }

    [Fact]
    public async Task BrowserProbeIsSkippedForLocalFiles()
    {
        // Local file paths never need a browser probe; the source IS the media. The browser
        // client must not be invoked at all.
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "fake media", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ffmpeg = new RecordingFfmpegClient();
        var browser = new FakeBrowserCaptureClientForSpotFrames { InlineMediaUrl = "would-be-wrong.m3u8" };
        var service = new FrameCaptureService(store, new FakeYtDlpClient(), ffmpeg, browser);

        await service.CaptureAsync(new FrameCaptureRequest(
            Source: sourcePath,
            Timestamps: [TimeSpan.FromSeconds(1)],
            RunId: "local-no-browser"), progress: null, CancellationToken.None);

        Assert.Null(browser.LastRequest);
        Assert.Equal(sourcePath, ffmpeg.LastMediaSource);
    }

    private sealed class RecordingFfmpegClient : IFfmpegClient
    {
        public double? ProbedDuration { get; init; }

        public Func<TimeSpan, TimeSpan, int, IReadOnlyList<FrameArtifact>>? SceneFramesProducer { get; init; }

        public FrameCaptureOptions? LastOptions { get; private set; }

        public string? LastMediaSource { get; private set; }

        public List<IReadOnlyList<TimeSpan>> TimestampsCalls { get; } = [];

        public List<(TimeSpan Start, TimeSpan End)> SceneInRangeCalls { get; } = [];

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            LastMediaSource = mediaSource;
            LastOptions = options;
            TimestampsCalls.Add([.. timestamps]);
            var frames = new List<FrameArtifact>(timestamps.Count);
            for (var i = 0; i < timestamps.Count; i++)
            {
                var seconds = timestamps[i].TotalSeconds;
                var label = Timestamp.Format(seconds);
                var fileSafeLabel = label.Replace(':', '-').Replace('.', '-');
                var relativePath = $"frames/frame-{i + 1:000}-{fileSafeLabel}.jpg";
                // Write a placeholder file so any downstream existence check passes.
                var absolute = run.GetPath(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.WriteAllText(absolute, "fake jpeg");
                frames.Add(new FrameArtifact($"frame-{i + 1:000}", relativePath, seconds, label));
            }
            return Task.FromResult<IReadOnlyList<FrameArtifact>>(frames);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            LastMediaSource = mediaSource;
            LastOptions = options;
            SceneInRangeCalls.Add((rangeStart, rangeEnd));
            var frames = SceneFramesProducer?.Invoke(rangeStart, rangeEnd, sceneSafetyCap) ?? [];
            return Task.FromResult(frames);
        }

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
            => Task.FromResult("audio/audio.wav");

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
            => Task.FromResult("clips/clip.mp4");

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
            => Task.FromResult(ProbedDuration);

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>("abcdef0123456789");
    }
}
