using System.Text.Json;
using NJsonSchema;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Sharpness-aware interval picking: instead of the single frame at the interval instant, a
/// small window of candidates is scored with the variance of the Laplacian and the crispest one
/// is kept. Motion-heavy videos (cooking, hands-on demos) routinely land the exact instant on a
/// smear while the frame a second away is sharp.
/// </summary>
public sealed class SharpnessSelectionTests
{
    [Fact]
    public void VarianceOfLaplacianRanksCheckerboardAboveGradientAboveFlat()
    {
        const int w = 16, h = 16;
        var flat = Enumerable.Repeat((byte)128, w * h).ToArray();
        var gradient = Enumerable.Range(0, w * h).Select(i => (byte)(i % w * 16)).ToArray();
        var checker = Enumerable.Range(0, w * h).Select(i => (byte)(((i % w) + (i / w)) % 2 == 0 ? 0 : 255)).ToArray();

        var flatScore = SharpnessScore.VarianceOfLaplacian(flat, w, h);
        var gradientScore = SharpnessScore.VarianceOfLaplacian(gradient, w, h);
        var checkerScore = SharpnessScore.VarianceOfLaplacian(checker, w, h);

        Assert.Equal(0, flatScore);
        Assert.True(checkerScore > gradientScore, $"checker {checkerScore} should beat gradient {gradientScore}");
        Assert.True(gradientScore >= 0);
        Assert.Equal(0, SharpnessScore.VarianceOfLaplacian(new byte[4], 2, 2));

        var stream = flat.Concat(checker).Concat(gradient).ToArray();
        var scores = SharpnessScore.ScoreFrames(stream, w, h);
        Assert.Equal(3, scores.Length);
        Assert.Equal(1, Array.IndexOf(scores, scores.Max()));
    }

    [Fact]
    public void ModeResolutionFollowsAutoOnOffAndMediaLocality()
    {
        var frames = new FramesConfig();
        Assert.NotNull(SharpnessModes.Resolve(frames, mediaIsLocal: true));
        Assert.Null(SharpnessModes.Resolve(frames, mediaIsLocal: false));
        Assert.NotNull(SharpnessModes.Resolve(new FramesConfig { Sharpness = "on" }, mediaIsLocal: false));
        Assert.Null(SharpnessModes.Resolve(new FramesConfig { Sharpness = "off" }, mediaIsLocal: true));
        var custom = SharpnessModes.Resolve(new FramesConfig { Sharpness = "on", SharpnessWindowSeconds = 2.5, SharpnessCandidates = 5 }, mediaIsLocal: true);
        Assert.Equal((2.5, 5), (custom!.WindowSeconds, custom.Candidates));
        Assert.Throws<ReplayException>(() => SharpnessModes.Normalize("sometimes"));
    }

    [Fact]
    public async Task ConfigKeysRoundTrip()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("frames.sharpness", "on", CancellationToken.None);
        await store.SetAsync("frames.sharpness-window-seconds", "1.5", CancellationToken.None);
        await store.SetAsync("frames.sharpnessCandidates", "5", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("on", config.Frames.Sharpness);
        Assert.Equal(1.5, config.Frames.SharpnessWindowSeconds);
        Assert.Equal(5, config.Frames.SharpnessCandidates);
        Assert.Equal("auto", await new ConfigStore(temp.GetPath("fresh.json")).GetAsync("frames.sharpness", CancellationToken.None));
    }

    [SkippableFact]
    public async Task SharpestCandidateWinsOverTheBlurredIntervalInstant()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        var ffprobeStatus = dependencies.GetFfprobeStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        Skip.If(!ffprobeStatus.IsFound || string.IsNullOrWhiteSpace(ffprobeStatus.Path), "ffprobe is not configured.");
        var runner = new ProcessRunner();
        var probe = await runner.RunAsync(ffmpegStatus.Path!, ["-version"], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(probe.ExitCode != 0, "ffmpeg is not runnable.");

        // 4-second high-detail test pattern; frames between 1.4 s and 2.6 s are heavily blurred,
        // so the single interval instant of a one-frame request (t = 2.0 s) lands on the blur.
        var fixturePath = temp.GetPath("blurry-middle.mp4");
        var create = await runner.RunAsync(ffmpegStatus.Path!,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=4",
            "-vf", "boxblur=luma_radius=12:luma_power=3:enable='between(t,1.4,2.6)'",
            "-pix_fmt", "yuv420p", "-y", fixturePath
        ], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(create.ExitCode != 0 || !File.Exists(fixturePath), "ffmpeg could not generate the fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var client = new FfmpegClient(dependencies, runner);

        var plainRun = store.CreateRun(fixturePath, "sharp-off");
        var plain = await client.ExtractFramesAsync(fixturePath, plainRun, 1, 4.0, FrameSelectionStrategies.Interval, 2000, new FrameCaptureOptions(), CancellationToken.None);
        var sharpRun = store.CreateRun(fixturePath, "sharp-on");
        var sharp = await client.ExtractFramesAsync(fixturePath, sharpRun, 1, 4.0, FrameSelectionStrategies.Interval, 2000, new FrameCaptureOptions(Sharpness: new SharpnessSelection(WindowSeconds: 1.0, Candidates: 3)), CancellationToken.None);

        var plainFrame = Assert.Single(plain);
        var sharpFrame = Assert.Single(sharp);
        Assert.Equal(2.0, plainFrame.TimestampSeconds);
        Assert.Null(plainFrame.SharpnessScore);
        Assert.Equal("frame-001", sharpFrame.Id);
        Assert.True(File.Exists(sharpRun.GetPath(sharpFrame.Path)));
        Assert.NotNull(sharpFrame.SharpnessScore);
        Assert.True(sharpFrame.TimestampSeconds is < 1.4 or > 2.6, $"expected a candidate outside the blurred window, got {sharpFrame.TimestampSeconds}s");
        Assert.Equal(2.0, sharpFrame.RequestedTimestampSeconds);
        Assert.Empty(Directory.GetDirectories(sharpRun.GetPath("frames"), ".sharp-*"));
        Assert.Single(Directory.GetFiles(sharpRun.GetPath("frames"), "*.jpg"));

        // Artifacts with the new fields still satisfy the published schemas.
        var manifest = AnalysisPipelineTests.CreateManifest(fixturePath, sharpRun.Id, DateTimeOffset.UnixEpoch) with { Frames = sharp };
        await store.WriteJsonAsync(sharpRun, "manifest.json", manifest, CancellationToken.None);
        var schema = await JsonSchema.FromFileAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", "manifest.schema.json")));
        Assert.Empty(schema.Validate(await File.ReadAllTextAsync(sharpRun.GetPath("manifest.json"))).Select(e => e.ToString()));
    }

    [SkippableFact]
    public async Task StaticContentKeepsTheIntervalInstant()
    {
        using var temp = new TestTempDirectory();
        var dependencies = new DependencyResolver();
        var ffmpegStatus = dependencies.GetFfmpegStatus();
        Skip.If(!ffmpegStatus.IsFound || string.IsNullOrWhiteSpace(ffmpegStatus.Path), "ffmpeg is not configured.");
        var runner = new ProcessRunner();
        var probe = await runner.RunAsync(ffmpegStatus.Path!, ["-version"], timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(probe.ExitCode != 0, "ffmpeg is not runnable.");

        // A still image as video: every candidate scores the same, so the exact instant wins.
        var fixturePath = temp.GetPath("static.mp4");
        var create = await runner.RunAsync(ffmpegStatus.Path!,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "smptebars=size=320x240:rate=10:duration=4", "-pix_fmt", "yuv420p", "-y", fixturePath],
            timeout: TimeSpan.FromSeconds(60), cancellationToken: CancellationToken.None);
        Skip.If(create.ExitCode != 0, "ffmpeg could not generate the fixture.");

        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun(fixturePath, "sharp-static");
        var frames = await new FfmpegClient(dependencies, runner).ExtractFramesAsync(fixturePath, run, 1, 4.0, FrameSelectionStrategies.Interval, 2000, new FrameCaptureOptions(Sharpness: SharpnessSelection.Default), CancellationToken.None);

        var frame = Assert.Single(frames);
        Assert.Equal(2.0, frame.TimestampSeconds);
        Assert.Null(frame.RequestedTimestampSeconds);
    }
}
