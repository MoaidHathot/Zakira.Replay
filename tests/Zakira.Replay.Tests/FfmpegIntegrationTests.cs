using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class FfmpegIntegrationTests
{
    [SkippableFact]
    public async Task FfmpegClientExtractsFramesAndAudioFromGeneratedFixture()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        var ffprobeStatus = dependencies.GetFfprobeStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        Skip.If(!ffprobeStatus.IsFound || string.IsNullOrWhiteSpace(ffprobeStatus.Path), "ffprobe is not configured.");

        var runner = new ProcessRunner();
        await SkipIfNotRunnableAsync(runner, ffmpegStatus.Path!, "ffmpeg is not runnable.");
        await SkipIfNotRunnableAsync(runner, ffprobeStatus.Path!, "ffprobe is not runnable.");

        var fixturePath = temp.GetPath("fixture.mp4");
        var createFixture = await runner.RunAsync(
            ffmpegStatus.Path!,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=160x120:rate=10:duration=2",
                "-f", "lavfi",
                "-i", "sine=frequency=1000:sample_rate=16000:duration=2",
                "-shortest",
                "-pix_fmt", "yuv420p",
                "-y",
                fixturePath
            ],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        Skip.If(createFixture.ExitCode != 0 || !File.Exists(fixturePath), "ffmpeg could not generate the test fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun(fixturePath, "ffmpeg-fixture");
        var client = new FfmpegClient(dependencies, runner);

        var duration = await client.TryProbeDurationAsync(fixturePath, CancellationToken.None);
        var frames = await client.ExtractFramesAsync(fixturePath, run, count: 2, duration, FrameSelectionStrategies.Interval, CancellationToken.None);
        var audioPath = await client.ExtractAudioAsync(fixturePath, run, CancellationToken.None);

        Assert.True(duration >= 1.5);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.True(File.Exists(run.GetPath(frame.Path))));
        Assert.True(File.Exists(run.GetPath(audioPath)));
    }

    [SkippableFact]
    public async Task FfmpegClientExtractsTimestampedClipFromGeneratedFixture()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");

        var runner = new ProcessRunner();
        await SkipIfNotRunnableAsync(runner, ffmpegStatus.Path!, "ffmpeg is not runnable.");

        var fixturePath = temp.GetPath("fixture.mp4");
        var createFixture = await runner.RunAsync(
            ffmpegStatus.Path!,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=160x120:rate=10:duration=2",
                "-f", "lavfi",
                "-i", "sine=frequency=1000:sample_rate=16000:duration=2",
                "-shortest",
                "-pix_fmt", "yuv420p",
                "-y",
                fixturePath
            ],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        Skip.If(createFixture.ExitCode != 0 || !File.Exists(fixturePath), "ffmpeg could not generate the test fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun(fixturePath, "ffmpeg-clip-fixture");
        var client = new FfmpegClient(dependencies, runner);

        var clipPath = await client.ExtractClipAsync(fixturePath, run, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(1), "sample", CancellationToken.None);

        Assert.Equal("clips/sample.mp4", clipPath);
        Assert.True(File.Exists(run.GetPath(clipPath)));
    }

    [SkippableFact]
    public async Task FfmpegClientSceneStrategyProducesFramesOrFallsBackToInterval()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        var ffprobeStatus = dependencies.GetFfprobeStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        Skip.If(!ffprobeStatus.IsFound || string.IsNullOrWhiteSpace(ffprobeStatus.Path), "ffprobe is not configured.");

        var runner = new ProcessRunner();
        await SkipIfNotRunnableAsync(runner, ffmpegStatus.Path!, "ffmpeg is not runnable.");
        await SkipIfNotRunnableAsync(runner, ffprobeStatus.Path!, "ffprobe is not runnable.");

        var fixturePath = temp.GetPath("fixture.mp4");
        var createFixture = await runner.RunAsync(
            ffmpegStatus.Path!,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=160x120:rate=10:duration=2",
                "-pix_fmt", "yuv420p",
                "-y",
                fixturePath
            ],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        Skip.If(createFixture.ExitCode != 0 || !File.Exists(fixturePath), "ffmpeg could not generate the test fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun(fixturePath, "ffmpeg-scene-fixture");
        var client = new FfmpegClient(dependencies, runner);

        var frames = await client.ExtractFramesAsync(fixturePath, run, count: 1, durationSeconds: null, FrameSelectionStrategies.Scene, CancellationToken.None);

        Assert.Single(frames);
        Assert.True(File.Exists(run.GetPath(frames[0].Path)));
    }

    [SkippableFact]
    public async Task FfmpegClientEveryFrameStrategyExtractsSequentialFrames()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        var ffprobeStatus = dependencies.GetFfprobeStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        Skip.If(!ffprobeStatus.IsFound || string.IsNullOrWhiteSpace(ffprobeStatus.Path), "ffprobe is not configured.");

        var runner = new ProcessRunner();
        await SkipIfNotRunnableAsync(runner, ffmpegStatus.Path!, "ffmpeg is not runnable.");
        await SkipIfNotRunnableAsync(runner, ffprobeStatus.Path!, "ffprobe is not runnable.");

        var fixturePath = temp.GetPath("fixture.mp4");
        var createFixture = await runner.RunAsync(
            ffmpegStatus.Path!,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=160x120:rate=3:duration=2",
                "-pix_fmt", "yuv420p",
                "-y",
                fixturePath
            ],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        Skip.If(createFixture.ExitCode != 0 || !File.Exists(fixturePath), "ffmpeg could not generate the test fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun(fixturePath, "ffmpeg-every-frame-fixture");
        var client = new FfmpegClient(dependencies, runner);

        var frames = await client.ExtractFramesAsync(fixturePath, run, count: 3, durationSeconds: null, FrameSelectionStrategies.EveryFrame, CancellationToken.None);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame => Assert.True(File.Exists(run.GetPath(frame.Path))));
    }

    private static async Task SkipIfNotRunnableAsync(ProcessRunner runner, string executablePath, string reason)
    {
        try
        {
            var result = await runner.RunAsync(executablePath, ["-version"], timeout: TimeSpan.FromSeconds(15), cancellationToken: CancellationToken.None);
            Skip.If(result.ExitCode != 0, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Skip.If(true, reason);
        }
    }
}
