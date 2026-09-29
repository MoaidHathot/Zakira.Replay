using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Transcript quality regressions found by re-parsing the real YouTube auto-caption runs under
/// <c>runs/</c>:
/// <list type="bullet">
///   <item>ordinary prose with a colon (<c>yogurt has two ingredients: milk</c>) and bracketed
///   sound annotations (<c>[snorts]</c>) became <em>speakers</em>, polluting evidence.json and
///   blocking normalizer merges (speaker changes are hard boundaries);</item>
///   <item>the normalizer located the merge point with a case-sensitive ASCII regex while
///   tokenising case-insensitively, so every capitalised word in an overlap shifted the index
///   and words were dropped or truncated at merge boundaries (<c>I spent 3 weeks</c> became
///   <c>I 3 weeks</c>, <c>Today</c> became <c>oday</c>); non-Latin scripts never merged;</item>
///   <item>with merges working, a continuous monologue collapsed into one segment spanning the
///   whole video, so the normalizer now re-segments rolling captions at sentence ends.</item>
/// </list>
/// </summary>
public sealed class TranscriptQualityTests
{
    /// <summary>Verbatim excerpt (cues 20.15 s – 30.35 s) of runs/yogurt/captions/subtitle.en-orig.vtt plus one bracket annotation.</summary>
    private const string YouTubeAutoCaptionExcerpt = """
        WEBVTT
        Kind: captions
        Language: en

        00:00:20.150 --> 00:00:20.160 align:start position:0%
        are paying premium prices for what is
         

        00:00:20.160 --> 00:00:21.990 align:start position:0%
        are paying premium prices for what is
        essentially<00:00:20.760><c> flavored</c><00:00:21.160><c> pudding</c><00:00:21.760><c> with</c><00:00:21.920><c> a</c>

        00:00:21.990 --> 00:00:22.000 align:start position:0%
        essentially flavored pudding with a
         

        00:00:22.000 --> 00:00:24.350 align:start position:0%
        essentially flavored pudding with a
        probiotic<00:00:22.520><c> logo</c><00:00:22.840><c> printed</c><00:00:23.240><c> on</c><00:00:23.320><c> the</c><00:00:23.440><c> lid.</c><00:00:24.120><c> Real</c>

        00:00:24.350 --> 00:00:24.360 align:start position:0%
        probiotic logo printed on the lid. Real
         

        00:00:24.360 --> 00:00:26.350 align:start position:0%
        probiotic logo printed on the lid. Real
        yogurt<00:00:24.720><c> has</c><00:00:24.920><c> two</c><00:00:25.040><c> ingredients:</c><00:00:25.680><c> milk</c><00:00:26.200><c> and</c>

        00:00:26.350 --> 00:00:26.360 align:start position:0%
        yogurt has two ingredients: milk and
         

        00:00:26.360 --> 00:00:28.510 align:start position:0%
        yogurt has two ingredients: milk and
        live<00:00:26.640><c> bacterial</c><00:00:27.120><c> cultures.</c><00:00:28.200><c> That's</c><00:00:28.440><c> the</c>

        00:00:28.510 --> 00:00:28.520 align:start position:0%
        live bacterial cultures. That's the
         

        00:00:28.520 --> 00:00:30.350 align:start position:0%
        live bacterial cultures. That's the
        entire<00:00:28.840><c> list.</c><00:00:29.360><c> But</c><00:00:29.560><c> walk</c><00:00:29.760><c> down</c><00:00:29.960><c> the</c><00:00:30.040><c> dairy</c>

        00:00:30.350 --> 00:00:31.900 align:start position:0%
        entire list. But walk down the dairy
        [snorts]<00:00:30.800><c> aisle</c><00:00:31.200><c> and</c><00:00:31.500><c> flip</c>
        """;

    [Fact]
    public void AutoCaptionProseWithColonsAndSoundAnnotationsProducesNoSpeakers()
    {
        var raw = SubtitleConverter.ParseSegments(YouTubeAutoCaptionExcerpt);

        Assert.All(raw, segment => Assert.Null(segment.SpeakerDisplayName));
        Assert.Contains(raw, segment => segment.Text == "yogurt has two ingredients: milk and");
        Assert.Contains(raw, segment => segment.Text.StartsWith("entire list. But walk down the dairy [snorts] aisle", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizedAutoCaptionsLoseNoWordsAndDuplicateNothing()
    {
        var raw = SubtitleConverter.ParseSegments(YouTubeAutoCaptionExcerpt);
        var normalized = TranscriptNormalizer.Normalize(raw);
        var joined = string.Join(" ", normalized.Select(segment => segment.Text));

        const string expected = "are paying premium prices for what is essentially flavored pudding with a probiotic logo printed on the lid. "
            + "Real yogurt has two ingredients: milk and live bacterial cultures. That's the entire list. But walk down the dairy [snorts] aisle and flip";
        Assert.Equal(expected, joined);
        Assert.All(normalized, segment => Assert.Null(segment.SpeakerId));
        // Segments must be sentence-aligned once the soft budget is exceeded: the second one
        // starts with the carried "Real yogurt…" sentence, not mid-sentence.
        Assert.True(normalized.Count >= 2, "expected the 11-second excerpt to be split at a sentence end");
        Assert.EndsWith("printed on the lid.", normalized[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Real yogurt has two ingredients", normalized[1].Text, StringComparison.Ordinal);
        Assert.True(normalized[1].StartSeconds > normalized[0].StartSeconds);
    }

    [Theory]
    [InlineData("Speaker A", true)]
    [InlineData("Alice", true)]
    [InlineData("Ohad Schneider", true)]
    [InlineData("Lital Porat Tuvian", true)]
    [InlineData("SPEAKER_01", true)]
    [InlineData("@1", true)]
    [InlineData("Dr. Jones", true)]
    [InlineData("Ludwig van Beethoven", true)]
    [InlineData("yogurt has two ingredients", false)]
    [InlineData("requires two things", false)]
    [InlineData("differently. It affects two things", false)]
    [InlineData("heavier compounds", false)]
    [InlineData("snorts", false)]
    [InlineData("Music", false)]
    [InlineData("Applause", false)]
    [InlineData("Step 1", false)]
    [InlineData("Note", false)]
    [InlineData("Today we", false)]
    [InlineData("12", false)]
    [InlineData("", false)]
    [InlineData("One Two Three Four Five", false)]
    public void SpeakerLabelShapeRuleMatchesRealLabelsAndRejectsProse(string candidate, bool expected)
    {
        Assert.Equal(expected, SpeakerLabelHeuristics.LooksLikeSpeakerLabel(candidate));
    }

    [Fact]
    public void ColonLabelsRequireARecurringLabelInTheDocument()
    {
        Assert.True(SpeakerLabelHeuristics.AcceptColonLabels(["Speaker A", "Speaker B", "Speaker A"]));
        Assert.True(SpeakerLabelHeuristics.AcceptColonLabels(["Alice", "alice"]));
        Assert.False(SpeakerLabelHeuristics.AcceptColonLabels(["Note"]));
        Assert.False(SpeakerLabelHeuristics.AcceptColonLabels(["Alice", "Bob"]));
        Assert.False(SpeakerLabelHeuristics.AcceptColonLabels([]));
    }

    [Fact]
    public void SingleTitleCaseColonPrefixStaysProseWhileRecurringOnesBecomeSpeakers()
    {
        var single = SubtitleConverter.ParseSegments("""
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            Reminder: preheat the oven first

            00:00:04.000 --> 00:00:06.000
            then mix the dry ingredients
            """);
        Assert.All(single, segment => Assert.Null(segment.SpeakerDisplayName));
        Assert.Equal("Reminder: preheat the oven first", single[0].Text);

        var labelled = SubtitleConverter.ParseSegments("""
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            Alice: welcome to the demo

            00:00:04.000 --> 00:00:06.000
            Bob: thanks for having me

            00:00:07.000 --> 00:00:09.000
            Alice: let's get started
            """);
        Assert.Equal(["Alice", "Bob", "Alice"], labelled.Select(segment => segment.SpeakerDisplayName ?? string.Empty).ToArray());
        Assert.Equal("welcome to the demo", labelled[0].Text);
    }

    [Fact]
    public void MarkdownRoundTripKeepsBracketAnnotationsOutOfTheSpeakerRegistry()
    {
        var segments = TranscriptParser.ParseMarkdown(
        [
            "**[00:00:01.000 - 00:00:03.000]** [music] hello there",
            "**[00:00:04.000 - 00:00:06.000]** [Alice] hi",
            "**[00:00:07.000 - 00:00:09.000]** [SPEAKER_01] and welcome",
        ]);

        Assert.Null(segments[0].SpeakerDisplayName);
        Assert.Equal("[music] hello there", segments[0].Text);
        Assert.Equal("Alice", segments[1].SpeakerDisplayName);
        Assert.Equal("hi", segments[1].Text);
        Assert.Equal("SPEAKER_01", segments[2].SpeakerDisplayName);
        Assert.Equal("speaker-01", segments[2].SpeakerId);
    }

    [Fact]
    public void OverlapMergeKeepsCapitalisedWordsIntact()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 2, "00:00 - 00:02", "flavors that contain nothing natural at all. I"),
            new TranscriptSegment(2, 4, "00:02 - 00:04", "nothing natural at all. I spent 3 weeks auditing every major"),
            new TranscriptSegment(4, 6, "00:04 - 00:06", "spent 3 weeks auditing every major yogurt brand. Today, I'm exposing"),
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal("flavors that contain nothing natural at all. I spent 3 weeks auditing every major yogurt brand. Today, I'm exposing", segment.Text);
    }

    [Fact]
    public void NonLatinCaptionsAreNormalizedToo()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 2, "00:00 - 00:02", "שלום לכולם וברוכים הבאים"),
            new TranscriptSegment(2, 2.01, "00:02 - 00:02", "שלום לכולם וברוכים הבאים"),
            new TranscriptSegment(2.01, 4, "00:02 - 00:04", "וברוכים הבאים לפגישה השבועית"),
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal("שלום לכולם וברוכים הבאים לפגישה השבועית", segment.Text);
    }

    [Fact]
    public void RollingCaptionsAreResegmentedAtSentenceEndsWithoutLosingText()
    {
        // 14 sentences, each ~3 s, delivered as YouTube-style rolling cues (each cue repeats the
        // previous cue's tail). Without re-segmentation this collapses into a single segment.
        var sentences = Enumerable.Range(1, 14).Select(i => $"Sentence number {i} says something useful about topic {i}.").ToArray();
        var cues = new List<TranscriptSegment>();
        for (var i = 0; i < sentences.Length; i++)
        {
            var text = i == 0 ? sentences[i] : sentences[i - 1] + " " + sentences[i];
            cues.Add(new TranscriptSegment(i * 3.0, (i + 1) * 3.0, null, text));
        }

        var result = TranscriptNormalizer.NormalizeWithReport(cues);
        var normalized = result.Segments;

        Assert.Equal(string.Join(" ", sentences), string.Join(" ", normalized.Select(segment => segment.Text)));
        Assert.True(normalized.Count >= 4, $"expected several sentence-aligned segments, got {normalized.Count}");
        Assert.All(normalized, segment => Assert.EndsWith(".", segment.Text));
        Assert.All(normalized, segment => Assert.True((segment.EndSeconds - segment.StartSeconds) <= TranscriptNormalizer.HardMaxSegmentSeconds + 3));
        Assert.True(result.Report.SplitCount >= 3);
        Assert.Equal("0.2", result.Report.SchemaVersion);
        for (var i = 1; i < normalized.Count; i++)
        {
            Assert.True(normalized[i].StartSeconds > normalized[i - 1].StartSeconds, "segments must stay chronological");
        }
    }

    [Fact]
    public void CaptionsGrowingInPlaceNeverSplit()
    {
        // Live captions that extend the same cue keep the same start time; that is a growing
        // caption, not a continuation, and must keep merging regardless of length.
        var words = Enumerable.Range(1, 60).Select(i => $"word{i}.").ToArray();
        var cues = Enumerable.Range(1, 60)
            .Select(count => new TranscriptSegment(0, count * 0.5, null, string.Join(" ", words.Take(count))))
            .ToArray();

        var result = TranscriptNormalizer.NormalizeWithReport(cues);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(string.Join(" ", words), segment.Text);
        Assert.Equal(0, result.Report.SplitCount);
    }

    [Fact]
    public void UnpunctuatedRollingCaptionsSplitAtTheHardBudget()
    {
        var chunks = Enumerable.Range(1, 20).Select(i => $"chunk{i} alpha beta").ToArray();
        var cues = new List<TranscriptSegment>();
        for (var i = 0; i < chunks.Length; i++)
        {
            var text = i == 0 ? chunks[i] : chunks[i - 1] + " " + chunks[i];
            cues.Add(new TranscriptSegment(i * 2.0, (i + 1) * 2.0, null, text));
        }

        var normalized = TranscriptNormalizer.Normalize(cues);

        Assert.Equal(string.Join(" ", chunks), string.Join(" ", normalized.Select(segment => segment.Text)));
        Assert.True(normalized.Count >= 2);
        Assert.All(normalized, segment => Assert.True((segment.EndSeconds - segment.StartSeconds) <= TranscriptNormalizer.HardMaxSegmentSeconds + 2.0));
    }

    /// <summary>
    /// The normalized transcript must be on disk as soon as captions are parsed - before media
    /// resolution and frame extraction - so a run that dies later still leaves a usable
    /// transcript.md rather than the raw rolling captions.
    /// </summary>
    [Fact]
    public async Task TranscriptIsNormalizedOnDiskBeforeMediaResolution()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new CaptionsThenTimeoutYtDlp(YouTubeAutoCaptionExcerpt);
        var pipeline = new AnalysisPipeline(store, ytDlp, new NoopFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=YfUWbwXiiWM", "normalize-early") with { IncludeTranscript = true, FrameCount = 3 };

        var thrown = await Record.ExceptionAsync(() => pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None));

        Assert.True(ytDlp.MediaUrlRequested, "the fake should have reached the media-URL stage");
        var run = store.CreateRun(request.Source, request.RunId);
        var transcript = await File.ReadAllTextAsync(run.GetPath("transcript.md"));
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 3, $"expected the normalized transcript (2-3 segments), got {lines.Length} lines:\n{transcript}");
        Assert.Contains("Real yogurt has two ingredients: milk and live bacterial cultures.", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("[yogurt has two ingredients]", transcript, StringComparison.Ordinal);
        Assert.True(File.Exists(run.GetPath("transcript/raw.md")));
        Assert.True(File.Exists(run.GetPath("transcript/normalization.json")));
        _ = thrown; // The media stage may or may not surface as an exception depending on failure handling; the transcript contract holds either way.
    }

    private sealed class CaptionsThenTimeoutYtDlp : IYtDlpClient
    {
        private readonly string vtt;

        public CaptionsThenTimeoutYtDlp(string vtt)
        {
            this.vtt = vtt;
        }

        public bool MediaUrlRequested { get; private set; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new YtDlpInfo { Id = "YfUWbwXiiWM", Title = "Yogurt", WebpageUrl = request.Source, DurationSeconds = 788 });

        public async Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        {
            var captionPath = run.GetPath(Path.Combine("captions", "subtitle.en-orig.vtt"));
            await File.WriteAllTextAsync(captionPath, vtt, cancellationToken);
            var segments = SubtitleConverter.ParseSegments(vtt);
            var markdownPath = run.GetPath("transcript.md");
            await File.WriteAllTextAsync(markdownPath, SubtitleConverter.ToMarkdown(segments), cancellationToken);
            return new TranscriptArtifact(captionPath, markdownPath, "yt-dlp-subtitle", segments);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            MediaUrlRequested = true;
            throw new ProcessTimeoutException("yt-dlp", ["-g", request.Source], TimeSpan.FromMinutes(5), "", "ERROR: [youtube] Sign in to confirm you're not a bot");
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoopFfmpeg : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("no audio in this fake");
        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => throw new ReplayException("no clips in this fake");
        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
