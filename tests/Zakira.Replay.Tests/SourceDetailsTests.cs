using System.Text.Json;
using NJsonSchema;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// metadata.json carried the description, creator chapters, uploader and counts but the
/// manifest never did, and comments were never fetched at all - two of four Shorts in the user
/// report had the recipe in the description and one in a pinned comment. The probe can now ask
/// yt-dlp for comments (--comments top|all) and the manifest gets a sourceDetails block.
/// </summary>
public sealed class SourceDetailsTests
{
    private const string DumpWithEverything = """
        {
          "id": "abc", "title": "Rye bread", "webpage_url": "https://www.youtube.com/watch?v=abc", "duration": 90,
          "description": "Recipe:\n500 g rye flour\n400 g water",
          "uploader": "ChainBaker", "channel": "ChainBaker", "upload_date": "20260427",
          "view_count": 192269, "like_count": 3829, "comment_count": 455,
          "tags": ["bread", "rye"], "categories": ["Howto & Style"],
          "chapters": [
            {"start_time": 0.0, "title": "Intro", "end_time": 12.5},
            {"start_time": 12.5, "title": "Mixing", "end_time": 60.0}
          ],
          "comments": [
            {"id": "c1", "text": "Great video", "author": "@fan", "author_is_uploader": false, "is_pinned": false, "like_count": 900, "timestamp": 1777600000, "parent": "root"},
            {"id": "c2", "text": "Full recipe: 500 g rye, 400 g water, 10 g salt. Ferment 2-2.5 h.", "author": "@ChainBaker", "author_is_uploader": true, "is_pinned": true, "like_count": 120, "timestamp": 1777500000, "parent": "root"},
            {"id": "c3", "text": "reply text", "author": "@x", "author_is_uploader": false, "is_pinned": false, "like_count": 5000, "timestamp": 1777500001, "parent": "c1"},
            {"id": "c4", "text": "Thanks for watching!", "author": "@ChainBaker", "author_is_uploader": true, "is_pinned": false, "like_count": 10, "timestamp": 1777500002, "parent": "root"}
          ]
        }
        """;

    [Fact]
    public void BuilderLiftsDescriptionChaptersCountsAndOrdersComments()
    {
        var info = JsonSerializer.Deserialize<YtDlpInfo>(DumpWithEverything, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var details = SourceDetailsBuilder.FromInfo(info, "comments/comments.json");

        Assert.NotNull(details);
        Assert.StartsWith("Recipe:", details.Description);
        Assert.Equal("ChainBaker", details.Uploader);
        Assert.Equal("2026-04-27", details.UploadDate);
        Assert.Equal(192269, details.ViewCount);
        Assert.Equal(["bread", "rye"], details.Tags);
        Assert.Equal(2, details.Chapters!.Count);
        Assert.Equal(("Mixing", 12.5, "00:12"), (details.Chapters[1].Title, details.Chapters[1].StartSeconds, details.Chapters[1].StartLabel));
        // Pinned uploader comment first, then the uploader's other comment, then by likes; the reply is excluded.
        Assert.Equal(["c2", "c4", "c1"], details.Comments!.Select(c => c.Id ?? string.Empty).ToArray());
        Assert.True(details.Comments![0].IsPinned);
        Assert.True(details.Comments[0].AuthorIsUploader);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1777500000).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), details.Comments[0].PublishedAt);
        Assert.Equal("comments/comments.json", details.CommentsPath);
    }

    [Fact]
    public void BuilderReturnsNullForAnEmptyInfoAndCapsCommentText()
    {
        Assert.Null(SourceDetailsBuilder.FromInfo(new YtDlpInfo(), null));

        var longText = new string('x', 5000);
        var comments = SourceDetailsBuilder.SelectManifestComments([new YtDlpComment { Id = "1", Text = longText, Parent = "root" }]);
        Assert.Equal(SourceDetailsBuilder.MaxManifestCommentChars + 1, comments!.Single().Text.Length);

        var many = Enumerable.Range(0, 50).Select(i => new YtDlpComment { Id = i.ToString(), Text = $"c{i}", LikeCount = i, Parent = "root" }).ToList();
        Assert.Equal(SourceDetailsBuilder.MaxManifestComments, SourceDetailsBuilder.SelectManifestComments(many)!.Count);
        Assert.Equal("49", SourceDetailsBuilder.SelectManifestComments(many)![0].Id);
    }

    [Theory]
    [InlineData(null, "off", null)]
    [InlineData("off", "off", null)]
    [InlineData("top", "top", "youtube:max_comments=60,20,0,0;comment_sort=top")]
    [InlineData("ALL", "all", "youtube:max_comments=1000,200,800,20;comment_sort=top")]
    public void CommentsModesNormalizeAndMapToExtractorArguments(string? input, string expectedMode, string? expectedArgs)
    {
        Assert.Equal(expectedMode, CommentsModes.Normalize(input));
        Assert.Equal(expectedArgs, CommentsModes.ToExtractorArguments(input));
        Assert.Throws<ReplayException>(() => CommentsModes.Normalize("pinned"));
    }

    [Fact]
    public async Task ProbePassesWriteCommentsOnlyWhenRequested()
    {
        using var temp = new TestTempDirectory();
        var fake = temp.GetPath("fake-yt-dlp.exe");
        await File.WriteAllBytesAsync(fake, [], CancellationToken.None);
        var calls = new List<string[]>();
        var runner = new CapturingRunner(args =>
        {
            calls.Add(args);
            return new ProcessResult("yt-dlp", args, 0, DumpWithEverything, "");
        });
        var client = new YtDlpClient(new DependencyResolver(new ReplayConfig { Dependencies = new DependencyPathConfig { YtDlpPath = fake } }), runner);
        var request = new AnalyzeRequest(Source: "https://www.youtube.com/watch?v=abc", VisionInstruction: "", IncludeTranscript: false, FrameCount: 0, RunId: "c");

        var plain = await client.GetInfoAsync(request, CancellationToken.None);
        var withComments = await client.GetInfoAsync(request with { Comments = "top" }, CancellationToken.None);

        Assert.DoesNotContain("--write-comments", calls[0]);
        Assert.Contains("--write-comments", calls[1]);
        var index = Array.IndexOf(calls[1], "--extractor-args");
        Assert.Equal("youtube:max_comments=60,20,0,0;comment_sort=top", calls[1][index + 1]);
        Assert.Equal(request.Source, calls[1][^1]);
        Assert.Equal(4, withComments.Comments!.Count);
        Assert.Equal(2, plain.Chapters!.Count);
    }

    [Fact]
    public async Task PipelineWritesSourceDetailsIntoTheManifestAndCommentsToTheirOwnFile()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var pipeline = new AnalysisPipeline(store, new DumpYtDlp(DumpWithEverything), new NoFramesFfmpeg(), _ => null);

        var result = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest("https://www.youtube.com/watch?v=abc", "source-details") with { Comments = "top" }, progress: null, CancellationToken.None);

        var details = result.Manifest.SourceDetails;
        Assert.NotNull(details);
        Assert.StartsWith("Recipe:", details.Description);
        Assert.Equal(2, details.Chapters!.Count);
        Assert.Equal(3, details.Comments!.Count);
        Assert.Equal("comments/comments.json", details.CommentsPath);
        Assert.True(File.Exists(result.Run.GetPath("comments/comments.json")));
        var fullComments = JsonSerializer.Deserialize<List<YtDlpComment>>(await File.ReadAllTextAsync(result.Run.GetPath("comments/comments.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(4, fullComments!.Count);

        var schema = await JsonSchema.FromFileAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", "manifest.schema.json")));
        Assert.Empty(schema.Validate(await File.ReadAllTextAsync(result.Run.GetPath("manifest.json"))).Select(e => e.ToString()));

        // Local files carry no source details.
        var local = temp.GetPath("local.mp4");
        await File.WriteAllTextAsync(local, "x", CancellationToken.None);
        var localResult = await pipeline.AnalyzeAsync(AnalysisPipelineTests.CreateRequest(local, "source-details-local"), progress: null, CancellationToken.None);
        Assert.Null(localResult.Manifest.SourceDetails);
    }

    private sealed class CapturingRunner : ProcessRunner
    {
        private readonly Func<string[], ProcessResult> handler;
        public CapturingRunner(Func<string[], ProcessResult> handler) { this.handler = handler; }
        public override Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(handler(arguments.ToArray()));
    }

    private sealed class DumpYtDlp : IYtDlpClient
    {
        private readonly string dump;
        public DumpYtDlp(string dump) { this.dump = dump; }
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            var info = JsonSerializer.Deserialize<YtDlpInfo>(dump, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            info.RawJson = dump;
            return Task.FromResult(info);
        }
        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => Task.FromResult<TranscriptArtifact?>(null);
        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoFramesFfmpeg : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("no audio");
        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => throw new ReplayException("no clip");
        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult<double?>(90);
        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
