using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// One yt-dlp extractor pass per analyze. The metadata probe's <c>--dump-single-json</c> output
/// already carries every signed format URL and caption URL, so it is persisted as
/// <c>yt-dlp.info.json</c> and handed back with <c>--load-info-json</c> to the subtitle,
/// media-URL and download steps (previously three separate extractions, each a fresh chance for
/// a YouTube bot check or HTTP 429). Verified against yt-dlp 2026.08.19: <c>-g</c> answers from
/// the dump without network access, and the subtitle step falls back to a live extraction by
/// itself when the dump's caption URLs have expired.
/// </summary>
public sealed class InfoJsonReuseTests
{
    private static readonly string FutureExpiry = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds().ToString();
    private static readonly string PastExpiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();

    private static string Dump(string expiry, bool withCaptions = true, string? captionExpiry = null) => JsonSerializer.Serialize(new
    {
        id = "abc",
        title = "Dump",
        duration = 120,
        webpage_url = "https://www.youtube.com/watch?v=abc",
        formats = new[] { new { format_id = "18", url = $"https://rr1.googlevideo.com/videoplayback?expire={expiry}&id=x" } },
        subtitles = new Dictionary<string, object>(),
        automatic_captions = withCaptions
            ? new Dictionary<string, object> { ["en"] = new[] { new { ext = "vtt", url = $"https://www.youtube.com/api/timedtext?v=abc&expire={captionExpiry ?? expiry}" } } }
            : new Dictionary<string, object>()
    });

    [Theory]
    [InlineData("https://rr1.googlevideo.com/videoplayback?expire=1778427728&ei=x", 1778427728L)]
    [InlineData("https://www.youtube.com/api/timedtext?v=abc&expire=1778427728&signature=y", 1778427728L)]
    [InlineData("https://cdn.example/seg/expires/1778427728/file.m3u8", 1778427728L)]
    [InlineData("https://cdn.example/video.mp4", null)]
    [InlineData("https://cdn.example/video.mp4?expire=12", null)]
    [InlineData("", null)]
    public void ExpiryIsParsedFromSignedUrls(string url, long? expectedUnixSeconds)
    {
        var parsed = YtDlpInfoJson.TryParseExpiry(url);
        Assert.Equal(expectedUnixSeconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(expectedUnixSeconds.Value), parsed);
    }

    [Fact]
    public void FreshnessFollowsTheEarliestSignedUrl()
    {
        using var temp = new TestTempDirectory();
        var fresh = temp.GetPath("fresh.json");
        var stale = temp.GetPath("stale.json");
        var noExpiry = temp.GetPath("plain.json");
        File.WriteAllText(fresh, Dump(FutureExpiry));
        File.WriteAllText(stale, Dump(PastExpiry));
        File.WriteAllText(noExpiry, """{"id":"x","formats":[{"url":"https://cdn.example/a.mp4"}]}""");

        Assert.True(YtDlpInfoJson.IsFresh(fresh));
        Assert.False(YtDlpInfoJson.IsFresh(stale));
        Assert.True(YtDlpInfoJson.IsFresh(noExpiry));
        Assert.False(YtDlpInfoJson.IsFresh(temp.GetPath("missing.json")));

        // A dump whose formats are fine but whose caption URLs expired is stale as a whole.
        var mixed = temp.GetPath("mixed.json");
        File.WriteAllText(mixed, Dump(FutureExpiry, captionExpiry: PastExpiry));
        Assert.False(YtDlpInfoJson.IsFresh(mixed));
    }

    [Fact]
    public void AdvertisesCaptionsReflectsTheDump()
    {
        var with = JsonSerializer.Deserialize<YtDlpInfo>(Dump(FutureExpiry, withCaptions: true), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var without = JsonSerializer.Deserialize<YtDlpInfo>(Dump(FutureExpiry, withCaptions: false), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(with.AdvertisesCaptions);
        Assert.False(without.AdvertisesCaptions);
        Assert.False(new YtDlpInfo().AdvertisesCaptions);
    }

    [Fact]
    public async Task GetInfoKeepsTheVerbatimDumpOutOfMetadataJson()
    {
        using var temp = new TestTempDirectory();
        var raw = Dump(FutureExpiry);
        var (client, calls) = CreateClient(temp, (args, _) => new ProcessResult("yt-dlp", args, 0, raw + "\n", ""));

        var info = await client.GetInfoAsync(Request(), CancellationToken.None);

        Assert.Equal(raw, info.RawJson);
        var serialized = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("rawJson", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("advertisesCaptions", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"webpage_url\"", serialized);
        Assert.Single(calls);
    }

    [Fact]
    public async Task MediaUrlUsesTheFreshDumpAndFallsBackToTheUrlWhenRejected()
    {
        using var temp = new TestTempDirectory();
        var dump = temp.GetPath(YtDlpInfoJson.FileName);
        File.WriteAllText(dump, Dump(FutureExpiry));
        var (client, calls) = CreateClient(temp, (args, _) => args.Contains("--load-info-json")
            ? new ProcessResult("yt-dlp", args, 0, "https://cdn.example/from-dump\n", "")
            : new ProcessResult("yt-dlp", args, 0, "https://cdn.example/from-url\n", ""));

        var url = await client.GetBestMediaUrlAsync(Request(), dump, CancellationToken.None);

        Assert.Equal("https://cdn.example/from-dump", url);
        var call = Assert.Single(calls);
        Assert.Equal(["--load-info-json", dump], call[^2..]);
        Assert.DoesNotContain("--no-playlist", call);
        Assert.DoesNotContain(Request().Source, call);

        // Rejected dump (non-zero exit, e.g. version skew) -> one live extraction.
        calls.Clear();
        var (rejecting, rejectingCalls) = CreateClient(temp, (args, _) => args.Contains("--load-info-json")
            ? new ProcessResult("yt-dlp", args, 1, "", "ERROR: bad info json")
            : new ProcessResult("yt-dlp", args, 0, "https://cdn.example/from-url\n", ""));
        Assert.Equal("https://cdn.example/from-url", await rejecting.GetBestMediaUrlAsync(Request(), dump, CancellationToken.None));
        Assert.Equal(2, rejectingCalls.Count);
        Assert.Equal(Request().Source, rejectingCalls[1][^1]);
        Assert.Contains("--no-playlist", rejectingCalls[1]);
    }

    [Fact]
    public async Task StaleOrMissingDumpGoesStraightToTheUrl()
    {
        using var temp = new TestTempDirectory();
        var stale = temp.GetPath(YtDlpInfoJson.FileName);
        File.WriteAllText(stale, Dump(PastExpiry));
        var (client, calls) = CreateClient(temp, (args, _) => new ProcessResult("yt-dlp", args, 0, "https://cdn.example/from-url\n", ""));

        await client.GetBestMediaUrlAsync(Request(), stale, CancellationToken.None);
        await client.GetBestMediaUrlAsync(Request(), temp.GetPath("does-not-exist.json"), CancellationToken.None);
        await client.GetBestMediaUrlAsync(Request(), null, CancellationToken.None);

        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.DoesNotContain("--load-info-json", call));
        Assert.All(calls, call => Assert.Equal(Request().Source, call[^1]));
    }

    [Fact]
    public async Task SubtitleAndDownloadStepsReuseTheDump()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://www.youtube.com/watch?v=abc", "dump-reuse");
        var dump = run.GetPath(YtDlpInfoJson.FileName);
        File.WriteAllText(dump, Dump(FutureExpiry));
        var (client, calls) = CreateClient(temp, (args, _) =>
        {
            if (args.Contains("--write-subs"))
            {
                File.WriteAllText(run.GetPath(Path.Combine("captions", "subtitle.en.vtt")), "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nhello\n");
            }
            if (args.Contains("--merge-output-format"))
            {
                File.WriteAllText(run.GetPath(Path.Combine("media", "source.mp4")), "x");
            }
            return new ProcessResult("yt-dlp", args, 0, "", "");
        });

        var transcript = await client.DownloadBestSubtitleAsync(Request(), run, ["en"], dump, CancellationToken.None);
        var media = await client.DownloadMediaForProcessingAsync(Request(), run, dump, CancellationToken.None);

        Assert.NotNull(transcript);
        Assert.EndsWith("source.mp4", media);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(["--load-info-json", dump], call[^2..]));
        Assert.Contains("--write-auto-subs", calls[0]);
        Assert.Contains("--merge-output-format", calls[1]);
    }

    [Fact]
    public async Task PipelinePersistsTheDumpAndSkipsTheSubtitlePassWhenNoneAreAdvertised()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var ytDlp = new RecordingYtDlp(Dump(FutureExpiry, withCaptions: false));
        var pipeline = new AnalysisPipeline(store, ytDlp, new FrameFfmpeg(), _ => null);
        var request = AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "dump-pipeline") with { IncludeTranscript = true, FrameCount = 1 };

        var result = await pipeline.AnalyzeAsync(request, progress: null, CancellationToken.None);

        var dumpPath = result.Run.GetPath(YtDlpInfoJson.FileName);
        Assert.True(File.Exists(dumpPath));
        Assert.Equal(Dump(FutureExpiry, withCaptions: false), (await File.ReadAllTextAsync(dumpPath)).Trim());
        Assert.Equal(0, ytDlp.SubtitleCalls);
        Assert.Equal(dumpPath, ytDlp.LastMediaUrlInfoJsonPath);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.TranscriptNotFoundNoStt);

        // With captions advertised the subtitle pass runs and receives the dump path.
        var withCaptions = new RecordingYtDlp(Dump(FutureExpiry, withCaptions: true));
        var pipeline2 = new AnalysisPipeline(store, withCaptions, new FrameFfmpeg(), _ => null);
        var result2 = await pipeline2.AnalyzeAsync(request with { RunId = "dump-pipeline-2" }, progress: null, CancellationToken.None);
        Assert.Equal(1, withCaptions.SubtitleCalls);
        Assert.Equal(result2.Run.GetPath(YtDlpInfoJson.FileName), withCaptions.LastSubtitleInfoJsonPath);
    }

    private static AnalyzeRequest Request() => new(Source: "https://www.youtube.com/watch?v=abc", VisionInstruction: "", IncludeTranscript: true, FrameCount: 1, RunId: "dump");

    private static (YtDlpClient Client, List<string[]> Calls) CreateClient(TestTempDirectory temp, Func<string[], TimeSpan?, ProcessResult> handler)
    {
        var fake = temp.GetPath("fake-yt-dlp.exe");
        if (!File.Exists(fake))
        {
            File.WriteAllBytes(fake, []);
        }

        var calls = new List<string[]>();
        var runner = new RecordingRunner((args, timeout) =>
        {
            calls.Add(args);
            return handler(args, timeout);
        });
        var client = new YtDlpClient(new DependencyResolver(new ReplayConfig { Dependencies = new DependencyPathConfig { YtDlpPath = fake } }), runner);
        return (client, calls);
    }

    private sealed class RecordingRunner : ProcessRunner
    {
        private readonly Func<string[], TimeSpan?, ProcessResult> handler;
        public RecordingRunner(Func<string[], TimeSpan?, ProcessResult> handler) { this.handler = handler; }
        public override Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(handler(arguments.ToArray(), timeout));
    }

    private sealed class RecordingYtDlp : IYtDlpClient
    {
        private readonly string raw;
        public RecordingYtDlp(string raw) { this.raw = raw; }
        public int SubtitleCalls { get; private set; }
        public string? LastSubtitleInfoJsonPath { get; private set; }
        public string? LastMediaUrlInfoJsonPath { get; private set; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            var info = JsonSerializer.Deserialize<YtDlpInfo>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            info.RawJson = raw;
            return Task.FromResult(info);
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
            => DownloadBestSubtitleAsync(request, run, subtitleLanguages, null, cancellationToken);

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, string? infoJsonPath, CancellationToken cancellationToken)
        {
            SubtitleCalls++;
            LastSubtitleInfoJsonPath = infoJsonPath;
            return Task.FromResult<TranscriptArtifact?>(null);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => GetBestMediaUrlAsync(request, null, cancellationToken);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, string? infoJsonPath, CancellationToken cancellationToken)
        {
            LastMediaUrlInfoJsonPath = infoJsonPath;
            return Task.FromResult<string?>("https://cdn.example/v.mp4");
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FrameFfmpeg : IFfmpegClient
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
