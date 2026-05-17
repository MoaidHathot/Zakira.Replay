using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class AuthProfileTests
{
    [Theory]
    [InlineData("Microsoft Ignite 2026", "microsoft-ignite-2026")]
    [InlineData("MyCorp SSO", "mycorp-sso")]
    [InlineData("ALL CAPS", "all-caps")]
    [InlineData("path/with/slashes", "path-with-slashes")]
    [InlineData("..\\..\\escape", "escape")]
    [InlineData("a", "a")]
    public void SlugifyProfileNameProducesSafeFileNames(string input, string expectedSlug)
    {
        Assert.Equal(expectedSlug, AuthProfileStore.SlugifyProfileName(input));
    }

    [Fact]
    public void GetProfilePathThrowsOnEmptyName()
    {
        var store = new AuthProfileStore(MakeConfig(), MakeConfigPath());
        Assert.Throws<ReplayException>(() => store.GetProfilePath(""));
        Assert.Throws<ReplayException>(() => store.GetProfilePath("   "));
    }

    [Fact]
    public void TryReadReturnsNullWhenProfileMissing()
    {
        using var temp = new TestTempDirectory();
        var store = new AuthProfileStore(MakeConfig(temp), MakeConfigPath(temp));

        var result = store.TryRead("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void TryReadSurfacesByteCountAndAgeForExistingProfile()
    {
        using var temp = new TestTempDirectory();
        var store = new AuthProfileStore(MakeConfig(temp), MakeConfigPath(temp));
        store.SaveJson("Acme SSO", "{\"cookies\":[],\"origins\":[]}");

        // Pretend we are reading 30 minutes after the file was written.
        var profile = store.TryRead("Acme SSO", referenceTime: DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.NotNull(profile);
        Assert.Equal("acme-sso", profile!.Slug);
        Assert.True(profile.ByteCount > 0);
        Assert.True(profile.Age.TotalMinutes >= 30 - 1, $"age was {profile.Age}");
    }

    [Fact]
    public void TryReadFlagsProfileAsStaleWhenOlderThanThreshold()
    {
        using var temp = new TestTempDirectory();
        // Threshold 5 minutes; file written "now"; reference time 10 minutes later.
        var config = MakeConfig(temp);
        config.Auth.StaleThresholdMinutes = 5;
        var store = new AuthProfileStore(config, MakeConfigPath(temp));
        store.SaveJson("session", "{}");

        var stale = store.TryRead("session", referenceTime: DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.NotNull(stale);
        Assert.True(stale!.IsStale);
    }

    [Fact]
    public void TryReadDoesNotFlagFreshProfileAsStale()
    {
        using var temp = new TestTempDirectory();
        var config = MakeConfig(temp);
        config.Auth.StaleThresholdMinutes = 60;
        var store = new AuthProfileStore(config, MakeConfigPath(temp));
        store.SaveJson("session", "{}");

        var fresh = store.TryRead("session", referenceTime: DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.NotNull(fresh);
        Assert.False(fresh!.IsStale);
    }

    [Fact]
    public void ListReturnsEmptyWhenAuthDirectoryMissing()
    {
        using var temp = new TestTempDirectory();
        // Point at a directory that doesn't yet exist.
        var config = MakeConfig(temp);
        config.Auth.Directory = Path.Combine(temp.GetPath("does-not-exist"), "auth");
        var store = new AuthProfileStore(config, MakeConfigPath(temp));

        Assert.Empty(store.List());
    }

    [Fact]
    public void ListReturnsAllSavedProfilesSorted()
    {
        using var temp = new TestTempDirectory();
        var store = new AuthProfileStore(MakeConfig(temp), MakeConfigPath(temp));
        store.SaveJson("Zeta", "{}");
        store.SaveJson("Alpha", "{}");
        store.SaveJson("Mike", "{}");

        var profiles = store.List();

        Assert.Equal(3, profiles.Count);
        Assert.Equal(["alpha", "mike", "zeta"], profiles.Select(p => p.Slug).ToArray());
    }

    [Fact]
    public void ClearRemovesProfileAndReturnsTrue()
    {
        using var temp = new TestTempDirectory();
        var store = new AuthProfileStore(MakeConfig(temp), MakeConfigPath(temp));
        store.SaveJson("temp-profile", "{}");
        Assert.NotNull(store.TryRead("temp-profile"));

        var existed = store.Clear("temp-profile");

        Assert.True(existed);
        Assert.Null(store.TryRead("temp-profile"));
    }

    [Fact]
    public void ClearReturnsFalseWhenProfileMissing()
    {
        using var temp = new TestTempDirectory();
        var store = new AuthProfileStore(MakeConfig(temp), MakeConfigPath(temp));

        Assert.False(store.Clear("never-existed"));
    }

    [Fact]
    public void DirectoryEnvironmentVariableOverridesConfig()
    {
        using var temp = new TestTempDirectory();
        var envOverride = temp.GetPath("env-auth");
        var configOverride = temp.GetPath("config-auth");
        Directory.CreateDirectory(envOverride);
        Directory.CreateDirectory(configOverride);

        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", envOverride);
            var config = MakeConfig(temp);
            config.Auth.Directory = configOverride;
            var store = new AuthProfileStore(config, MakeConfigPath(temp));

            Assert.Equal(Path.GetFullPath(envOverride), Path.GetFullPath(store.Directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", previous);
        }
    }

    [Fact]
    public void FormatAgeUsesCompactUnits()
    {
        var seconds = MakeProfile(TimeSpan.FromSeconds(45)).FormatAge();
        var minutes = MakeProfile(TimeSpan.FromMinutes(20)).FormatAge();
        var hours = MakeProfile(TimeSpan.FromHours(5.5)).FormatAge();
        var days = MakeProfile(TimeSpan.FromDays(3.2)).FormatAge();

        Assert.EndsWith("s", seconds);
        Assert.EndsWith("m", minutes);
        Assert.EndsWith("h", hours);
        Assert.EndsWith("d", days);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsAuthProfileNotFoundWhenMissing()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new RecordingBrowserCapture();
        var ytDlp = new MinimalYtDlp();
        var ffmpeg = new MinimalFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        // Point auth dir at the per-test temp dir to keep this test fully isolated.
        var prev = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        var prevEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", temp.GetPath("auth"));
            // Force the persistent-context check to fall through by pointing at a non-existent
            // path; otherwise this test would activate persistent-context on machines where the
            // user has already initialised their dedicated Edge profile.
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", temp.GetPath("no-edge-here"));
            var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "auth-not-found",
                CaptureMode: CaptureModes.Browser,
                AuthProfile: "does-not-exist"), progress: null, CancellationToken.None);

            Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.AuthProfileNotFound);
            // Browser capture still ran but with no storage state — verify the pipeline didn't
            // refuse to call the client just because the profile was missing.
            Assert.True(browser.WasCalled);
            Assert.Null(browser.LastRequest!.AuthStorageStatePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", prev);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", prevEdge);
        }
    }

    [Fact]
    public async Task AnalyzeAsyncForwardsResolvedAuthProfilePathToBrowserClient()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        // Seed a profile in a per-test auth directory.
        var authDir = temp.GetPath("auth");
        var prev = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        var prevEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", authDir);
            // Force persistent-context off so the AuthStorageStatePath assertion holds.
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", temp.GetPath("no-edge-here"));
            var profileStore = new AuthProfileStore(MakeConfig(), configPath: null);
            profileStore.SaveJson("ignite", "{\"cookies\":[],\"origins\":[]}");
            var expectedPath = profileStore.GetProfilePath("ignite");

            var browser = new RecordingBrowserCapture();
            var ytDlp = new MinimalYtDlp();
            var ffmpeg = new MinimalFfmpeg();
            var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

            var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "auth-forwarded",
                CaptureMode: CaptureModes.Browser,
                AuthProfile: "ignite"), progress: null, CancellationToken.None);

            Assert.True(browser.WasCalled);
            Assert.Equal(expectedPath, browser.LastRequest!.AuthStorageStatePath);
            Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.AuthProfileNotFound);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", prev);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", prevEdge);
        }
    }

    private static AuthProfile MakeProfile(TimeSpan age)
    {
        var now = DateTimeOffset.UtcNow;
        return new AuthProfile(
            Name: "test",
            Slug: "test",
            Path: "/tmp/test.json",
            ByteCount: 100,
            CreatedAtUtc: now - age,
            LastWriteAtUtc: now - age,
            Age: age,
            IsStale: false);
    }

    private static ReplayConfig MakeConfig(TestTempDirectory? temp = null)
    {
        var config = ConfigStore.CreateDefaultConfig();
        if (temp is not null)
        {
            config.Auth.Directory = temp.GetPath("auth");
        }
        return config;
    }

    private static string MakeConfigPath(TestTempDirectory? temp = null)
    {
        return temp is null
            ? Path.Combine(Path.GetTempPath(), "Zakira.Replay.Tests", "Zakira.Replay.json")
            : temp.GetPath("Zakira.Replay.json");
    }

    private sealed class RecordingBrowserCapture : IBrowserVideoCaptureClient
    {
        public bool WasCalled { get; private set; }

        public BrowserCaptureRequest? LastRequest { get; private set; }

        public Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastRequest = request;
            // Return one synthetic frame so the pipeline finishes cleanly without doing real work.
            var framePath = "frames/scene-0001.jpg";
            var fullPath = request.Run.GetPath(framePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            var frame = new FrameArtifact("scene-0001", framePath, 0.0, "00:00");
            return Task.FromResult(new BrowserCaptureResult([frame], 60.0, [], []));
        }
    }

    private sealed class MinimalYtDlp : IYtDlpClient
    {
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new YtDlpInfo
            {
                Id = "test",
                Title = "Test",
                WebpageUrl = request.Source
            });
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        {
            return Task.FromResult<TranscriptArtifact?>(null);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class MinimalFfmpeg : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult<double?>(null);

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>("0000000000000000");
    }
}
