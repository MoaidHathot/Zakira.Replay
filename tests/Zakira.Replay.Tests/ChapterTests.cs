using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class ChapterTests
{
    [Fact]
    public async Task ChapterBuilderWritesJsonAndMarkdownArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);

        var result = await new ChapterBuilder().BuildAsync(runDirectory, new ChapterBuildOptions(MinDurationSeconds: 20, MaxDurationSeconds: 45), CancellationToken.None);

        Assert.Equal("chapter-run", result.RunId);
        Assert.True(result.ChapterCount >= 2);
        Assert.True(File.Exists(Path.Combine(runDirectory, "chapters", "chapters.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "chapters", "chapters.md")));
        Assert.All(result.Document.Chapters, chapter => Assert.True(chapter.EndSeconds >= chapter.StartSeconds));
        Assert.Contains(result.Document.Chapters, chapter => chapter.Evidence.Any(item => item.Text.Contains("router", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Document.Chapters, chapter => chapter.Evidence.Any(item => item.Text.Contains("travel", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ChaptersCliBuildCommandWritesArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Cli.CliApp.RunAsync(["chapters", "build", runDirectory, "--min-duration", "20", "--max-duration", "45"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Built", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(runDirectory, "chapters", "chapters.json")));
    }

    private static async Task<string> CreateChapterRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "chapter-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.7",
            Source: "source.mp4",
            VisionInstruction: "test",

            OcrInstruction: "",
            RunId: "chapter-run",
            Title: "Chapter Fixture",
            WebpageUrl: null,
            DurationSeconds: 130,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(0, 12, "00:00", "The router comparison starts with WireGuard VPN setup and secure tunnel goals."),
                new TranscriptSegment(12, 28, "00:12", "Router throughput numbers show VPN performance and latency under load."),
                new TranscriptSegment(28, 46, "00:28", "The benchmark explains network routing, firewall rules, and client devices."),
                new TranscriptSegment(52, 65, "00:52", "The speaker switches to travel accessories, packing cubes, and bag organization."),
                new TranscriptSegment(65, 82, "01:05", "Travel bags are compared by size, durability, compartments, and airport convenience."),
                new TranscriptSegment(82, 104, "01:22", "The conclusion recommends the best bag for short trips and daily carry.")
            ],
            Frames: [new FrameArtifact("frame-001", "frames/frame-001.jpg", 10, "00:10")],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return runDirectory;
    }
}
