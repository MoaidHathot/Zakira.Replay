using System.Runtime.InteropServices;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Tests;

/// <summary>
/// Subprocess timeout diagnostics. Before these changes a timed-out yt-dlp/ffmpeg run threw a
/// bare "Process timed out after 00:05:00" and discarded everything the process had printed,
/// which made a YouTube bot check, an HTTP 429 and a slow network indistinguishable. The
/// budgets themselves were hardcoded; they now come from <c>timeouts.*</c> in the config.
/// </summary>
public sealed class ProcessTimeoutTests
{
    [Fact]
    public async Task TimeoutPreservesOutputPrintedBeforeTheKill()
    {
        var runner = new ProcessRunner();
        var (fileName, args) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", new[] { "/c", "echo WARNING: [youtube] Sign in to confirm you're not a bot 1>&2 & echo still-working & ping -n 30 127.0.0.1 >nul" })
            : ("/bin/sh", new[] { "-c", "echo 'WARNING: [youtube] Sign in to confirm you'\"'\"'re not a bot' 1>&2; echo still-working; sleep 30" });

        var ex = await Assert.ThrowsAsync<ProcessTimeoutException>(() => runner.RunAsync(fileName, args, timeout: TimeSpan.FromSeconds(2), cancellationToken: CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(2), ex.Timeout);
        Assert.Equal(fileName, ex.FileName);
        Assert.Contains("Sign in to confirm you're not a bot", ex.StandardError);
        Assert.Contains("still-working", ex.StandardOutput);
        Assert.Contains("Sign in to confirm you're not a bot", ex.OutputTail);
        // The message alone must carry the evidence: most callers only log ex.Message.
        Assert.Contains("Process timed out after 00:00:02", ex.Message);
        Assert.Contains("Last 1 line(s) of stderr:", ex.Message);
        Assert.Contains("Sign in to confirm", ex.Message);
        Assert.Contains("--cookies", ex.Message);
        Assert.NotNull(ex.Hint);
        Assert.IsAssignableFrom<ReplayException>(ex);
    }

    [Fact]
    public void MessageEmbedsOnlyTheTailOfLongOutput()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line-{i:00}").ToArray();
        var ex = new ProcessTimeoutException("yt-dlp", ["-g", "x"], TimeSpan.FromMinutes(5), standardOutput: "", standardError: string.Join('\n', lines));

        Assert.Contains("Last 20 line(s) of stderr:", ex.Message);
        Assert.Contains("line-40", ex.Message);
        Assert.Contains("line-21", ex.Message);
        Assert.DoesNotContain("line-20\n", ex.Message + "\n");
        Assert.DoesNotContain("line-01", ex.Message);
        Assert.Equal(20, ex.OutputTail.Split(Environment.NewLine).Length);
        Assert.Null(ex.Hint);
    }

    [Fact]
    public void MessageFallsBackToStdoutAndReportsSilence()
    {
        var withStdout = new ProcessTimeoutException("ffmpeg", [], TimeSpan.FromSeconds(30), standardOutput: "frame=1\nframe=2", standardError: "   ");
        Assert.Contains("Last 2 line(s) of stdout:", withStdout.Message);
        Assert.Contains("frame=2", withStdout.OutputTail);

        var silent = new ProcessTimeoutException("ffmpeg", [], TimeSpan.FromSeconds(30), standardOutput: "", standardError: "");
        Assert.Contains("No output was captured", silent.Message);
        Assert.Equal(string.Empty, silent.OutputTail);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies-from-browser", "--cookies")]
    [InlineData("ERROR: unable to download video data: HTTP Error 429: Too Many Requests", "rate-limiting")]
    [InlineData("WARNING: [youtube] abc: nsig extraction failed: Some formats may be missing", "JavaScript runtime")]
    [InlineData("ERROR: [youtube] abc: Requested format is not available.", "yt-dlp -F")]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", "private")]
    public void SuggestHintRecognisesKnownSignatures(string output, string expectedFragment)
    {
        var hint = SubprocessDiagnostics.SuggestHint(output);

        Assert.NotNull(hint);
        Assert.Contains(expectedFragment, hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestHintReturnsNullForUnknownOutput()
    {
        Assert.Null(SubprocessDiagnostics.SuggestHint("[download] 100% of 3.2MiB"));
        Assert.Null(SubprocessDiagnostics.SuggestHint(null));
    }

    [Fact]
    public void JobFailureDescriptionAppendsStderrAndHintForProcessFailures()
    {
        var failed = new ProcessFailedException("yt-dlp", ["-g"], 1, "ERROR: HTTP Error 429: Too Many Requests");
        var described = McpJob.DescribeFailure(failed);

        Assert.StartsWith("Process failed with exit code 1: yt-dlp", described);
        Assert.Contains("HTTP Error 429", described);
        Assert.Contains("Hint:", described);

        var timeout = new ProcessTimeoutException("yt-dlp", ["-g"], TimeSpan.FromMinutes(5), "", "ERROR: HTTP Error 429");
        Assert.Equal(timeout.Message, McpJob.DescribeFailure(timeout));
        Assert.Contains("Hint:", timeout.Message);
    }

    [Fact]
    public void TimeoutsConfigDefaultsReproducePreviousHardcodedBudgets()
    {
        var timeouts = new TimeoutsConfig();

        Assert.Equal(TimeSpan.FromMinutes(5), TimeoutsConfig.Resolve(timeouts.YtDlpMetadataSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(10), TimeoutsConfig.Resolve(timeouts.YtDlpSubtitlesSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(5), TimeoutsConfig.Resolve(timeouts.YtDlpMediaUrlSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(30), TimeoutsConfig.Resolve(timeouts.YtDlpDownloadSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(3), TimeoutsConfig.Resolve(timeouts.FfmpegFrameSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(15), TimeoutsConfig.Resolve(timeouts.FfmpegPassSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(10), TimeoutsConfig.Resolve(timeouts.FfmpegAudioSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(10), TimeoutsConfig.Resolve(timeouts.FfmpegClipSeconds, 1));
        Assert.Equal(TimeSpan.FromMinutes(2), TimeoutsConfig.Resolve(timeouts.FfprobeSeconds, 1));
        // Zero / negative values (e.g. an old config file with the key missing) fall back.
        Assert.Equal(TimeSpan.FromSeconds(7), TimeoutsConfig.Resolve(0, 7));
        Assert.Equal(TimeSpan.FromSeconds(7), TimeoutsConfig.Resolve(-1, 7));
    }

    [Fact]
    public async Task ConfigStoreRoundTripsTimeoutKeys()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("timeouts.ytDlpMediaUrlSeconds", "420", CancellationToken.None);
        await store.SetAsync("timeouts.ffmpeg-frame-seconds", "45", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(420, config.Timeouts.YtDlpMediaUrlSeconds);
        Assert.Equal(45, config.Timeouts.FfmpegFrameSeconds);
        Assert.Equal("420", await store.GetAsync("timeouts.ytDlpMediaUrlSeconds", CancellationToken.None));
        Assert.Equal("300", await store.GetAsync("timeouts.ytDlpMetadataSeconds", CancellationToken.None));
        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("timeouts.ffprobeSeconds", "0", CancellationToken.None));
    }

    [Fact]
    public async Task YtDlpAndFfmpegClientsUseConfiguredTimeouts()
    {
        using var temp = new TestTempDirectory();
        var fakeYtDlp = temp.GetPath("fake-yt-dlp.exe");
        var fakeFfmpeg = temp.GetPath("fake-ffmpeg.exe");
        var fakeFfprobe = temp.GetPath("fake-ffprobe.exe");
        foreach (var path in new[] { fakeYtDlp, fakeFfmpeg, fakeFfprobe })
        {
            await File.WriteAllBytesAsync(path, [], CancellationToken.None);
        }

        var config = new ReplayConfig
        {
            Dependencies = new DependencyPathConfig { YtDlpPath = fakeYtDlp, FfmpegPath = fakeFfmpeg, FfprobePath = fakeFfprobe },
            Timeouts = new TimeoutsConfig { YtDlpMediaUrlSeconds = 361, YtDlpMetadataSeconds = 77, FfmpegFrameSeconds = 33, FfprobeSeconds = 11 }
        };
        var recorded = new List<(string File, TimeSpan? Timeout)>();
        var runner = new TimeoutRecordingRunner((file, args, timeout) =>
        {
            recorded.Add((Path.GetFileName(file), timeout));
            if (args.Contains("--dump-single-json"))
            {
                return new ProcessResult(file, args, 0, "{\"id\":\"x\",\"title\":\"t\",\"duration\":10}", "");
            }
            if (args.Contains("-g"))
            {
                return new ProcessResult(file, args, 0, "https://cdn.example/v\n", "");
            }
            if (file == fakeFfprobe)
            {
                return new ProcessResult(file, args, 0, "12.5\n", "");
            }
            return new ProcessResult(file, args, 0, "", "");
        });

        var dependencies = new DependencyResolver(config);
        var ytDlp = new YtDlpClient(dependencies, runner);
        var ffmpeg = new FfmpegClient(dependencies, runner);
        var request = new AnalyzeRequest(Source: "https://example.test/v", VisionInstruction: "", IncludeTranscript: false, FrameCount: 1, RunId: "timeouts");

        await ytDlp.GetInfoAsync(request, CancellationToken.None);
        await ytDlp.GetBestMediaUrlAsync(request, CancellationToken.None);
        await ffmpeg.TryProbeDurationAsync(temp.GetPath("local.mp4"), CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("local.mp4", "timeouts");
        await ffmpeg.ExtractFramesAtAsync(temp.GetPath("local.mp4"), run, [TimeSpan.FromSeconds(1)], new FrameCaptureOptions(), CancellationToken.None);

        Assert.Contains(recorded, r => r.File == "fake-yt-dlp.exe" && r.Timeout == TimeSpan.FromSeconds(77));
        Assert.Contains(recorded, r => r.File == "fake-yt-dlp.exe" && r.Timeout == TimeSpan.FromSeconds(361));
        Assert.Contains(recorded, r => r.File == "fake-ffprobe.exe" && r.Timeout == TimeSpan.FromSeconds(11));
        Assert.Contains(recorded, r => r.File == "fake-ffmpeg.exe" && r.Timeout == TimeSpan.FromSeconds(33));
    }

    private sealed class TimeoutRecordingRunner : ProcessRunner
    {
        private readonly Func<string, string[], TimeSpan?, ProcessResult> handler;

        public TimeoutRecordingRunner(Func<string, string[], TimeSpan?, ProcessResult> handler)
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
            return Task.FromResult(handler(fileName, arguments.ToArray(), timeout));
        }
    }
}
