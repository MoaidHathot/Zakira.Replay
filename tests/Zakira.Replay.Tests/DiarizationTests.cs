using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class DiarizationTests
{
    [Theory]
    [InlineData("sherpa-onnx", DiarizationProviders.SherpaOnnx)]
    [InlineData("sherpa", DiarizationProviders.SherpaOnnx)]
    [InlineData("sherpaonnx", DiarizationProviders.SherpaOnnx)]
    [InlineData("local", DiarizationProviders.SherpaOnnx)]
    [InlineData("pyannote", DiarizationProviders.SherpaOnnx)]
    [InlineData(" SHERPA-ONNX ", DiarizationProviders.SherpaOnnx)]
    [InlineData("", DiarizationProviders.SherpaOnnx)]
    [InlineData(null, DiarizationProviders.SherpaOnnx)]
    public void DiarizationProviderFactoryNormalizesKnownAliases(string? input, string expected)
    {
        Assert.Equal(expected, DiarizationProviderFactory.Normalize(input));
    }

    [Fact]
    public void DiarizationProviderFactoryReturnsUnknownVerbatimSoCallerCanWarn()
    {
        Assert.Equal("pyannote-cloud", DiarizationProviderFactory.Normalize("pyannote-cloud"));
        Assert.Equal("magic-diar", DiarizationProviderFactory.Normalize("magic-diar"));
    }

    [Fact]
    public void GetConfiguredProviderPrefersEnvironmentVariableOverConfig()
    {
        const string envVar = "ZAKIRA_REPLAY_DIARIZATION_PROVIDER";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "sherpa");
            var config = new ReplayConfig
            {
                Diarization = new DiarizationConfig { Provider = "future-provider" }
            };

            Assert.Equal(DiarizationProviders.SherpaOnnx, DiarizationProviderFactory.GetConfiguredProvider(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void DiarizationSegmentFormatsSpeakerIdWithTwoDigitPad()
    {
        Assert.Equal("SPEAKER_00", DiarizationSegment.FormatSpeakerId(0));
        Assert.Equal("SPEAKER_07", DiarizationSegment.FormatSpeakerId(7));
        Assert.Equal("SPEAKER_42", DiarizationSegment.FormatSpeakerId(42));
        Assert.Equal("SPEAKER_100", DiarizationSegment.FormatSpeakerId(100));
    }

    [Fact]
    public void MergeAssignsSpeakerToTranscriptSegmentByMaximumOverlap()
    {
        // Transcript: 0-5 (silent), 5-10 (Alice talks).
        // Diarization: 4.5-9 = SPEAKER_00, 9-15 = SPEAKER_01.
        // The 5-10 transcript segment overlaps SPEAKER_00 by 4s and SPEAKER_01 by 1s -> SPEAKER_00 wins.
        var transcript = new[]
        {
            new TranscriptSegment(StartSeconds: 0, EndSeconds: 5, Timestamp: "00:00 - 00:05", Text: "Welcome."),
            new TranscriptSegment(StartSeconds: 5, EndSeconds: 10, Timestamp: "00:05 - 00:10", Text: "Hello everyone.")
        };
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9), "SPEAKER_00"),
            new DiarizationSegment(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15), "SPEAKER_01")
        };

        var merged = DiarizationMerger.Merge(transcript, diarization);

        Assert.Equal(2, merged.Count);
        Assert.Equal("SPEAKER_00", merged[0].SpeakerId);
        Assert.Equal("SPEAKER_00", merged[0].SpeakerDisplayName);
        Assert.Equal("SPEAKER_00", merged[1].SpeakerId);
    }

    [Fact]
    public void MergeLeavesSpeakerNullWhenNoOverlap()
    {
        var transcript = new[]
        {
            new TranscriptSegment(StartSeconds: 100, EndSeconds: 110, Timestamp: "01:40 - 01:50", Text: "Late speech.")
        };
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(50), "SPEAKER_00")
        };

        var merged = DiarizationMerger.Merge(transcript, diarization);

        Assert.Null(merged[0].SpeakerId);
        Assert.Null(merged[0].SpeakerDisplayName);
    }

    [Fact]
    public void MergePreservesExistingSpeakerAttributionByDefault()
    {
        // VTT <v> tags or SRT prefixes may have already filled SpeakerId. Diarization must not
        // overwrite that — the explicit caption-side label wins.
        var transcript = new[]
        {
            new TranscriptSegment(
                StartSeconds: 0,
                EndSeconds: 5,
                Timestamp: "00:00 - 00:05",
                Text: "Hi.",
                SpeakerId: "alice",
                SpeakerDisplayName: "Alice")
        };
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "SPEAKER_00")
        };

        var merged = DiarizationMerger.Merge(transcript, diarization);

        Assert.Equal("alice", merged[0].SpeakerId);
        Assert.Equal("Alice", merged[0].SpeakerDisplayName);
    }

    [Fact]
    public void MergeOverwritesExistingSpeakerWhenPreserveDisabled()
    {
        var transcript = new[]
        {
            new TranscriptSegment(
                StartSeconds: 0,
                EndSeconds: 5,
                Timestamp: "00:00 - 00:05",
                Text: "Hi.",
                SpeakerId: "alice",
                SpeakerDisplayName: "Alice")
        };
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "SPEAKER_00")
        };

        var merged = DiarizationMerger.Merge(transcript, diarization, new MergeOptions(PreserveExistingSpeakers: false));

        Assert.Equal("SPEAKER_00", merged[0].SpeakerId);
    }

    [Fact]
    public void MergeLeavesUntimedSegmentsUntouched()
    {
        var transcript = new[]
        {
            new TranscriptSegment(StartSeconds: null, EndSeconds: null, Timestamp: null, Text: "Stray prose")
        };
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "SPEAKER_00")
        };

        var merged = DiarizationMerger.Merge(transcript, diarization);

        Assert.Null(merged[0].SpeakerId);
        Assert.Equal("Stray prose", merged[0].Text);
    }

    [Fact]
    public void MergeReturnsTranscriptUnchangedWhenDiarizationIsEmpty()
    {
        var transcript = new[]
        {
            new TranscriptSegment(StartSeconds: 0, EndSeconds: 5, Timestamp: "00:00 - 00:05", Text: "Alone."),
            new TranscriptSegment(StartSeconds: 5, EndSeconds: 10, Timestamp: "00:05 - 00:10", Text: "Still alone.")
        };

        var merged = DiarizationMerger.Merge(transcript, []);

        Assert.Same(transcript, merged);
    }

    [Fact]
    public void MergeReturnsTranscriptUnchangedWhenTranscriptIsEmpty()
    {
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "SPEAKER_00")
        };

        var merged = DiarizationMerger.Merge([], diarization);

        Assert.Empty(merged);
    }

    [Fact]
    public void AnnotateMarkdownInjectsSpeakerPrefixesAheadOfTimestampedLines()
    {
        // The chunked-STT step produces lines like `**[mm:ss - mm:ss]** text`. The merger must
        // re-emit them with a `[SPEAKER_NN]` prefix between the timestamp and the text so the
        // existing TranscriptParser (which already understands the bracketed prefix from
        // VTT <v> tags / SRT prefixes) picks the attribution back up on the next normalise pass.
        var markdown = "**[00:00 - 00:05]** Hello\n**[00:05 - 00:10]** World";
        var diarization = new[]
        {
            new DiarizationSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "SPEAKER_00"),
            new DiarizationSegment(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "SPEAKER_01")
        };

        var annotated = DiarizationMerger.AnnotateMarkdown(markdown, diarization);

        Assert.Contains("**[00:00 - 00:05]** [SPEAKER_00] Hello", annotated, StringComparison.Ordinal);
        Assert.Contains("**[00:05 - 00:10]** [SPEAKER_01] World", annotated, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnotateMarkdownReturnsInputUnchangedWhenDiarizationEmpty()
    {
        var markdown = "**[00:00 - 00:05]** Hello";

        Assert.Equal(markdown, DiarizationMerger.AnnotateMarkdown(markdown, []));
    }

    [Fact]
    public void OptionsResolveHonoursEnvironmentVariablesOverConfig()
    {
        const string segEnv = "ZAKIRA_REPLAY_DIARIZATION_SEGMENTATION_MODEL_PATH";
        const string embEnv = "ZAKIRA_REPLAY_DIARIZATION_EMBEDDING_MODEL_PATH";
        const string numEnv = "ZAKIRA_REPLAY_DIARIZATION_NUM_SPEAKERS";
        const string thresholdEnv = "ZAKIRA_REPLAY_DIARIZATION_THRESHOLD";
        var prevSeg = Environment.GetEnvironmentVariable(segEnv);
        var prevEmb = Environment.GetEnvironmentVariable(embEnv);
        var prevNum = Environment.GetEnvironmentVariable(numEnv);
        var prevThreshold = Environment.GetEnvironmentVariable(thresholdEnv);
        try
        {
            Environment.SetEnvironmentVariable(segEnv, "/env/seg.onnx");
            Environment.SetEnvironmentVariable(embEnv, "/env/emb.onnx");
            Environment.SetEnvironmentVariable(numEnv, "5");
            Environment.SetEnvironmentVariable(thresholdEnv, "0.42");
            var config = new ReplayConfig
            {
                Diarization = new DiarizationConfig
                {
                    SegmentationModelPath = "/config/seg.onnx",
                    EmbeddingModelPath = "/config/emb.onnx",
                    NumSpeakers = 9,
                    Threshold = 0.99f
                }
            };

            var options = DiarizationOptions.Resolve(config);

            Assert.Equal("/env/seg.onnx", options.SegmentationModelPath);
            Assert.Equal("/env/emb.onnx", options.EmbeddingModelPath);
            Assert.Equal(5, options.NumSpeakers);
            Assert.Equal(0.42f, options.Threshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable(segEnv, prevSeg);
            Environment.SetEnvironmentVariable(embEnv, prevEmb);
            Environment.SetEnvironmentVariable(numEnv, prevNum);
            Environment.SetEnvironmentVariable(thresholdEnv, prevThreshold);
        }
    }

    [Fact]
    public void OptionsMissingFilesReportsBothModels()
    {
        using var temp = new TestTempDirectory();
        var options = new DiarizationOptions(
            SegmentationModelPath: temp.GetPath("missing-seg.onnx"),
            EmbeddingModelPath: temp.GetPath("missing-emb.onnx"));

        var missing = options.MissingFiles();

        Assert.Equal(2, missing.Count);
    }

    [Fact]
    public async Task OptionsMissingFilesReportsEmptyWhenAllPresent()
    {
        using var temp = new TestTempDirectory();
        var seg = temp.GetPath("seg.onnx");
        var emb = temp.GetPath("emb.onnx");
        await File.WriteAllBytesAsync(seg, [0], CancellationToken.None);
        await File.WriteAllBytesAsync(emb, [0], CancellationToken.None);
        var options = new DiarizationOptions(seg, emb);

        Assert.Empty(options.MissingFiles());
    }

    [Fact]
    public async Task ConfigStoreRoundTripsDiarizationKeys()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        await store.SetAsync("diarization.provider", "sherpa-onnx", CancellationToken.None);
        await store.SetAsync("diarization.numSpeakers", "4", CancellationToken.None);
        await store.SetAsync("diarization.threshold", "0.35", CancellationToken.None);
        await store.SetAsync("diarization.threads", "2", CancellationToken.None);
        await store.SetAsync("diarization.autoDownload", "false", CancellationToken.None);

        Assert.Equal("sherpa-onnx", await store.GetAsync("diarization.provider", CancellationToken.None));
        Assert.Equal("4", await store.GetAsync("diarization.numSpeakers", CancellationToken.None));
        Assert.Equal("2", await store.GetAsync("diarization.threads", CancellationToken.None));
        Assert.Equal("False", await store.GetAsync("diarization.autoDownload", CancellationToken.None));
    }

    [Fact]
    public void AnalysisCacheKeyIncludesDiarizationKnobs()
    {
        var baseline = new AnalyzeRequest(
            Source: "https://example.com/talk",
            VisionInstruction: "",
            IncludeTranscript: true,
            FrameCount: 10,
            RunId: null);

        var withDiarize = baseline with { UseDiarization = true };
        var withSpeakers = baseline with { UseDiarization = true, NumSpeakers = 3 };
        var withThreshold = baseline with { UseDiarization = true, DiarizationThreshold = 0.4f };

        // Each variation must produce a distinct cache key, otherwise a non-diarised run would
        // reuse a cached run with no speaker labels.
        var keys = new[]
        {
            AnalysisCache.CreateKey(baseline),
            AnalysisCache.CreateKey(withDiarize),
            AnalysisCache.CreateKey(withSpeakers),
            AnalysisCache.CreateKey(withThreshold)
        };

        Assert.Equal(4, keys.Distinct().Count());
    }
}
