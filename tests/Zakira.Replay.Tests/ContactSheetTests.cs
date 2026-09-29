using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Contact sheets: all frames of a run tiled into labelled grid JPEGs plus a legend JSON, so an
/// agent reviews 98 frames in two image reads instead of 98.
/// </summary>
public sealed class ContactSheetTests
{
    [Fact]
    public async Task BuildsSheetsFromManifestAndCaptureFramesWithLegend()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://example.test/v", "sheet-run");
        var frames = await WriteFramesAsync(run, 14);
        await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("https://example.test/v", run.Id, DateTimeOffset.UnixEpoch) with { Frames = frames.Take(10).ToList() }, CancellationToken.None);
        await store.WriteJsonAsync(run, "frame-capture.json", new FrameCaptureManifest("0.1", "frame-capture", "https://example.test/v", run.Id, DateTimeOffset.UnixEpoch, new FrameCaptureRequestSummary("timestamps", null, null, null, null, null, null, null, false), frames.Skip(8).ToList(), []), CancellationToken.None);
        var renderer = new FakeRenderer();
        var service = new ContactSheetService(store, renderer);

        var result = await service.BuildAsync(new ContactSheetRequest(run.Id, Columns: 4, MaxRows: 2, ThumbWidth: 200), progress: null, CancellationToken.None);

        // 14 unique frames (2 overlapped between manifest and capture), 8 per sheet -> 2 sheets.
        Assert.Equal(14, result.Manifest.FrameCount);
        Assert.Equal(2, result.Manifest.Sheets.Count);
        Assert.Equal(8, result.Manifest.Sheets[0].Cells.Count);
        Assert.Equal(6, result.Manifest.Sheets[1].Cells.Count);
        Assert.Equal(2, result.Manifest.Sheets[1].Rows);
        Assert.Equal("sheets/sheet-001.jpg", result.Manifest.Sheets[0].Path);
        Assert.Equal(1, result.Manifest.Sheets[0].Cells[0].Index);
        Assert.Equal(9, result.Manifest.Sheets[1].Cells[0].Index);
        Assert.Equal((1, 3), (result.Manifest.Sheets[0].Cells[7].Row, result.Manifest.Sheets[0].Cells[7].Column));
        Assert.Equal("#9 00:40", result.Manifest.Sheets[1].Cells[0].Label);
        Assert.Equal(200, result.Manifest.ThumbWidth);
        Assert.Equal(112, result.Manifest.ThumbHeight);
        Assert.True(result.Manifest.Labels);
        Assert.True(File.Exists(run.GetPath(ContactSheetService.ManifestFileName)));
        Assert.Equal(2, renderer.Calls.Count);
        Assert.All(renderer.Calls, call => Assert.True(call.Labels));
        // Chronological order across sources.
        var timestamps = result.Manifest.Sheets.SelectMany(s => s.Cells).Select(c => c.TimestampSeconds).ToArray();
        Assert.Equal(timestamps.OrderBy(t => t).ToArray(), timestamps);
    }

    [Fact]
    public async Task WindowFiltersFramesAndLabelFailureFallsBackToUnlabelledSheet()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://example.test/v", "sheet-window");
        var frames = await WriteFramesAsync(run, 10);
        await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("https://example.test/v", run.Id, DateTimeOffset.UnixEpoch) with { Frames = frames }, CancellationToken.None);
        var renderer = new FakeRenderer { FailWithLabels = true };
        var service = new ContactSheetService(store, renderer);

        var result = await service.BuildAsync(new ContactSheetRequest(run.Id, From: TimeSpan.FromSeconds(10), To: TimeSpan.FromSeconds(25)), progress: null, CancellationToken.None);

        Assert.Equal(4, result.Manifest.FrameCount); // 10, 15, 20, 25
        Assert.False(result.Manifest.Labels);
        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.ContactSheetLabelsUnavailable);
        Assert.Equal(2, renderer.Calls.Count);
        Assert.False(renderer.Calls[1].Labels);
        Assert.Equal("sheets/sheet.jpg", result.Manifest.Sheets.Single().Path);
    }

    [Fact]
    public async Task MissingRunOrEmptyWindowFailsClearly()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var service = new ContactSheetService(store, new FakeRenderer());

        var missing = await Assert.ThrowsAsync<ReplayException>(() => service.BuildAsync(new ContactSheetRequest("nope"), progress: null, CancellationToken.None));
        Assert.Contains("nope", missing.Message);

        var run = store.CreateRun("https://example.test/v", "sheet-empty");
        var frames = await WriteFramesAsync(run, 3);
        await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("https://example.test/v", run.Id, DateTimeOffset.UnixEpoch) with { Frames = frames }, CancellationToken.None);
        var empty = await Assert.ThrowsAsync<ReplayException>(() => service.BuildAsync(new ContactSheetRequest(run.Id, From: TimeSpan.FromMinutes(10)), progress: null, CancellationToken.None));
        Assert.Contains("no frames", empty.Message);
    }

    [Fact]
    public void FfmpegScriptsAreEscapedForWindowsPathsAndPunctuation()
    {
        var tiles = new[]
        {
            new ContactSheetTile(@"C:\runs\it's\frame-001.jpg", "#1 00:04"),
            new ContactSheetTile(@"C:\runs\it's\frame-002.jpg", "#2 01:23:45")
        };

        var concat = FfmpegContactSheetRenderer.BuildConcatList(tiles);
        Assert.StartsWith("ffconcat version 1.0\n", concat);
        Assert.Contains("file 'C:/runs/it'\\''s/frame-001.jpg'\nduration 1\n", concat);
        // Exactly one entry per tile: a repeated last entry would render as a duplicate cell.
        Assert.Equal(2, concat.Split("file '").Length - 1);

        var commands = FfmpegContactSheetRenderer.BuildSendCmdScript(tiles);
        Assert.Contains("0 drawtext reinit 'text=#1 00\\:04';", commands);
        Assert.Contains("1 drawtext reinit 'text=#2 01\\:23\\:45';", commands);

        Assert.Equal("C\\:/fonts/it\\'s.ttf", FfmpegContactSheetRenderer.EscapeFilterPath(@"C:\fonts\it's.ttf"));
        Assert.Equal("a\\, b\\; 50%%", FfmpegContactSheetRenderer.EscapeSendCmdText("a, b; 50%"));
    }

    [SkippableFact]
    public async Task FfmpegRendersALabelledGridFromGeneratedFrames()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        var runner = new ProcessRunner();
        var probe = await runner.RunAsync(ffmpegStatus.Path!, ["-version"], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(probe.ExitCode != 0, "ffmpeg is not runnable.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("fixture", "sheet-ffmpeg");
        var frames = new List<FrameArtifact>();
        for (var i = 0; i < 7; i++)
        {
            var relative = $"frames/frame-{i + 1:000}.jpg";
            var absolute = run.GetPath(relative);
            var generate = await runner.RunAsync(ffmpegStatus.Path!, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=1", "-frames:v", "1", "-q:v", "4", "-y", absolute], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
            Skip.If(generate.ExitCode != 0, "ffmpeg could not generate fixture frames.");
            frames.Add(new FrameArtifact($"frame-{i + 1:000}", relative, i * 5, Timestamp.Format(i * 5)));
        }
        await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("fixture", run.Id, DateTimeOffset.UnixEpoch) with { Frames = frames }, CancellationToken.None);

        var service = new ContactSheetService(store, new FfmpegContactSheetRenderer(dependencies, runner));
        var result = await service.BuildAsync(new ContactSheetRequest(run.Id, Columns: 3, ThumbWidth: 160, ThumbHeight: 120), progress: null, CancellationToken.None);

        var sheet = Assert.Single(result.Manifest.Sheets);
        var sheetPath = run.GetPath(sheet.Path);
        Assert.True(File.Exists(sheetPath));
        Assert.True(new FileInfo(sheetPath).Length > 1024);
        Assert.Equal(3, sheet.Rows);
        Assert.Equal(7, sheet.Cells.Count);
        // 3 columns x 160 + 2 x 2 padding + 2 x 4 margin = 492; 3 rows x 120 + 2 x 2 + 8 = 372.
        Assert.Equal((492, 372), ReadJpegSize(sheetPath));
        Assert.Equal((492, 372), (sheet.Width, sheet.Height));
        Assert.True(result.Manifest.Labels, "drawtext labels should render on this ffmpeg build; check the warnings: " + string.Join(" | ", result.Manifest.Warnings.Select(w => w.Message)));
        Assert.Empty(result.Manifest.Warnings);
        Assert.Empty(Directory.GetFiles(run.GetPath("sheets"), "*.txt", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public async Task CliSheetCommandRendersAndEmitsJson()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        var runner = new ProcessRunner();
        var probe = await runner.RunAsync(ffmpegStatus.Path!, ["-version"], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(probe.ExitCode != 0, "ffmpeg is not runnable.");

        var previousRuns = Environment.GetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable, temp.GetPath("runs"));
            var store = new ArtifactStore(temp.GetPath("runs"));
            var run = store.CreateRun("fixture", "cli-sheet");
            var frames = new List<FrameArtifact>();
            for (var i = 0; i < 3; i++)
            {
                var relative = $"frames/frame-{i + 1:000}.jpg";
                var generate = await runner.RunAsync(ffmpegStatus.Path!, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=1", "-frames:v", "1", "-y", run.GetPath(relative)], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
                Skip.If(generate.ExitCode != 0, "ffmpeg could not generate fixture frames.");
                frames.Add(new FrameArtifact($"frame-{i + 1:000}", relative, i * 2, Timestamp.Format(i * 2)));
            }
            await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("fixture", run.Id, DateTimeOffset.UnixEpoch) with { Frames = frames }, CancellationToken.None);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await Cli.CliApp.RunAsync(["sheet", "cli-sheet", "--cols", "2", "--thumb-width", "100", "--output-format", "json"], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(stdout.ToString());
            var sheet = document.RootElement.GetProperty("sheet");
            Assert.Equal(3, sheet.GetProperty("frameCount").GetInt32());
            Assert.Equal(2, sheet.GetProperty("columns").GetInt32());
            var page = sheet.GetProperty("sheets").EnumerateArray().Single();
            Assert.True(File.Exists(page.GetProperty("path").GetString()));
            Assert.Equal(3, page.GetProperty("cells").GetArrayLength());
            Assert.True(File.Exists(sheet.GetProperty("legendPath").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable, previousRuns);
        }
    }

    private static async Task<List<FrameArtifact>> WriteFramesAsync(VideoRun run, int count)
    {
        var frames = new List<FrameArtifact>();
        for (var i = 0; i < count; i++)
        {
            var relative = $"frames/frame-{i + 1:000}.jpg";
            var absolute = run.GetPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllTextAsync(absolute, "jpg");
            frames.Add(new FrameArtifact($"frame-{i + 1:000}", relative, i * 5, Timestamp.Format(i * 5)));
        }

        return frames;
    }

    /// <summary>Reads width/height from the JPEG SOF0/SOF2 marker.</summary>
    private static (int Width, int Height) ReadJpegSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = bytes[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                var height = (bytes[i + 5] << 8) | bytes[i + 6];
                var width = (bytes[i + 7] << 8) | bytes[i + 8];
                return (width, height);
            }

            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            i += 2 + length;
        }

        throw new InvalidOperationException("no SOF marker");
    }

    private sealed class FakeRenderer : IContactSheetRenderer
    {
        public bool FailWithLabels { get; init; }
        public List<(IReadOnlyList<ContactSheetTile> Tiles, ContactSheetLayout Layout, string Output, bool Labels)> Calls { get; } = [];

        public Task<ContactSheetRenderResult> RenderAsync(IReadOnlyList<ContactSheetTile> tiles, ContactSheetLayout layout, string outputPath, bool labels, CancellationToken cancellationToken)
        {
            Calls.Add((tiles, layout, outputPath, labels));
            if (labels && FailWithLabels)
            {
                return Task.FromResult(ContactSheetRenderResult.Failed("No such filter: 'drawtext'"));
            }

            File.WriteAllText(outputPath, "jpg");
            return Task.FromResult(ContactSheetRenderResult.Ok(layout.Columns * layout.ThumbWidth, layout.Rows * layout.ThumbHeight));
        }
    }
}
