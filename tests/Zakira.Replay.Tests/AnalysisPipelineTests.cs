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
            VisionInstruction: "Test remote fallback",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "remote-fallback"), progress: null, CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Equal(1, ytDlp.DownloadMediaCallCount);
        Assert.Equal(new[] { ytDlp.BestMediaUrl, downloadedMedia }, ffmpeg.FrameSources);
        Assert.Single(result.Manifest.Frames);
        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.FramesRemoteFallback);
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
            VisionInstruction: "Test scene strategy",
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
            VisionInstruction: "Test every-frame strategy",
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
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, new FakeYtDlpClient(), ffmpeg, provider => provider == "openai" ? llm : null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: "Test OCR provider",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "llm-provider",
            UseOcr: true,
            Model: "gpt-4o-mini",
            LlmProvider: "openai"), progress: null, CancellationToken.None);

        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        Assert.NotNull(evidence);
        Assert.Single(evidence.Ocr);
        Assert.Equal("fake response", evidence.Ocr[0].Text);
        Assert.Equal("frame-001", evidence.Ocr[0].FrameId);
        Assert.Equal("frame-001", evidence.Frames[0].Id);
        Assert.Equal("gpt-4o-mini", llm.Requests.Single().Model);
    }

    [Fact]
    public async Task AnalyzeAsyncWritesAvailableSubtitleLanguagesToMetadata()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Multi-language Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 30,
                Language = "fr",
                Subtitles = new Dictionary<string, JsonElement>
                {
                    ["fr"] = JsonDocument.Parse("[]").RootElement.Clone(),
                    ["en"] = JsonDocument.Parse("[]").RootElement.Clone()
                },
                AutomaticCaptions = new Dictionary<string, JsonElement>
                {
                    ["fr"] = JsonDocument.Parse("[]").RootElement.Clone(),
                    ["es"] = JsonDocument.Parse("[]").RootElement.Clone()
                }
            }
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, new FakeFfmpegClient());

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "Surface available subtitle languages",
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "available-langs"), progress: null, CancellationToken.None);

        var metadataJson = await File.ReadAllTextAsync(result.Run.GetPath("metadata.json"), CancellationToken.None);
        using var metadata = JsonDocument.Parse(metadataJson);
        var available = metadata.RootElement.GetProperty("availableSubtitleLanguages");
        Assert.Equal(JsonValueKind.Object, available.ValueKind);

        var languages = available.EnumerateObject().Select(prop => prop.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("fr", languages);
        Assert.Contains("en", languages);
        Assert.Contains("es", languages);

        Assert.True(available.GetProperty("fr").GetProperty("hasManual").GetBoolean());
        Assert.True(available.GetProperty("fr").GetProperty("hasAuto").GetBoolean());
        Assert.True(available.GetProperty("en").GetProperty("hasManual").GetBoolean());
        Assert.False(available.GetProperty("en").GetProperty("hasAuto").GetBoolean());
        Assert.False(available.GetProperty("es").GetProperty("hasManual").GetBoolean());
        Assert.True(available.GetProperty("es").GetProperty("hasAuto").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeAsyncResolvesAutoCaptionLanguagesFromMetadata()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Localized Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 30,
                Language = "fr",
                Subtitles = new Dictionary<string, JsonElement>
                {
                    ["fr"] = JsonDocument.Parse("[]").RootElement.Clone()
                },
                AutomaticCaptions = new Dictionary<string, JsonElement>
                {
                    ["es"] = JsonDocument.Parse("[]").RootElement.Clone()
                }
            }
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, new FakeFfmpegClient());

        await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "Resolve languages",
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "resolve-auto",
            CaptionLanguages: ["auto"]), progress: null, CancellationToken.None);

        var resolved = ytDlp.ResolvedSubtitleLanguageCalls.Single();
        Assert.Contains("fr", resolved);
        // Auto-caption-only languages (e.g., "es" via AutomaticCaptions) are auto-translations,
        // not source-language facts, so they must NOT be expanded into the resolved list.
        Assert.DoesNotContain("es", resolved);
        Assert.Contains("en", resolved);
        Assert.DoesNotContain("auto", resolved);
        Assert.Equal("fr", resolved[0]);
    }

    [Fact]
    public async Task AnalyzeAsyncPassesExplicitCaptionLanguagesVerbatim()
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
                DurationSeconds = 30
            }
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, new FakeFfmpegClient());

        await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "Resolve languages explicit",
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "resolve-explicit",
            CaptionLanguages: ["de", "fr"]), progress: null, CancellationToken.None);

        Assert.Equal(["de", "fr"], ytDlp.ResolvedSubtitleLanguageCalls.Single());
    }

    [Fact]
    public void ResolveSubtitleLanguagesExpandsAutoToMetadataPlusDefaults()
    {
        var info = new YtDlpInfo
        {
            Language = "ja",
            Subtitles = new Dictionary<string, JsonElement>
            {
                ["ja"] = JsonDocument.Parse("[]").RootElement.Clone()
            },
            AutomaticCaptions = new Dictionary<string, JsonElement>
            {
                ["zh"] = JsonDocument.Parse("[]").RootElement.Clone()
            }
        };

        var resolved = AnalysisPipeline.ResolveSubtitleLanguages(["auto"], info);

        Assert.Equal("ja", resolved[0]);
        // "zh" appears only in AutomaticCaptions (auto-translation), so it must NOT be resolved.
        Assert.DoesNotContain("zh", resolved);
        Assert.Contains("en", resolved);
        Assert.DoesNotContain("auto", resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSubtitleLanguagesPassesExplicitLanguagesThroughBeforeAutoExpansion()
    {
        var info = new YtDlpInfo
        {
            Language = "fr",
            Subtitles = new Dictionary<string, JsonElement>
            {
                ["fr"] = JsonDocument.Parse("[]").RootElement.Clone()
            }
        };

        var resolved = AnalysisPipeline.ResolveSubtitleLanguages(["pt", "auto", "pt"], info);

        Assert.Equal("pt", resolved[0]);
        Assert.Contains("fr", resolved);
        Assert.Single(resolved, language => language.Equals("pt", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(evidence.Transcript, segment => segment.SpeakerDisplayName == "Speaker A" && segment.Text == "welcome to the demo");
        Assert.Contains(evidence.Transcript, segment => segment.SpeakerDisplayName == "Speaker B" && segment.Text == "thanks for having me");
        Assert.Equal(2, evidence.Speakers.Count);
        Assert.Contains(evidence.Speakers, speaker => speaker.Id == "speaker-a" && speaker.SegmentCount == 2);
        Assert.Contains(evidence.Speakers, speaker => speaker.Id == "speaker-b" && speaker.SegmentCount == 1);
    }

    [Fact]
    public async Task AnalyzeAsyncExtractsSpeakersFromVttVoiceTags()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("voice.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        await File.WriteAllTextAsync(temp.GetPath("voice.vtt"), """
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            <v Alice>welcome to the demo

            00:00:04.000 --> 00:00:06.000
            <v Bob>thanks Alice

            00:00:07.000 --> 00:00:09.000
            <v Alice>let's get started
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "vtt-voice") with { IncludeTranscript = true }, progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        var transcriptMarkdown = await File.ReadAllTextAsync(result.Run.GetPath("transcript.md"), CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal(3, evidence.Transcript.Count);
        Assert.All(evidence.Transcript, segment => Assert.False(string.IsNullOrEmpty(segment.SpeakerId), $"Expected SpeakerId on segment '{segment.Text}'."));
        Assert.Contains(evidence.Transcript, segment => segment.SpeakerDisplayName == "Alice" && segment.Text == "welcome to the demo");
        Assert.Contains(evidence.Transcript, segment => segment.SpeakerDisplayName == "Bob" && segment.Text == "thanks Alice");
        Assert.Equal(2, evidence.Speakers.Count);
        Assert.Contains(evidence.Speakers, speaker => speaker.Id == "alice" && speaker.SegmentCount == 2);
        Assert.Contains(evidence.Speakers, speaker => speaker.Id == "bob" && speaker.SegmentCount == 1);
        Assert.Contains("[Alice]", transcriptMarkdown, StringComparison.Ordinal);
        Assert.Contains("[Bob]", transcriptMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsyncDoesNotMergeAcrossSpeakerChangesEvenInsideDuplicateWindow()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("conversation.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        await File.WriteAllTextAsync(temp.GetPath("conversation.vtt"), """
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            <v Alice>shipping starts Monday

            00:00:01.500 --> 00:00:03.500
            <v Bob>shipping starts Monday
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "speaker-boundary") with { IncludeTranscript = true }, progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        var normalizationReport = await store.ReadJsonAsync<TranscriptNormalizationReport>(result.Run, "transcript/normalization.json", CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.NotNull(normalizationReport);
        Assert.Equal(2, evidence.Transcript.Count);
        Assert.Equal(0, normalizationReport.MergeCount);
        Assert.Equal(["alice", "bob"], evidence.Transcript.Select(segment => segment.SpeakerId ?? string.Empty).ToArray());
    }

    [Fact]
    public void TranscriptNormalizerStillMergesSameSpeakerDuplicates()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 2, "00:00 - 00:02", "Router setup begins", SpeakerId: "alice", SpeakerDisplayName: "Alice"),
            new TranscriptSegment(1, 3, "00:01 - 00:03", "Router setup begins with VPN", SpeakerId: "alice", SpeakerDisplayName: "Alice")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal("Router setup begins with VPN", segment.Text);
        Assert.Equal("alice", segment.SpeakerId);
    }

    [Fact]
    public void TranscriptNormalizerAssignsStableSegmentIds()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 2, "00:00", "first"),
            new TranscriptSegment(5, 7, "00:05", "second"),
            new TranscriptSegment(10, 12, "00:10", "third")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(["segment-0001", "segment-0002", "segment-0003"], normalized.Select(segment => segment.Id ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsOneSlidePerFrameAndOcrFrameResultsReferenceSlides()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var llm = new FakeLlmProvider("openai");
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, new FakeYtDlpClient(), ffmpeg, provider => provider == "openai" ? llm : null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: "Test slide grouping",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "slides-integration",
            UseOcr: true,
            UseVision: true,
            Model: "gpt-4o-mini",
            LlmProvider: "openai"), progress: null, CancellationToken.None);

        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        Assert.NotNull(evidence);
        var slide = Assert.Single(evidence.Slides);
        Assert.Equal("frame-001", slide.PrimaryFrameId);
        Assert.Equal(["frame-001"], slide.FrameIds);
        Assert.Equal("slide-001", slide.Id);
        Assert.NotNull(evidence.Frames[0].PerceptualHash);

        var ocrResult = Assert.Single(evidence.Ocr);
        Assert.Equal("slide-001", ocrResult.SlideId);
        Assert.Equal("frame-001", ocrResult.FrameId);

        var visionResult = Assert.Single(evidence.Vision);
        Assert.Equal("slide-001", visionResult.SlideId);

        Assert.True(File.Exists(result.Run.GetPath("slides/slides.json")));
        Assert.True(File.Exists(result.Run.GetPath("ocr/frame-001.json")));
        Assert.True(File.Exists(result.Run.GetPath("vision/frame-001.json")));
    }

    [Fact]
    public async Task AnalyzeAsyncRecordsParseFallbackWarningWhenLlmReturnsProse()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var llm = new FakeLlmProvider("openai")
        {
            Response = "This is a free-form description, not JSON."
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, new FakeYtDlpClient(), ffmpeg, provider => provider == "openai" ? llm : null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: "Test parse fallback",
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "parse-fallback",
            UseOcr: true,
            Model: "gpt-4o-mini",
            LlmProvider: "openai"), progress: null, CancellationToken.None);

        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        Assert.NotNull(evidence);
        Assert.Single(evidence.Ocr);
        Assert.Equal("This is a free-form description, not JSON.", evidence.Ocr[0].Text);
        Assert.Contains(evidence.Warnings, warning => warning.Code == ReplayWarningCodes.OcrParseFallback);
    }

    [Fact]
    public async Task AnalyzeAsyncWritesEmptyInstructionsToEvidenceWhenNotProvided()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AnalyzeAsync(CreateRequest(sourcePath, "empty-instructions"), progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal(string.Empty, evidence.VisionInstruction);
        Assert.Equal(string.Empty, evidence.OcrInstruction);
        Assert.Equal(string.Empty, result.Manifest.VisionInstruction);
        Assert.Equal(string.Empty, result.Manifest.OcrInstruction);
    }

    [Fact]
    public async Task AnalyzeAsyncPersistsBothInstructionsVerbatimWhenProvided()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = CreatePipeline(store);

        var request = CreateRequest(sourcePath, "filled-instructions") with
        {
            VisionInstruction = "Focus on slide titles and chart axes.",
            OcrInstruction = "Preserve code indentation."
        };
        var result = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);
        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal("Focus on slide titles and chart axes.", evidence.VisionInstruction);
        Assert.Equal("Preserve code indentation.", evidence.OcrInstruction);
        Assert.Equal("Focus on slide titles and chart axes.", result.Manifest.VisionInstruction);
        Assert.Equal("Preserve code indentation.", result.Manifest.OcrInstruction);
    }

    [Fact]
    public void ResolveEffectiveFrameCountReturnsFrameCountWhenFramesPerMinuteIsNull()
    {
        var request = new AnalyzeRequest(
            Source: "x",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 30,
            RunId: null);

        Assert.Equal(30, AnalysisPipeline.ResolveEffectiveFrameCount(request, durationSeconds: 2400));
    }

    [Fact]
    public void ResolveEffectiveFrameCountScalesByDurationWhenFramesPerMinuteIsSet()
    {
        var request = new AnalyzeRequest(
            Source: "x",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 30,
            RunId: null,
            FramesPerMinute: 2);

        // 40 minutes * 2 frames/min = 80 frames; floor (30) loses
        Assert.Equal(80, AnalysisPipeline.ResolveEffectiveFrameCount(request, durationSeconds: 2400));
    }

    [Fact]
    public void ResolveEffectiveFrameCountFloorWinsWhenScaledIsLower()
    {
        var request = new AnalyzeRequest(
            Source: "x",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 30,
            RunId: null,
            FramesPerMinute: 1);

        // 5 minutes * 1 = 5; floor (30) wins
        Assert.Equal(30, AnalysisPipeline.ResolveEffectiveFrameCount(request, durationSeconds: 300));
    }

    [Fact]
    public void ResolveEffectiveFrameCountIgnoresFramesPerMinuteWhenDurationUnknown()
    {
        var request = new AnalyzeRequest(
            Source: "x",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 7,
            RunId: null,
            FramesPerMinute: 5);

        Assert.Equal(7, AnalysisPipeline.ResolveEffectiveFrameCount(request, durationSeconds: null));
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsUndersampledWarningWhenIntervalRatioTooLow()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Long Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 60 * 40 // 40 minutes
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "undersampled"), progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.FramesLikelyUndersampled);
    }

    [Fact]
    public async Task AnalyzeAsyncDoesNotEmitUndersampledWarningWhenFramesPerMinuteSet()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new FakeYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "remote",
                Title = "Long Video",
                WebpageUrl = "https://example.test/video",
                DurationSeconds = 60 * 40
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "rate-aware",
            FramesPerMinute: 2), progress: null, CancellationToken.None);

        Assert.DoesNotContain(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.FramesLikelyUndersampled);
    }

    [Fact]
    public async Task AnalyzeAsyncSceneStrategyPassesSafetyCapToFfmpeg()
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
                DurationSeconds = 600
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 5,
            RunId: "scene-cap",
            FrameStrategy: FrameSelectionStrategies.Scene,
            SceneSafetyCap: 7), progress: null, CancellationToken.None);

        // FakeFfmpegClient ignores sceneSafetyCap but the call is recorded.
        Assert.Equal(7, ffmpeg.LastSceneSafetyCap);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsSceneCapWarningWhenFramesReachSafetyCap()
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
                DurationSeconds = 600
            },
            BestMediaUrl = "https://cdn.example.test/direct.mp4"
        };
        var ffmpeg = new FakeFfmpegClient { SceneFrameCount = 3 };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "scene-cap-hit",
            FrameStrategy: FrameSelectionStrategies.Scene,
            SceneSafetyCap: 3), progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.FramesSceneCapReached);
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
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 0,
            RunId: runId,
            ExtractAudio: false,
            UseSpeechToText: false,
            UseOcr: false,
            UseVision: false);
    }

    internal static ArtifactManifest CreateManifest(string source, string runId, DateTimeOffset createdAt)
    {
        return new ArtifactManifest(
            SchemaVersion: "0.7",
            Source: source,
            VisionInstruction: "Test instruction",

            OcrInstruction: "",
            CreatedAt: createdAt,
            RunId: runId,
            Title: "Existing",
            WebpageUrl: null,
            Duration: null,
            AudioPath: null,
            TranscriptPath: null,
            OcrPath: null,
            VisionPath: null,
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

        public List<IReadOnlyList<string>> ResolvedSubtitleLanguageCalls { get; } = [];

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Info);
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        {
            ResolvedSubtitleLanguageCalls.Add(subtitleLanguages);
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

        /// <summary>
        /// When set, the scene strategy returns exactly this many fake frames so tests can
        /// verify the scene-cap warning fires.
        /// </summary>
        public int? SceneFrameCount { get; init; }

        public IReadOnlyList<string> FrameSources => frameSources;

        public IReadOnlyList<string> FrameStrategies => frameStrategies;

        public int LastSceneSafetyCap { get; private set; }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
        {
            frameSources.Add(mediaSource);
            frameStrategies.Add(strategy);
            LastSceneSafetyCap = sceneSafetyCap;
            if (mediaSource == FailingFrameSource)
            {
                throw new ReplayException("simulated remote ffmpeg failure");
            }

            if (strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase) && SceneFrameCount is { } sceneCount)
            {
                var scenes = new List<FrameArtifact>(sceneCount);
                for (var i = 0; i < sceneCount; i++)
                {
                    scenes.Add(new FrameArtifact($"scene-{i + 1:0000}", $"frames/scene-{i + 1:0000}.jpg", i, Timestamp.Format(i)));
                }

                return Task.FromResult<IReadOnlyList<FrameArtifact>>(scenes);
            }

            return Task.FromResult<IReadOnlyList<FrameArtifact>>([new FrameArtifact("frame-001", "frames/frame-001.jpg", 4.5, "00:04")]);
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

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        }

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "fake chunk");
            return Task.CompletedTask;
        }

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
        {
            // Stable per-path hash so slide grouping is deterministic in tests.
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var c in imagePath)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }

                return Task.FromResult<string?>(hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            }
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

        public string Response { get; init; } = "fake response";

        public IReadOnlyList<LlmRequest> Requests => requests;

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.FromResult(Response);
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
