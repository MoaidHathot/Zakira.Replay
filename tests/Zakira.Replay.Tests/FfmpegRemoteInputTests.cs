using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Guards the http(s) input options <see cref="FfmpegClient"/> hands to ffmpeg for remote media
/// (yt-dlp direct URLs). Without them ffmpeg answers a forward seek on a googlevideo URL by
/// draining the rest of the response - the whole file, served at playback speed - so a single
/// frame at 02:30 could take minutes and every frame stage timed out with zero frames.
/// </summary>
public sealed class FfmpegRemoteInputTests
{
    private const string RemoteUrl = "https://rr3---sn-example.googlevideo.com/videoplayback?itag=136&source=youtube";

    private const string HelpWithRequestSize =
        "https AVOptions:\n"
        + "-seekable          <boolean>    .D......... control seekability of connection (default auto)\n"
        + "-request_size      <int64>      .D......... size (in bytes) of requests to make (from 0 to I64_MAX) (default 0)\n"
        + "-short_seek_size   <int>        .D......... Threshold to favor readahead over seek. (from 0 to INT_MAX) (default 0)\n";

    private const string HelpWithoutRequestSize =
        "https AVOptions:\n"
        + "-seekable          <boolean>    .D......... control seekability of connection (default auto)\n"
        + "-short_seek_size   <int>        .D......... Threshold to favor readahead over seek. (from 0 to INT_MAX) (default 0)\n";

    [Fact]
    public async Task RemoteInputGetsShortSeekAndRequestSizeWhenFfmpegSupportsIt()
    {
        using var fixture = await Fixture.CreateAsync(HelpWithRequestSize);
        var run = fixture.CreateRun("remote-both");

        var frames = await fixture.Client.ExtractFramesAtAsync(RemoteUrl, run, [TimeSpan.FromSeconds(150)], new FrameCaptureOptions(), CancellationToken.None);

        Assert.Single(frames);
        var args = Assert.Single(fixture.CaptureInvocations);
        AssertOptionPrecedesInput(args, "-short_seek_size", FfmpegClient.RemoteInputShortSeekSizeBytes.ToString());
        AssertOptionPrecedesInput(args, "-request_size", FfmpegClient.RemoteInputRequestSizeBytes.ToString());
        // Input-side seek must still come before -i so ffmpeg seeks instead of decoding from 0.
        Assert.True(Array.IndexOf(args, "-ss") < Array.IndexOf(args, "-i"));
    }

    [Fact]
    public async Task RemoteInputOmitsRequestSizeWhenFfmpegDoesNotListTheOption()
    {
        // ffmpeg aborts on unknown options, so an older build must only get -short_seek_size.
        using var fixture = await Fixture.CreateAsync(HelpWithoutRequestSize);
        var run = fixture.CreateRun("remote-old-ffmpeg");

        var frames = await fixture.Client.ExtractFramesAtAsync(RemoteUrl, run, [TimeSpan.FromSeconds(150)], new FrameCaptureOptions(), CancellationToken.None);

        Assert.Single(frames);
        var args = Assert.Single(fixture.CaptureInvocations);
        AssertOptionPrecedesInput(args, "-short_seek_size", FfmpegClient.RemoteInputShortSeekSizeBytes.ToString());
        Assert.DoesNotContain("-request_size", args);
    }

    [Fact]
    public async Task ProbeFailureFallsBackToShortSeekOnly()
    {
        using var fixture = await Fixture.CreateAsync(helpOutput: null, helpExitCode: 1);
        var run = fixture.CreateRun("remote-probe-failed");

        var frames = await fixture.Client.ExtractFramesAtAsync(RemoteUrl, run, [TimeSpan.FromSeconds(150)], new FrameCaptureOptions(), CancellationToken.None);

        Assert.Single(frames);
        var args = Assert.Single(fixture.CaptureInvocations);
        AssertOptionPrecedesInput(args, "-short_seek_size", FfmpegClient.RemoteInputShortSeekSizeBytes.ToString());
        Assert.DoesNotContain("-request_size", args);
    }

    [Fact]
    public async Task LocalInputGetsNoRemoteOptionsAndNoProbe()
    {
        using var fixture = await Fixture.CreateAsync(HelpWithRequestSize);
        var run = fixture.CreateRun("local");
        var localMedia = fixture.Temp.GetPath("video.mp4");
        await File.WriteAllBytesAsync(localMedia, [0], CancellationToken.None);

        var frames = await fixture.Client.ExtractFramesAtAsync(localMedia, run, [TimeSpan.FromSeconds(5)], new FrameCaptureOptions(), CancellationToken.None);

        Assert.Single(frames);
        var args = Assert.Single(fixture.CaptureInvocations);
        Assert.DoesNotContain("-short_seek_size", args);
        Assert.DoesNotContain("-request_size", args);
        Assert.Equal(0, fixture.ProbeInvocations);
    }

    [Fact]
    public async Task CapabilityProbeRunsOncePerClient()
    {
        using var fixture = await Fixture.CreateAsync(HelpWithRequestSize);
        var run = fixture.CreateRun("probe-once");

        await fixture.Client.ExtractFramesAtAsync(RemoteUrl, run, [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)], new FrameCaptureOptions(), CancellationToken.None);
        await fixture.Client.ExtractAudioAsync(RemoteUrl, run, CancellationToken.None);

        Assert.Equal(1, fixture.ProbeInvocations);
        Assert.Equal(4, fixture.CaptureInvocations.Count);
        Assert.All(fixture.CaptureInvocations, args => AssertOptionPrecedesInput(args, "-request_size", FfmpegClient.RemoteInputRequestSizeBytes.ToString()));
    }

    [Fact]
    public async Task EveryRemoteCapableInvocationCarriesTheOptions()
    {
        // Audio extraction, clip extraction (both attempts) and both scene-detection paths read the
        // remote stream too; the whole-stream ones suffer most from open-ended throttled reads.
        using var fixture = await Fixture.CreateAsync(HelpWithRequestSize);
        var run = fixture.CreateRun("all-sites");

        await fixture.Client.ExtractAudioAsync(RemoteUrl, run, CancellationToken.None);
        await fixture.Client.ExtractClipAsync(RemoteUrl, run, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), outputName: null, CancellationToken.None);
        await fixture.Client.ExtractSceneFramesInRangeAsync(RemoteUrl, run, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), sceneSafetyCap: 5, new FrameCaptureOptions(), CancellationToken.None);
        await fixture.Client.ExtractFramesAsync(RemoteUrl, run, count: 3, durationSeconds: 60, FrameSelectionStrategies.EveryFrame, sceneSafetyCap: 5, CancellationToken.None);

        Assert.NotEmpty(fixture.CaptureInvocations);
        Assert.All(fixture.CaptureInvocations, args =>
        {
            AssertOptionPrecedesInput(args, "-short_seek_size", FfmpegClient.RemoteInputShortSeekSizeBytes.ToString());
            AssertOptionPrecedesInput(args, "-request_size", FfmpegClient.RemoteInputRequestSizeBytes.ToString());
        });
    }

    [Fact]
    public void RequestSizeOptionRegexMatchesHelpLineOnly()
    {
        Assert.Matches(FfmpegOutputPatterns.RequestSizeOptionRegex(), HelpWithRequestSize);
        Assert.DoesNotMatch(FfmpegOutputPatterns.RequestSizeOptionRegex(), HelpWithoutRequestSize);
        // A mention inside prose (e.g. an error message) must not count as support.
        Assert.DoesNotMatch(FfmpegOutputPatterns.RequestSizeOptionRegex(), "Unrecognized option 'request_size'.\n");
    }

    private static void AssertOptionPrecedesInput(string[] args, string option, string expectedValue)
    {
        var optionIndex = Array.IndexOf(args, option);
        var inputIndex = Array.IndexOf(args, "-i");
        Assert.True(optionIndex >= 0, $"expected '{option}' in: {string.Join(' ', args)}");
        Assert.True(inputIndex >= 0, $"expected '-i' in: {string.Join(' ', args)}");
        Assert.True(optionIndex < inputIndex, $"'{option}' must precede '-i' to apply to the input: {string.Join(' ', args)}");
        Assert.Equal(expectedValue, args[optionIndex + 1]);
    }

    /// <summary>
    /// Real <see cref="FfmpegClient"/> + <see cref="DependencyResolver"/> over a fake process
    /// runner: the "ffmpeg" answers the capability probe with canned help text and, for every
    /// other invocation, creates whatever output path the arguments name so the client sees a
    /// successful capture. Every non-probe argument list is recorded for assertions.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(TestTempDirectory temp, FfmpegClient client, ArtifactStore store, RecordingRunner runner)
        {
            Temp = temp;
            Client = client;
            this.store = store;
            this.runner = runner;
        }

        private readonly ArtifactStore store;
        private readonly RecordingRunner runner;

        public TestTempDirectory Temp { get; }

        public FfmpegClient Client { get; }

        public IReadOnlyList<string[]> CaptureInvocations => runner.CaptureInvocations;

        public int ProbeInvocations => runner.ProbeInvocations;

        public VideoRun CreateRun(string runId) => store.CreateRun("https://www.youtube.com/watch?v=example", runId);

        public static async Task<Fixture> CreateAsync(string? helpOutput, int helpExitCode = 0)
        {
            var temp = new TestTempDirectory();
            var fakeFfmpeg = temp.GetPath("fake-ffmpeg.exe");
            var fakeFfprobe = temp.GetPath("fake-ffprobe.exe");
            await File.WriteAllBytesAsync(fakeFfmpeg, [], CancellationToken.None);
            await File.WriteAllBytesAsync(fakeFfprobe, [], CancellationToken.None);

            var config = new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { FfmpegPath = fakeFfmpeg, FfprobePath = fakeFfprobe },
            };
            var runner = new RecordingRunner(helpOutput ?? string.Empty, helpExitCode);
            var client = new FfmpegClient(new DependencyResolver(config), runner);
            var store = new ArtifactStore(temp.GetPath("runs"));
            return new Fixture(temp, client, store, runner);
        }

        public void Dispose() => Temp.Dispose();
    }

    private sealed class RecordingRunner : ProcessRunner
    {
        private readonly string helpOutput;
        private readonly int helpExitCode;
        private readonly List<string[]> captureInvocations = [];

        public RecordingRunner(string helpOutput, int helpExitCode)
        {
            this.helpOutput = helpOutput;
            this.helpExitCode = helpExitCode;
        }

        public IReadOnlyList<string[]> CaptureInvocations => captureInvocations;

        public int ProbeInvocations { get; private set; }

        public override Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            if (args.Contains("-h") && args.Contains("protocol=https"))
            {
                ProbeInvocations++;
                return Task.FromResult(new ProcessResult(fileName, args, helpExitCode, helpOutput, StandardError: ""));
            }

            if (fileName.EndsWith("fake-ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ProcessResult(fileName, args, ExitCode: 0, StandardOutput: "60.0\n", StandardError: ""));
            }

            captureInvocations.Add(args);

            // ffmpeg writes its output to the last argument; create it so the client treats the
            // capture as successful. Patterns like frame-%06d.jpg get one concrete file so the
            // directory enumerations find something.
            var output = args[^1];
            if (output != "-")
            {
                var concrete = output.Contains('%') ? output.Replace("%06d", "000001").Replace("%04d", "0001") : output;
                Directory.CreateDirectory(Path.GetDirectoryName(concrete)!);
                File.WriteAllBytes(concrete, [0xFF, 0xD8, 0xFF, 0xD9]);
            }

            return Task.FromResult(new ProcessResult(fileName, args, ExitCode: 0, StandardOutput: "", StandardError: "pts_time:1.000\n"));
        }
    }
}
