using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Guards the format selector <see cref="YtDlpClient.GetBestMediaUrlAsync"/> hands to yt-dlp for
/// the direct URL ffmpeg reads. YouTube no longer exposes progressive (video + audio) https
/// streams for most videos, so the video-only branches decide what ffmpeg has to pull: without a
/// resolution cap the selector landed on the highest rendition (4K AV1) for thumbnails.
/// </summary>
public sealed class YtDlpFormatSelectorTests
{
    [Fact]
    public async Task GetBestMediaUrlPassesTheDirectMediaSelectorAndReturnsFirstUrl()
    {
        using var temp = new TestTempDirectory();
        string[]? captured = null;
        var runner = new CapturingRunner(args =>
        {
            captured = args;
            return new ProcessResult("yt-dlp", args, ExitCode: 0, StandardOutput: "https://cdn.example/video\r\nhttps://cdn.example/audio\r\n", StandardError: "");
        });

        var fakeYtDlp = temp.GetPath("fake-yt-dlp.exe");
        await File.WriteAllBytesAsync(fakeYtDlp, [], CancellationToken.None);
        var client = new YtDlpClient(new DependencyResolver(new ReplayConfig { Dependencies = new DependencyPathConfig { YtDlpPath = fakeYtDlp } }), runner);
        var request = new AnalyzeRequest(Source: "https://www.youtube.com/watch?v=example", VisionInstruction: "", IncludeTranscript: false, FrameCount: 5, RunId: "selector");

        var url = await client.GetBestMediaUrlAsync(request, CancellationToken.None);

        Assert.Equal("https://cdn.example/video", url);
        Assert.NotNull(captured);
        var formatIndex = Array.IndexOf(captured!, "-f");
        Assert.True(formatIndex >= 0);
        Assert.Equal(YtDlpClient.DirectMediaUrlFormatSelector, captured[formatIndex + 1]);
        Assert.Contains("--no-playlist", captured);
        Assert.Equal("-g", captured[^2]);
        Assert.Equal(request.Source, captured[^1]);
    }

    [Fact]
    public void DirectMediaSelectorPrefersProgressiveThenCappedH264ThenAnything()
    {
        var branches = YtDlpClient.DirectMediaUrlFormatSelector.Split('/');

        // Progressive (video + audio) first: the only branch that also serves audio extraction.
        Assert.Equal("best[protocol=https][vcodec!=none][acodec!=none][ext=mp4]", branches[0]);
        Assert.Equal("best[protocol=https][vcodec!=none][acodec!=none]", branches[1]);

        // Then a resolution-capped, H.264-first video-only stream...
        Assert.Equal("bestvideo[protocol=https][vcodec^=avc1][height<=720][ext=mp4]", branches[2]);
        Assert.Equal("bestvideo[protocol=https][height<=720][ext=mp4]", branches[3]);

        // ...before the uncapped fallbacks that would pick 4K AV1.
        var cappedIndex = Array.IndexOf(branches, "bestvideo[protocol=https][height<=720][ext=mp4]");
        var uncappedIndex = Array.IndexOf(branches, "best[protocol=https][vcodec!=none][ext=mp4]");
        Assert.True(cappedIndex >= 0 && uncappedIndex > cappedIndex);
        Assert.Equal("b", branches[^1]);
        Assert.All(branches.Where(b => b != "b" && b != "bv*+ba"), b => Assert.Contains("[protocol=https]", b));
    }

    private sealed class CapturingRunner : ProcessRunner
    {
        private readonly Func<string[], ProcessResult> handler;

        public CapturingRunner(Func<string[], ProcessResult> handler)
        {
            this.handler = handler;
        }

        public override Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(handler(arguments.ToArray()));
        }
    }
}
