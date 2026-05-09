using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class AnalysisPipelineTests
{
    [Fact]
    public async Task AnalyzeAsyncReusesExistingRunWhenManifestExistsAndForceIsFalse()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var existingRun = store.CreateRun(sourcePath, "reuse-me");
        var existingManifest = CreateManifest(sourcePath, existingRun.Id, createdAt: DateTimeOffset.UnixEpoch);
        await store.WriteJsonAsync(existingRun, "manifest.json", existingManifest, CancellationToken.None);
        var pipeline = CreatePipeline(store);
        var progress = new RecordingProgress();

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "reuse-me"), progress, CancellationToken.None);

        Assert.True(result.Reused);
        Assert.Equal(existingRun.Id, result.Run.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.Manifest.CreatedAt);
        Assert.Contains(progress.Messages, log => log.Contains("Reusing existing run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncRecomputesExistingRunWhenForceIsTrue()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var existingRun = store.CreateRun(sourcePath, "force-me");
        await store.WriteJsonAsync(existingRun, "manifest.json", CreateManifest(sourcePath, existingRun.Id, createdAt: DateTimeOffset.UnixEpoch), CancellationToken.None);
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "force-me") with { Force = true }, progress: null, CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Equal(existingRun.Id, result.Run.Id);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, result.Manifest.CreatedAt);
        Assert.Equal("source", result.Manifest.Title);
        Assert.Equal("evidence.json", result.Manifest.EvidencePath);
    }

    [Fact]
    public async Task AnalyzeAsyncFallsBackToDownloadedMediaWhenRemoteFrameExtractionFails()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var downloadedMedia = temp.GetPath("downloaded.mp4");
        await File.WriteAllTextAsync(downloadedMedia, "fallback media", CancellationToken.None);
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Remote Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 9
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4",
            DownloadedMediaPath = downloadedMedia
        };
        var ffmpeg = new FakeFfmpegClient
        {
            FailingFrameSource = ytDlp.BestMediaUrl
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            Instruction: "Test remote fallback",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "remote-fallback"), progress: null, CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Equal(1, ytDlp.DownloadMediaCallCount);
        Assert.Equal(new[] { ytDlp.BestMediaUrl, downloadedMedia }, ffmpeg.FrameSources);
        Assert.Single(result.Manifest.Frames);
        Assert.Contains(result.Manifest.Warnings, warning => warning.Contains("Direct remote frame extraction failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncReusesCachedRunWhenCacheIsEnabledAndRunIdIsEmpty()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);
        var request = CreateRequest(sourcePath, runId: string.Empty) with { UseCache = true };

        var first = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);
        var second = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.Run.Id, second.Run.Id);
    }

    [Fact]
    public async Task AnalyzeAsyncPassesRequestedFrameStrategyToFfmpeg()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Remote Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 9
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            Instruction: "Test scene strategy",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "scene-strategy",
            FrameStrategy: FrameSelectionStrategies.Scene), progress: null, CancellationToken.None);

        Assert.Equal(FrameSelectionStrategies.Scene, ffmpeg.FrameStrategies.Single());
    }

    [Fact]
    public async Task AnalyzeAsyncPassesEveryFrameStrategyToFfmpeg()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Remote Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 9
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            Instruction: "Test every-frame strategy",
            IncludeTranscript: false,
            FrameCount: 2,
            RunId: "every-frame-strategy",
            FrameStrategy: FrameSelectionStrategies.EveryFrame), progress: null, CancellationToken.None);

        Assert.Equal(FrameSelectionStrategies.EveryFrame, ffmpeg.FrameStrategies.Single());
    }

    [Fact]
    public async Task AnalyzeAsyncUsesRequestedLlmProviderWhenAiWorkRuns()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var llm = new FakeLlmProvider("openai");
        var pipeline = new AnalysisPipeline(store, new FakeYtDlpClient(), new FakeFfmpegClient(), provider => provider == "openai" ? llm : null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            Instruction: "Test summary provider",
            IncludeTranscript: false,
            FrameCount: 0,
            RunId: "llm-provider",
            UseSummary: true,
            Model: "gpt-4o-mini",
            LlmProvider: "openai"), progress: null, CancellationToken.None);

        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        Assert.NotNull(evidence);
        Assert.Equal("fake response", evidence.Summary);
        Assert.Equal("gpt-4o-mini", llm.Requests.Single().Model);
    }

    [Fact]
    public async Task AnalyzeAsyncNormalizesLocalVttSidecarWithoutLosingUniqueWords()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("sidecar.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        await File.WriteAllTextAsync(temp.GetPath("sidecar.vtt"), """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            Router setup begins

            00:00:01.500 --> 00:00:04.000
            Router setup begins with WireGuard VPN

            00:00:04.000 --> 00:00:06.000
            WireGuard VPN performance testing

            00:00:20.000 --> 00:00:22.000
            Router setup begins
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "local-vtt") with { IncludeTranscript = true }, progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        var transcriptMarkdown = await File.ReadAllTextAsync(result.Run.GetPath("transcript.md"), CancellationToken.None);
        var rawTranscriptMarkdown = await File.ReadAllTextAsync(result.Run.GetPath("transcript/raw.md"), CancellationToken.None);
        var normalizationReport = await store.ReadJsonAsync<TranscriptNormalizationReport>(result.Run, "transcript/normalization.json", CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.NotNull(normalizationReport);
        Assert.Equal(2, evidence.Transcript.Count);
        Assert.Equal(4, normalizationReport.RawSegmentCount);
        Assert.Equal(2, normalizationReport.NormalizedSegmentCount);
        Assert.Equal(2, normalizationReport.MergeCount);
        Assert.Contains(normalizationReport.Merges, merge => merge.Reason == "growing-caption");
        Assert.Contains(normalizationReport.Merges, merge => merge.Reason == "overlapping-continuation");
        Assert.Contains(evidence.Transcript, segment => segment.Text == "Router setup begins with WireGuard VPN performance testing");
        Assert.Contains(evidence.Transcript, segment => segment.StartSeconds == 20 && segment.Text == "Router setup begins");
        Assert.True(File.Exists(result.Run.GetPath("transcript/raw.json")));
        Assert.Contains("**[00:00:00.000 - 00:00:02.000]** Router setup begins", rawTranscriptMarkdown, StringComparison.Ordinal);
        Assert.Contains("WireGuard VPN performance testing", transcriptMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsyncNormalizesLocalSrtSidecarWithoutMergingDifferentNearbyLines()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("sidecar-srt.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        await File.WriteAllTextAsync(temp.GetPath("sidecar-srt.srt"), """
            1
            00:00:01,000 --> 00:00:03,000
            Speaker A: welcome to the demo

            2
            00:00:04,000 --> 00:00:06,000
            Speaker B: thanks for having me

            3
            00:00:07,000 --> 00:00:09,000
            Speaker A: welcome to the demo
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "local-srt") with { IncludeTranscript = true }, progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        var normalizationReport = await store.ReadJsonAsync<TranscriptNormalizationReport>(result.Run, "transcript/normalization.json", CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.NotNull(normalizationReport);
        Assert.Equal(3, evidence.Transcript.Count);
        Assert.Equal(0, normalizationReport.MergeCount);
        Assert.Contains(evidence.Transcript, segment => segment.Text.Contains("Speaker A", StringComparison.Ordinal));
        Assert.Contains(evidence.Transcript, segment => segment.Text.Contains("Speaker B", StringComparison.Ordinal));
    }

    internal static AnalysisPipeline CreatePipeline(ArtifactStore store)
    {
        var dependencies = new DependencyResolver(new ReplayConfig());
        var processRunner = new ProcessRunner();
        return new AnalysisPipeline(store, new YtDlpClient(dependencies, processRunner), new FfmpegClient(dependencies, processRunner));
    }

    internal static AnalyzeRequest CreateRequest(string source, string runId)
    {
        return new AnalyzeRequest(
            Source: source,
            Instruction: "Test instruction",
            IncludeTranscript: false,
            FrameCount: 0,
            RunId: runId,
            ExtractAudio: false,
            UseSpeechToText: false,
            UseOcr: false,
            UseVision: false,
            UseSummary: false);
    }

    internal static ArtifactManifest CreateManifest(string source, string runId, DateTimeOffset createdAt)
    {
        return new ArtifactManifest(
            SchemaVersion: "0.1",
            Source: source,
            Instruction: "Test instruction",
            CreatedAt: createdAt,
            RunId: runId,
            Title: "Existing",
            WebpageUrl: null,
            Duration: null,
            AudioPath: null,
            TranscriptPath: null,
            OcrPath: null,
            VisionPath: null,
            SummaryPath: null,
            EvidencePath: "evidence.json",
            Frames: [],
            Warnings: []);
    }

    private sealed class FakeYtDlpClient : IYtDlpClient
    {
        public YtDlpInfo Info { get; init; } = new();

        public string? BestMediaUrl { get; init; }

        public string? DownloadedMediaPath { get; init; }

        public int DownloadMediaCallCount { get; private set; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Info);
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult<TranscriptArtifact?>(null);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(BestMediaUrl);
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            DownloadMediaCallCount++;
            return Task.FromResult(DownloadedMediaPath);
        }
    }

    private sealed class FakeFfmpegClient : IFfmpegClient
    {
        private readonly List<string> frameSources = [];
        private readonly List<string> frameStrategies = [];

        public string? FailingFrameSource { get; init; }

        public IReadOnlyList<string> FrameSources => frameSources;

        public IReadOnlyList<string> FrameStrategies => frameStrategies;

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, CancellationToken cancellationToken)
        {
            frameSources.Add(mediaSource);
            frameStrategies.Add(strategy);
            if (mediaSource == FailingFrameSource)
            {
                throw new ReplayException("simulated remote ffmpeg failure");
            }

            return Task.FromResult<IReadOnlyList<FrameArtifact>>([new FrameArtifact("frames/frame-001.jpg", 4.5, "00:04")]);
        }

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult("audio/audio.wav");
        }

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
        {
            return Task.FromResult("clips/clip.mp4");
        }

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
        {
            return Task.FromResult<double?>(9);
        }
    }

    private sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly List<LlmRequest> requests = [];

        public FakeLlmProvider(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IReadOnlyList<LlmRequest> Requests => requests;

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.FromResult("fake response");
        }
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages => messages;

        public void Report(string value)
        {
            messages.Add(value);
        }
    }
}
