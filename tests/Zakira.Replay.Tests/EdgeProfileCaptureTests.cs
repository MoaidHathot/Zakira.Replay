using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class EdgeProfileCaptureTests
{
    [Fact]
    public void ResolveEdgeUserDataDirReturnsDefaultWhenConfigNull()
    {
        var browser = new BrowserCaptureConfig();

        var resolved = browser.ResolveEdgeUserDataDir();

        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.EndsWith(Path.Combine("Zakira.Replay", "edge-profile"), resolved);
        Assert.StartsWith(expectedRoot, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveEdgeUserDataDirExpandsEnvironmentVariables()
    {
        // Use a test-owned env var so the assertion works on every OS. Windows-only vars
        // like %TEMP% / %LOCALAPPDATA% / %USERPROFILE% are not set on Linux + macOS CI
        // runners, which would leave the literal in place and make the test platform-
        // dependent. A custom var sidesteps that and still exercises the same code path
        // through Environment.ExpandEnvironmentVariables.
        const string key = "ZAKIRA_REPLAY_TEST_EDGE_BASE";
        var expectedRoot = Path.Combine(Path.GetTempPath(), "zakira-edge-expand-test");
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, expectedRoot);
            var browser = new BrowserCaptureConfig
            {
                EdgeUserDataDir = $"%{key}%/zakira-edge-test"
            };

            var resolved = browser.ResolveEdgeUserDataDir();

            Assert.False(resolved.Contains($"%{key}%", StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.IsPathFullyQualified(resolved));
            Assert.StartsWith(Path.GetFullPath(expectedRoot), resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public void ResolveEdgeProfileDirectoryDefaultsToDefault()
    {
        var browser = new BrowserCaptureConfig();

        Assert.Equal("Default", browser.ResolveEdgeProfileDirectory());
    }

    [Fact]
    public void ResolveEdgeProfileDirectoryHonorsExplicitValue()
    {
        var browser = new BrowserCaptureConfig { EdgeProfileDirectory = "Profile 1" };

        Assert.Equal("Profile 1", browser.ResolveEdgeProfileDirectory());
    }

    [Fact]
    public void IsEdgeProfileInitializedReturnsFalseWhenDirMissing()
    {
        using var temp = new TestTempDirectory();
        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("nonexistent")
        };

        Assert.False(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void IsEdgeProfileInitializedReturnsFalseWhenCookiesMissing()
    {
        using var temp = new TestTempDirectory();
        var profileDir = temp.GetPath("edge", "Default");
        Directory.CreateDirectory(profileDir);
        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("edge")
        };

        Assert.False(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void IsEdgeProfileInitializedReturnsTrueForModernNetworkCookies()
    {
        using var temp = new TestTempDirectory();
        var networkDir = temp.GetPath("edge", "Default", "Network");
        Directory.CreateDirectory(networkDir);
        File.WriteAllBytes(Path.Combine(networkDir, "Cookies"), [0xCA, 0xFE]);

        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("edge")
        };

        Assert.True(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void IsEdgeProfileInitializedReturnsTrueForLegacyCookies()
    {
        using var temp = new TestTempDirectory();
        var profileDir = temp.GetPath("edge", "Default");
        Directory.CreateDirectory(profileDir);
        File.WriteAllBytes(Path.Combine(profileDir, "Cookies"), [0xCA, 0xFE]);

        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("edge")
        };

        Assert.True(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void IsEdgeProfileInitializedHonorsNamedSubProfile()
    {
        using var temp = new TestTempDirectory();
        var networkDir = temp.GetPath("edge", "Profile 1", "Network");
        Directory.CreateDirectory(networkDir);
        File.WriteAllBytes(Path.Combine(networkDir, "Cookies"), [0x00]);

        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("edge"),
            EdgeProfileDirectory = "Profile 1"
        };

        Assert.True(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void IsEdgeProfileInitializedReturnsFalseForEmptyCookies()
    {
        using var temp = new TestTempDirectory();
        var networkDir = temp.GetPath("edge", "Default", "Network");
        Directory.CreateDirectory(networkDir);
        // Zero-byte file should not count as initialized.
        File.WriteAllBytes(Path.Combine(networkDir, "Cookies"), []);

        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = temp.GetPath("edge")
        };

        Assert.False(browser.IsEdgeProfileInitialized());
    }

    [Fact]
    public void GetEdgeProfileSingletonLockPathPointsAtCorrectFile()
    {
        var browser = new BrowserCaptureConfig
        {
            EdgeUserDataDir = "C:/zakira/edge",
            EdgeProfileDirectory = "Default"
        };

        var lockPath = browser.GetEdgeProfileSingletonLockPath();

        Assert.EndsWith(Path.Combine("Default", "SingletonLock"), lockPath);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?...", true)]
    [InlineData("https://login.live.com/", true)]
    [InlineData("https://login.windows.net/", true)]
    [InlineData("https://example.com/account/signin", true)]
    [InlineData("https://example.com/oauth2/authorize?response_type=code", true)]
    [InlineData("https://idp.example.com/saml/login", true)]
    [InlineData("https://microsofteur-my.sharepoint.com/personal/x/_layouts/15/stream.aspx?id=...", false)]
    [InlineData("https://www.youtube.com/watch?v=abc", false)]
    [InlineData("", false)]
    public void LooksLikeLoginPageMatchesKnownSignInDomains(string url, bool expected)
    {
        Assert.Equal(expected, PlaywrightVideoCaptureClient.LooksLikeLoginPage(url));
    }

    [Fact]
    public async Task AnalyzeEmitsProfileNotInitializedWhenDirMissing()
    {
        using var temp = new TestTempDirectory();
        var artifactStore = new ArtifactStore(temp.GetPath("runs"));
        var browser = new RecordingBrowserCapture();
        var ytDlp = new MinimalYtDlp();
        var ffmpeg = new MinimalFfmpeg();
        var pipeline = new AnalysisPipeline(artifactStore, ytDlp, ffmpeg, _ => null, browser);

        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousAuth = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        var previousEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", temp.GetPath("auth"));
            // Pin the resolver at a non-existent directory so the "not initialized" path runs
            // even on dev machines where the user has a real profile under %LOCALAPPDATA%.
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", temp.GetPath("no-edge-here"));

            var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "edge-profile-uninit",
                CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

            Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.CaptureBrowserProfileNotInitialized);
            Assert.True(browser.WasCalled);
            // No persistent-context fields should be set when profile isn't initialized.
            Assert.Null(browser.LastRequest!.EdgeUserDataDir);
            Assert.Null(browser.LastRequest!.EdgeProfileDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", previousAuth);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", previousEdge);
        }
    }

    [Fact]
    public async Task AnalyzeForwardsEdgeUserDataDirWhenProfileInitialized()
    {
        using var temp = new TestTempDirectory();
        var artifactStore = new ArtifactStore(temp.GetPath("runs"));

        // Seed a Cookies file so IsEdgeProfileInitialized() returns true.
        var edgeDir = temp.GetPath("edge");
        var networkDir = Path.Combine(edgeDir, "Default", "Network");
        Directory.CreateDirectory(networkDir);
        File.WriteAllBytes(Path.Combine(networkDir, "Cookies"), [0xCA, 0xFE]);

        var browser = new RecordingBrowserCapture();
        var ytDlp = new MinimalYtDlp();
        var ffmpeg = new MinimalFfmpeg();
        var pipeline = new AnalysisPipeline(artifactStore, ytDlp, ffmpeg, _ => null, browser);

        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", edgeDir);

            var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "edge-profile-active",
                CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

            Assert.True(browser.WasCalled);
            Assert.Equal(Path.GetFullPath(edgeDir), Path.GetFullPath(browser.LastRequest!.EdgeUserDataDir!));
            Assert.Equal("Default", browser.LastRequest!.EdgeProfileDirectory);
            Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.CaptureBrowserProfileNotInitialized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", previousEdge);
        }
    }

    [Fact]
    public async Task AnalyzeEmitsProfileConflictWhenBothAuthProfileAndEdgeDirAreActive()
    {
        using var temp = new TestTempDirectory();
        var artifactStore = new ArtifactStore(temp.GetPath("runs"));

        // Seed an initialized Edge profile.
        var edgeDir = temp.GetPath("edge");
        var networkDir = Path.Combine(edgeDir, "Default", "Network");
        Directory.CreateDirectory(networkDir);
        File.WriteAllBytes(Path.Combine(networkDir, "Cookies"), [0xCA, 0xFE]);

        // Seed an auth profile.
        var authDir = temp.GetPath("auth");
        Directory.CreateDirectory(authDir);

        var browser = new RecordingBrowserCapture();
        var pipeline = new AnalysisPipeline(artifactStore, new MinimalYtDlp(), new MinimalFfmpeg(), _ => null, browser);

        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousAuth = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        var previousEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", authDir);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", edgeDir);
            var profileStore = new AuthProfileStore(new ReplayConfig(), configFile);
            profileStore.SaveJson("test-profile", "{}");

            var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "edge-profile-conflict",
                CaptureMode: CaptureModes.Browser,
                AuthProfile: "test-profile"), progress: null, CancellationToken.None);

            Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.CaptureProfileConflict);
            // Persistent-context wins: AuthStorageStatePath should be null in the forwarded request.
            Assert.Null(browser.LastRequest!.AuthStorageStatePath);
            Assert.Equal(Path.GetFullPath(edgeDir), Path.GetFullPath(browser.LastRequest!.EdgeUserDataDir!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY", previousAuth);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", previousEdge);
        }
    }

    [Fact]
    public async Task ConfigSetPreservesEnvVarLiteralsForEdgeUserDataDir()
    {
        // Use a test-owned env var instead of %LOCALAPPDATA% so the resolve assertion
        // works on Linux + macOS CI runners (those platforms don't have a
        // %LOCALAPPDATA% env var, so Environment.ExpandEnvironmentVariables leaves the
        // literal in place — the assertion is about config-store behaviour, not the
        // existence of that particular Windows-only variable).
        const string envKey = "ZAKIRA_REPLAY_TEST_EDGE_PRESERVE";
        var expandedRoot = Path.Combine(Path.GetTempPath(), "zakira-replay-edge-preserve");
        using var temp = new TestTempDirectory();
        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousEnv = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable(envKey, expandedRoot);
            var literal = $"%{envKey}%/Zakira.Replay/edge-profile";
            var store = new ConfigStore();
            await store.SetAsync("capture.browser.edgeUserDataDir", literal, CancellationToken.None);

            // The value persisted to disk should preserve the env-var literal.
            var raw = await File.ReadAllTextAsync(configFile);
            Assert.Contains($"%{envKey}%", raw);

            // GetAsync returns the raw stored value as well.
            var fetched = await store.GetAsync("capture.browser.edgeUserDataDir", CancellationToken.None);
            Assert.Equal(literal, fetched);

            // ResolveEdgeUserDataDir expands the env var at read time.
            var loaded = store.Load();
            var resolved = loaded.Capture.Browser.ResolveEdgeUserDataDir();
            Assert.DoesNotContain($"%{envKey}%", resolved, StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(resolved));
            Assert.StartsWith(Path.GetFullPath(expandedRoot), resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable(envKey, previousEnv);
        }
    }

    [Fact]
    public async Task ConfigSetEdgeProfileDirectoryRoundTrips()
    {
        using var temp = new TestTempDirectory();
        var configFile = temp.GetPath("Zakira.Replay.json");
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            var store = new ConfigStore();
            await store.SetAsync("capture.browser.edgeProfileDirectory", "Profile 2", CancellationToken.None);

            var fetched = await store.GetAsync("capture.browser.edgeProfileDirectory", CancellationToken.None);
            Assert.Equal("Profile 2", fetched);

            var loaded = store.Load();
            Assert.Equal("Profile 2", loaded.Capture.Browser.ResolveEdgeProfileDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previous);
        }
    }

    [Fact]
    public async Task BrowserCaptureRequestEnablesMediaCollectionOnlyWhenSttRequestedAndNoAudio()
    {
        using var temp = new TestTempDirectory();
        var artifactStore = new ArtifactStore(temp.GetPath("runs"));
        var browser = new RecordingBrowserCapture();
        var pipeline = new AnalysisPipeline(artifactStore, new MinimalYtDlp(), new MinimalFfmpeg(), _ => null, browser);

        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", temp.GetPath("no-edge-here"));

            // Without --stt: media collection must stay off.
            await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "media-collect-off",
                CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);
            Assert.False(browser.LastRequest!.CaptureMediaForStt,
                "CaptureMediaForStt must be false when --stt is not requested");

            // With --stt and no audio source: media collection must be on.
            await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: true,
                UseSpeechToText: true,
                FrameCount: 1,
                RunId: "media-collect-on",
                CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);
            Assert.True(browser.LastRequest!.CaptureMediaForStt,
                "CaptureMediaForStt must be true when --stt is requested and no audio yet");
            Assert.True(browser.LastRequest!.MaxMediaBytes > 0,
                "MaxMediaBytes safety cap must be > 0 when media collection is enabled");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", previousEdge);
        }
    }

    [Fact]
    public async Task CaptureDebugFlagFlowsToBrowserRequest()
    {
        using var temp = new TestTempDirectory();
        var artifactStore = new ArtifactStore(temp.GetPath("runs"));
        var browser = new RecordingBrowserCapture();
        var pipeline = new AnalysisPipeline(artifactStore, new MinimalYtDlp(), new MinimalFfmpeg(), _ => null, browser);

        var configFile = temp.GetPath("Zakira.Replay.json");
        var previousConfig = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousEdge = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", temp.GetPath("no-edge-here"));

            // Default: debug stays off.
            await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "debug-default-off",
                CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);
            Assert.False(browser.LastRequest!.Debug);

            // Per-run override: CaptureDebug=true forces it on regardless of config.
            await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://example.test/private",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "debug-cli-on",
                CaptureMode: CaptureModes.Browser,
                CaptureDebug: true), progress: null, CancellationToken.None);
            Assert.True(browser.LastRequest!.Debug);
            Assert.True(browser.LastRequest!.DebugMaxBodyBytes > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfig);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR", previousEdge);
        }
    }

    [Fact]
    public async Task ConfigSetCaptureDebugRoundTrips()
    {
        using var temp = new TestTempDirectory();
        var configFile = temp.GetPath("Zakira.Replay.json");
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configFile);
            var store = new ConfigStore();
            await store.SetAsync("capture.browser.debug", "true", CancellationToken.None);

            var fetched = await store.GetAsync("capture.browser.debug", CancellationToken.None);
            Assert.Equal("True", fetched);

            var loaded = store.Load();
            Assert.True(loaded.Capture.Browser.Debug);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previous);
        }
    }

    private sealed class RecordingBrowserCapture : IBrowserVideoCaptureClient
    {
        public bool WasCalled { get; private set; }

        public BrowserCaptureRequest? LastRequest { get; private set; }

        public Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastRequest = request;
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
            => Task.FromResult(new YtDlpInfo { Id = "test", Title = "Test", WebpageUrl = request.Source });

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
            => Task.FromResult<TranscriptArtifact?>(null);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
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
