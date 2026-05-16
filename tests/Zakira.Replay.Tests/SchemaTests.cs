using System.Text.Json;
using NJsonSchema;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SchemaTests
{
    [Theory]
    [InlineData("request.schema.json")]
    [InlineData("manifest.schema.json")]
    [InlineData("evidence.schema.json")]
    [InlineData("transcript-normalization.schema.json")]
    [InlineData("chapters.schema.json")]
    [InlineData("clip.schema.json")]
    [InlineData("frame-capture.schema.json")]
    [InlineData("search-index.schema.json")]
    [InlineData("batch.schema.json")]
    [InlineData("batch-result.schema.json")]
    [InlineData("queue.schema.json")]
    [InlineData("queue-run-result.schema.json")]
    [InlineData("audio-chunks.schema.json")]
    [InlineData("slides.schema.json")]
    [InlineData("ocr.schema.json")]
    [InlineData("vision.schema.json")]
    [InlineData("evidence-aligned.schema.json")]
    public async Task SchemaFilesAreValidJson(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", fileName);

        await using var stream = File.OpenRead(System.IO.Path.GetFullPath(path));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        if (document.RootElement.TryGetProperty("type", out var typeElement))
        {
            Assert.True(typeElement.GetString() is "object" or "array");
        }
        else
        {
            // Schemas using top-level oneOf (e.g. evidence-aligned) intentionally omit `type`.
            Assert.True(document.RootElement.TryGetProperty("oneOf", out _));
        }
    }

    [Fact]
    public async Task GeneratedCoreArtifactsValidateAgainstPublishedSchemas()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = AnalysisPipelineTests.CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest(sourcePath, "schema-validation"), progress: null, CancellationToken.None);

        await AssertValidAsync("request.schema.json", result.Run.GetPath("request.json"));
        await AssertValidAsync("manifest.schema.json", result.Run.GetPath("manifest.json"));
        await AssertValidAsync("evidence.schema.json", result.Run.GetPath("evidence.json"));
    }

    [Fact]
    public async Task GeneratedTranscriptNormalizationArtifactValidatesAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        await File.WriteAllTextAsync(temp.GetPath("source.vtt"), """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            Router setup begins

            00:00:01.500 --> 00:00:04.000
            Router setup begins with VPN
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = AnalysisPipelineTests.CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest(sourcePath, "normalization-schema") with { IncludeTranscript = true }, progress: null, CancellationToken.None);

        await AssertValidAsync("transcript-normalization.schema.json", result.Run.GetPath("transcript/normalization.json"));
    }

    [Fact]
    public async Task GeneratedChaptersArtifactValidatesAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "chapters-schema");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "source.mp4",
            VisionInstruction: "test",

            OcrInstruction: "",
            RunId: "chapters-schema",
            Title: "Chapters Schema Fixture",
            WebpageUrl: null,
            DurationSeconds: 90,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(0, 20, "00:00 - 00:20", "Router setup covers VPN tunnels and network firewall rules."),
                new TranscriptSegment(25, 45, "00:25 - 00:45", "Router throughput is measured during secure WireGuard traffic."),
                new TranscriptSegment(50, 70, "00:50 - 01:10", "Travel accessories are compared for airports and daily carry.")
            ],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);

        await new ChapterBuilder().BuildAsync(runDirectory, new ChapterBuildOptions(MinDurationSeconds: 20, MaxDurationSeconds: 45), CancellationToken.None);

        await AssertValidAsync("chapters.schema.json", Path.Combine(runDirectory, "chapters", "chapters.json"));
    }

    [Fact]
    public async Task GeneratedSearchIndexArtifactValidatesAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);

        await new SearchIndexService().BuildAsync(runDirectory, CancellationToken.None);

        await AssertValidAsync("search-index.schema.json", Path.Combine(runDirectory, "search", "index.json"));
    }

    [Fact]
    public async Task GeneratedClipArtifactValidatesAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var service = new ClipExtractionService(store, new FakeClipYtDlpClient(), new FakeClipFfmpegClient());

        var result = await service.ExtractAsync(new ClipExtractionRequest(
            Source: sourcePath,
            Start: TimeSpan.FromSeconds(1),
            End: TimeSpan.FromSeconds(3),
            RunId: "clip-schema",
            OutputName: "sample"), progress: null, CancellationToken.None);

        await AssertValidAsync("clip.schema.json", result.Run.GetPath("clip.json"));
    }

    [Fact]
    public async Task BatchManifestAndGeneratedResultValidateAgainstPublishedSchemas()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var manifestPath = temp.GetPath("batch.json");
        var manifest = new BatchManifest
        {
            BatchId = "batch-schema",
            Frames = 0,
            IncludeTranscript = false,
            Items = [new BatchItem { Source = sourcePath, RunId = "batch-item-schema" }]
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        await AssertValidAsync("batch.schema.json", manifestPath);

        var store = new ArtifactStore(temp.GetPath("runs"));
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            var runner = new BatchRunner(() => AnalysisPipelineTests.CreatePipeline(store));
            var result = await runner.RunAsync(manifestPath, progress: null, CancellationToken.None);

            Assert.Single(result.Items);
            Assert.True(result.Items[0].Succeeded);
            await AssertValidAsync("batch-result.schema.json", Path.Combine(result.BatchDirectory, "batch-result.json"));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [Fact]
    public async Task QueueStateAndRunResultValidateAgainstPublishedSchemas()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var queue = new AnalysisQueue(() => AnalysisPipelineTests.CreatePipeline(store), temp.GetPath("queues"));
        var request = AnalysisPipelineTests.CreateRequest(sourcePath, "queue-schema") with { UseCache = false };

        var enqueue = await queue.EnqueueAsync("schema", request, "queue-schema-job", retries: 0, CancellationToken.None);
        var result = await queue.RunAsync("schema", new AnalysisQueueRunOptions(Concurrency: 1, Retries: 0), progress: null, CancellationToken.None);

        Assert.Equal(enqueue.QueueId, result.QueueId);
        await AssertValidAsync("queue.schema.json", Path.Combine(enqueue.QueueDirectory, "queue.json"));
        await AssertValidAsync("queue-run-result.schema.json", Path.Combine(enqueue.QueueDirectory, "last-run-result.json"));
    }

    [Fact]
    public async Task GeneratedFrameCaptureManifestValidatesAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeClipYtDlpClient();
        var ffmpeg = new FrameCaptureSchemaFfmpegClient();
        var service = new FrameCaptureService(store, ytDlp, ffmpeg);

        var captureResult = await service.CaptureAsync(
            new FrameCaptureRequest(
                Source: sourcePath,
                Timestamps: [TimeSpan.FromSeconds(1.5)],
                RunId: "frame-capture-schema",
                MaxLongEdgePixels: 640,
                JpegQuality: 80),
            progress: null,
            CancellationToken.None);

        await AssertValidAsync("frame-capture.schema.json", captureResult.Run.GetPath("frame-capture.json"));
    }

    private static async Task AssertValidAsync(string schemaFileName, string jsonPath)
    {
        var schema = await JsonSchema.FromFileAsync(GetSchemaPath(schemaFileName));
        var errors = schema.Validate(await File.ReadAllTextAsync(jsonPath, CancellationToken.None));

        Assert.Empty(errors.Select(error => error.ToString()));
    }

    private static string GetSchemaPath(string fileName)
    {
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", fileName));
    }

    private static async Task<string> CreateSearchRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "search-schema");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "source.mp4",
            VisionInstruction: "test",

            OcrInstruction: "",
            RunId: "search-schema",
            Title: "Search Schema Fixture",
            WebpageUrl: null,
            DurationSeconds: 30,
            AudioPath: null,
            Transcript: [new TranscriptSegment(1, 5, "00:01 - 00:05", "WireGuard VPN router setup.")],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return runDirectory;
    }

    private sealed class FakeClipYtDlpClient : IYtDlpClient
    {
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult(new YtDlpInfo());

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => Task.FromResult<TranscriptArtifact?>(null);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FakeClipFfmpegClient : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        }

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult("audio/audio.wav");
        }

        public async Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
        {
            var path = "clips/sample.mp4";
            Directory.CreateDirectory(Path.GetDirectoryName(run.GetPath(path))!);
            await File.WriteAllTextAsync(run.GetPath(path), "fake clip", cancellationToken);
            return path;
        }

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
        {
            return Task.FromResult<double?>(3);
        }

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        }

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "fake clip chunk");
            return Task.CompletedTask;
        }

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>("0000000000000000");
        }
    }

    private sealed class FrameCaptureSchemaFfmpegClient : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            var frames = new List<FrameArtifact>(timestamps.Count);
            for (var i = 0; i < timestamps.Count; i++)
            {
                var seconds = timestamps[i].TotalSeconds;
                var label = Timestamp.Format(seconds);
                var fileSafeLabel = label.Replace(':', '-').Replace('.', '-');
                var relativePath = $"frames/frame-{i + 1:000}-{fileSafeLabel}.jpg";
                var absolute = run.GetPath(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.WriteAllText(absolute, "fake jpeg");
                frames.Add(new FrameArtifact($"frame-{i + 1:000}", relativePath, seconds, label));
            }
            return Task.FromResult<IReadOnlyList<FrameArtifact>>(frames);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
            => Task.FromResult("audio/audio.wav");

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
            => Task.FromResult("clips/sample.mp4");

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
            => Task.FromResult<double?>(30.0);

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>("0000000000000000");
    }
}
