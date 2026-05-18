using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class EvidenceAlignmentTests
{
    [Fact]
    public async Task AlignmentBuildsBySlideAndByChapterViewsFromExistingEvidence()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "alignment-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = CreateFixtureEvidence();
        await WriteJsonAsync(Path.Combine(runDirectory, "evidence.json"), evidence);

        var chapterDirectory = Path.Combine(runDirectory, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        var chapters = new ChapterDocument(
            SchemaVersion: "0.8",
            RunId: evidence.RunId,
            CreatedAt: DateTimeOffset.UtcNow,
            Method: "offline-lexical",
            Chapters:
            [
                new Chapter(0, 60, "00:00", "01:00", []),
                new Chapter(60, 130, "01:00", "02:10", [])
            ]);
        await WriteJsonAsync(Path.Combine(chapterDirectory, "chapters.json"), chapters);

        var result = await new EvidenceAlignmentService().BuildAsync(runDirectory, new EvidenceAlignmentOptions(), CancellationToken.None);

        Assert.True(result.ChaptersLoaded);
        Assert.Equal(2, result.ByChapter.Chapters.Count);
        Assert.Equal(3, result.BySlide.Slides.Count);

        var firstChapter = result.ByChapter.Chapters[0];
        Assert.Contains("slide-001", firstChapter.SlideIds);
        Assert.Contains("slide-002", firstChapter.SlideIds);
        Assert.Contains("segment-0001", firstChapter.TranscriptSegmentIds);
        Assert.Contains("segment-0002", firstChapter.TranscriptSegmentIds);
        Assert.Contains(firstChapter.Speakers, stat => stat.SpeakerId == "alice" && stat.SegmentCount >= 1);
        Assert.Contains("frame-001", firstChapter.OcrFrameIds);
        Assert.Contains("frame-001", firstChapter.VisionFrameIds);

        var secondChapter = result.ByChapter.Chapters[1];
        Assert.Contains("slide-003", secondChapter.SlideIds);
        Assert.DoesNotContain("slide-001", secondChapter.SlideIds);

        var firstSlide = result.BySlide.Slides.Single(slide => slide.SlideId == "slide-001");
        Assert.Equal("frame-001", firstSlide.PrimaryFrameId);
        Assert.Equal(5, firstSlide.WindowStartSeconds);
        Assert.True(firstSlide.WindowEndSeconds >= 30);
        Assert.Contains("segment-0001", firstSlide.TranscriptSegmentIds);
        Assert.Contains(firstSlide.Speakers, stat => stat.SpeakerId == "alice");
        Assert.Equal([0], firstSlide.ChapterIndices);
        Assert.NotNull(firstSlide.Ocr);
        Assert.Equal("frame-001", firstSlide.Ocr!.FrameId);
        Assert.NotNull(firstSlide.Vision);
        Assert.Equal("frame-001", firstSlide.Vision!.FrameId);

        var lastSlide = result.BySlide.Slides.Single(slide => slide.SlideId == "slide-003");
        Assert.Equal([1], lastSlide.ChapterIndices);
        Assert.Contains("segment-0004", lastSlide.TranscriptSegmentIds);
        Assert.Contains(lastSlide.Speakers, stat => stat.SpeakerId == "bob");
    }

    [Fact]
    public async Task AlignmentReferencesOnlyExistingEvidenceIds()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "alignment-dangling");
        Directory.CreateDirectory(runDirectory);
        var evidence = CreateFixtureEvidence();
        await WriteJsonAsync(Path.Combine(runDirectory, "evidence.json"), evidence);

        var result = await new EvidenceAlignmentService().BuildAsync(runDirectory, new EvidenceAlignmentOptions(), CancellationToken.None);

        var slideIds = evidence.Slides.Select(slide => slide.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var frameIds = evidence.Frames.Select(frame => frame.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var segmentIds = evidence.Transcript.Select(segment => segment.Id).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var speakerIds = evidence.Speakers.Select(speaker => speaker.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in result.ByChapter.Chapters)
        {
            Assert.All(entry.SlideIds, id => Assert.Contains(id, slideIds));
            Assert.All(entry.TranscriptSegmentIds, id => Assert.Contains(id, segmentIds));
            Assert.All(entry.Speakers, stat => Assert.Contains(stat.SpeakerId, speakerIds));
            Assert.All(entry.OcrFrameIds, id => Assert.Contains(id, frameIds));
            Assert.All(entry.VisionFrameIds, id => Assert.Contains(id, frameIds));
        }

        foreach (var slide in result.BySlide.Slides)
        {
            Assert.Contains(slide.SlideId, slideIds);
            Assert.Contains(slide.PrimaryFrameId, frameIds);
            Assert.All(slide.FrameIds, id => Assert.Contains(id, frameIds));
            Assert.All(slide.TranscriptSegmentIds, id => Assert.Contains(id, segmentIds));
            Assert.All(slide.Speakers, stat => Assert.Contains(stat.SpeakerId, speakerIds));
        }
    }

    [Fact]
    public async Task AlignmentEmitsEmptyByChapterWhenNoChaptersFile()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "alignment-no-chapters");
        Directory.CreateDirectory(runDirectory);
        var evidence = CreateFixtureEvidence();
        await WriteJsonAsync(Path.Combine(runDirectory, "evidence.json"), evidence);

        var result = await new EvidenceAlignmentService().BuildAsync(runDirectory, new EvidenceAlignmentOptions(), CancellationToken.None);

        Assert.False(result.ChaptersLoaded);
        Assert.Empty(result.ByChapter.Chapters);
        Assert.NotEmpty(result.BySlide.Slides);
        Assert.All(result.BySlide.Slides, slide => Assert.Empty(slide.ChapterIndices));
    }

    [Fact]
    public void SlideWindowsExtendToNextSlideStart()
    {
        var evidence = CreateFixtureEvidence();
        var windows = EvidenceAlignmentService.BuildSlideWindows(evidence);

        Assert.Equal(5, windows["slide-001"].Start);
        Assert.Equal(30, windows["slide-001"].End);
        Assert.Equal(30, windows["slide-002"].Start);
        Assert.Equal(70, windows["slide-002"].End);
        Assert.Equal(70, windows["slide-003"].Start);
        Assert.True(windows["slide-003"].End >= 130);
    }

    [Fact]
    public async Task GeneratedAlignmentArtifactsValidateAgainstPublishedSchema()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "alignment-schema");
        Directory.CreateDirectory(runDirectory);
        var evidence = CreateFixtureEvidence();
        await WriteJsonAsync(Path.Combine(runDirectory, "evidence.json"), evidence);
        var chapterDirectory = Path.Combine(runDirectory, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        var chapters = new ChapterDocument("0.5", evidence.RunId, DateTimeOffset.UtcNow, "offline-lexical",
            [new Chapter(0, 60, "00:00", "01:00", []), new Chapter(60, 130, "01:00", "02:10", [])]);
        await WriteJsonAsync(Path.Combine(chapterDirectory, "chapters.json"), chapters);

        var result = await new EvidenceAlignmentService().BuildAsync(runDirectory, new EvidenceAlignmentOptions(), CancellationToken.None);

        var schemaPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", "evidence-aligned.schema.json"));
        var schema = await NJsonSchema.JsonSchema.FromFileAsync(schemaPath);
        var byChapterErrors = schema.Validate(await File.ReadAllTextAsync(result.ByChapterPath, CancellationToken.None));
        var bySlideErrors = schema.Validate(await File.ReadAllTextAsync(result.BySlidePath, CancellationToken.None));

        Assert.Empty(byChapterErrors.Select(error => error.ToString()));
        Assert.Empty(bySlideErrors.Select(error => error.ToString()));
    }

    [Fact]
    public async Task CliAlignCommandWritesArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = temp.GetPath("runs", "alignment-cli");
        Directory.CreateDirectory(runDirectory);
        var evidence = CreateFixtureEvidence();
        await WriteJsonAsync(Path.Combine(runDirectory, "evidence.json"), evidence);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Cli.CliApp.RunAsync(["align", "build", runDirectory], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Aligned evidence", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-chapter.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-slide.json")));
    }

    private static EvidenceDocument CreateFixtureEvidence()
    {
        return new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "fixture.mp4",
            VisionInstruction: "test alignment",

            OcrInstruction: "",
            RunId: "alignment-fixture",
            Title: "Alignment Fixture",
            WebpageUrl: null,
            DurationSeconds: 130,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(StartSeconds: 0, EndSeconds: 12, Timestamp: "00:00 - 00:12", Text: "Alice opens the discussion.", Id: "segment-0001", SpeakerId: "alice", SpeakerDisplayName: "Alice"),
                new TranscriptSegment(StartSeconds: 30, EndSeconds: 50, Timestamp: "00:30 - 00:50", Text: "Alice continues with metrics.", Id: "segment-0002", SpeakerId: "alice", SpeakerDisplayName: "Alice"),
                new TranscriptSegment(StartSeconds: 70, EndSeconds: 90, Timestamp: "01:10 - 01:30", Text: "Bob takes over the demo.", Id: "segment-0003", SpeakerId: "bob", SpeakerDisplayName: "Bob"),
                new TranscriptSegment(StartSeconds: 95, EndSeconds: 120, Timestamp: "01:35 - 02:00", Text: "Bob wraps up.", Id: "segment-0004", SpeakerId: "bob", SpeakerDisplayName: "Bob")
            ],
            Frames:
            [
                new FrameArtifact("frame-001", "frames/frame-001.jpg", 5, "00:05", "0000000000000000"),
                new FrameArtifact("frame-002", "frames/frame-002.jpg", 35, "00:35", "0000000000000003"),
                new FrameArtifact("frame-003", "frames/frame-003.jpg", 75, "01:15", "ffffffffffffffff")
            ],
            Slides:
            [
                new SlideArtifact("slide-001", 5, 5, "00:05", "00:05", "frame-001", ["frame-001"], "0000000000000000"),
                new SlideArtifact("slide-002", 30, 35, "00:30", "00:35", "frame-002", ["frame-002"], "0000000000000003"),
                new SlideArtifact("slide-003", 70, 75, "01:10", "01:15", "frame-003", ["frame-003"], "ffffffffffffffff")
            ],
            Ocr:
            [
                new OcrFrameResult("frame-001", "frames/frame-001.jpg", 5, "00:05", "Welcome", SlideId: "slide-001", Structured: new OcrFrameStructured("Welcome", ["Welcome"], []))
            ],
            Vision:
            [
                new VisionFrameResult("frame-001", "frames/frame-001.jpg", 5, "00:05", "Title slide.", SlideId: "slide-001", Structured: new VisionFrameStructured("slide", "Welcome", [], [], [], [], "Title slide."))
            ],
            Speakers:
            [
                new SpeakerSummary("alice", "Alice", 2, 32, 0, 50),
                new SpeakerSummary("bob", "Bob", 2, 45, 70, 120)
            ],
            Warnings: []);
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }), CancellationToken.None);
    }
}
