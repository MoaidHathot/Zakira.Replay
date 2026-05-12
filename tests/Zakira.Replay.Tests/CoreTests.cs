using Zakira.Replay.Core;
using Zakira.Replay.Cli;
using System.IO.Compression;
using System.Net;

namespace Zakira.Replay.Tests;

public sealed class CoreTests
{
    [Fact]
    public async Task ConfigStoreSupportsCaptionLanguagesSetting()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("captions.languages", "fr, en, live_chat", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("fr,en,live_chat", await store.GetAsync("captions.languages", CancellationToken.None));
        Assert.Equal(["fr", "en", "live_chat"], config.Captions.Languages);
    }

    [Fact]
    public async Task ConfigStoreCaptionLanguagesDefaultsToAuto()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        var config = await store.EnsureExistsAsync(CancellationToken.None);

        Assert.Equal(["auto"], config.Captions.Languages);
        Assert.Equal("auto", await store.GetAsync("captions.languages", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigStoreSetAsyncNormalizesDirectoryValuesToExecutablePaths()
    {
        using var temp = new TestTempDirectory();
        var toolDirectory = temp.GetPath("tools");
        Directory.CreateDirectory(toolDirectory);

        var store = new ConfigStore(temp.GetPath("config.json"));
        await store.SetAsync("yt_dlp.path", toolDirectory, CancellationToken.None);

        Assert.Equal(System.IO.Path.Combine(toolDirectory, "yt-dlp.exe"), await store.GetAsync("yt-dlp.path", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigStoreSupportsLlmProviderSettings()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("llm.provider", "openai", CancellationToken.None);
        await store.SetAsync("llm.openai.model", "gpt-4o-mini", CancellationToken.None);
        await store.SetAsync("llm.openai.baseUrl", "https://api.openai.com/v1", CancellationToken.None);
        await store.SetAsync("llm.openai.apiKeyEnvVars", "CUSTOM_OPENAI_KEY,OPENAI_API_KEY", CancellationToken.None);
        await store.SetAsync("llm.azureOpenAi.apiKeyEnvVars", "CUSTOM_AZURE_KEY;AZURE_OPENAI_API_KEY", CancellationToken.None);

        Assert.Equal("openai", await store.GetAsync("llm.provider", CancellationToken.None));
        Assert.Equal("gpt-4o-mini", await store.GetAsync("llm.openai.model", CancellationToken.None));
        Assert.Equal("https://api.openai.com/v1", await store.GetAsync("llm.openai.baseUrl", CancellationToken.None));
        Assert.Equal("CUSTOM_OPENAI_KEY,OPENAI_API_KEY", await store.GetAsync("llm.openai.apiKeyEnvVars", CancellationToken.None));
        Assert.Equal("CUSTOM_AZURE_KEY,AZURE_OPENAI_API_KEY", await store.GetAsync("llm.azureOpenAi.apiKeyEnvVars", CancellationToken.None));
    }

    [Fact]
    public void ConfigStoreGetDefaultConfigPathHonorsEnvironmentOverride()
    {
        using var temp = new TestTempDirectory();
        var configuredPath = temp.GetPath("custom-config.json");
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");

        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configuredPath);

            Assert.Equal(configuredPath, ConfigStore.GetDefaultConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previous);
        }
    }

    [Fact]
    public void ConfigStoreGetDefaultConfigPathHonorsXdgConfigHome()
    {
        using var temp = new TestTempDirectory();
        var previousConfigPath = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        var previousXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temp.Path);

            Assert.Equal(System.IO.Path.Combine(temp.Path, "Zakira.Replay", "Zakira.Replay.json"), ConfigStore.GetDefaultConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfigPath);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousXdgConfigHome);
        }
    }

    [Theory]
    [InlineData("VideoWatcher.json")]
    [InlineData("VideoWatcher.config")]
    [InlineData("config.json")]
    public async Task ConfigStoreMigratesLegacyVideoWatcherConfigToNewLocation(string legacyFileName)
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay", "Zakira.Replay.json");
        var legacyDirectory = temp.GetPath("VideoWatcher");
        var legacyPath = System.IO.Path.Combine(legacyDirectory, legacyFileName);
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(legacyPath, """
            {
              "llm": {
                "provider": "openai"
              }
            }
            """, CancellationToken.None);
        var store = new ConfigStore(configPath);

        var loaded = await store.LoadAsync(CancellationToken.None);
        await store.SetAsync("llm.provider", "azure-openai", CancellationToken.None);

        Assert.Equal("openai", loaded.Llm.Provider);
        Assert.True(File.Exists(configPath), "New config file should exist after migration.");
        Assert.False(File.Exists(legacyPath), "Legacy config file should be removed after migration.");
        Assert.False(Directory.Exists(legacyDirectory), "Empty legacy directory should be removed after migration.");
        Assert.Equal("azure-openai", await store.GetAsync("llm.provider", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigStoreMigrationDeletesLegacyEvenWhenNewFileAlreadyExists()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay", "Zakira.Replay.json");
        var legacyDirectory = temp.GetPath("VideoWatcher");
        var legacyPath = System.IO.Path.Combine(legacyDirectory, "VideoWatcher.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(configPath, """
            {
              "llm": {
                "provider": "azure-openai"
              }
            }
            """, CancellationToken.None);
        await File.WriteAllTextAsync(legacyPath, """
            {
              "llm": {
                "provider": "openai"
              }
            }
            """, CancellationToken.None);
        var store = new ConfigStore(configPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("azure-openai", loaded.Llm.Provider);
        Assert.True(File.Exists(configPath), "New config file should be preserved.");
        Assert.False(File.Exists(legacyPath), "Legacy config file should be removed even when the new file already exists.");
        Assert.False(Directory.Exists(legacyDirectory), "Empty legacy directory should be removed.");
    }

    [Fact]
    public async Task ConfigStoreEnsureExistsCreatesDefaultConfigFile()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay", "Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        var config = await store.EnsureExistsAsync(CancellationToken.None);

        Assert.True(File.Exists(configPath));
        Assert.Equal("github-copilot", config.Llm.Provider);
        var text = await File.ReadAllTextAsync(configPath, CancellationToken.None);
        Assert.Contains("OPENAI_API_KEY", text, StringComparison.Ordinal);
        Assert.Contains("AZURE_OPENAI_API_KEY", text, StringComparison.Ordinal);
        Assert.Contains("autoDownload", text, StringComparison.Ordinal);
        Assert.Contains("portableDirectory", text, StringComparison.Ordinal);
        Assert.Contains("modelDirectory", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigStoreSupportsPortableDependencySettings()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));
        var portableDirectory = temp.GetPath("portable-tools");
        var modelDirectory = temp.GetPath("models", "mini-lm");

        await store.SetAsync("dependencies.autoDownload", "true", CancellationToken.None);
        await store.SetAsync("dependencies.portableDirectory", portableDirectory, CancellationToken.None);
        await store.SetAsync("search.onnx.autoDownload", "yes", CancellationToken.None);
        await store.SetAsync("search.onnx.modelDirectory", modelDirectory, CancellationToken.None);
        await store.SetAsync("search.onnx.modelFile", "model.onnx", CancellationToken.None);

        Assert.Equal("True", await store.GetAsync("dependencies.autoDownload", CancellationToken.None));
        Assert.Equal(portableDirectory, await store.GetAsync("dependencies.portableDirectory", CancellationToken.None));
        Assert.Equal("True", await store.GetAsync("search.onnx.autoDownload", CancellationToken.None));
        Assert.Equal(modelDirectory, await store.GetAsync("search.onnx.modelDirectory", CancellationToken.None));
        Assert.Equal("model.onnx", await store.GetAsync("search.onnx.modelFile", CancellationToken.None));
    }

    [Fact]
    public void DependencyResolverPrefersEnvironmentPathOverConfigPath()
    {
        using var temp = new TestTempDirectory();
        var envPath = temp.GetPath("env", "yt-dlp.exe");
        var configPath = temp.GetPath("config", "yt-dlp.exe");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(envPath)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configPath)!);
        File.WriteAllText(envPath, string.Empty);
        File.WriteAllText(configPath, string.Empty);
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH");

        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", envPath);
            var resolver = new DependencyResolver(new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { YtDlpPath = configPath }
            });

            var status = resolver.GetYtDlpStatus();

            Assert.True(status.IsFound);
            Assert.Equal(envPath, status.Path);
            Assert.Equal("ZAKIRA_REPLAY_YTDLP_PATH", status.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", previous);
        }
    }

    [Fact]
    public void DependencyResolverFindsInstalledPortableExecutableBeforePath()
    {
        using var temp = new TestTempDirectory();
        var portableDirectory = temp.GetPath("portable");
        Directory.CreateDirectory(portableDirectory);
        var executablePath = System.IO.Path.Combine(portableDirectory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        File.WriteAllText(executablePath, string.Empty);
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", null);
            var resolver = new DependencyResolver(new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { PortableDirectory = portableDirectory }
            });

            var status = resolver.GetYtDlpStatus();

            Assert.True(status.IsFound);
            Assert.Equal(executablePath, status.Path);
            Assert.Equal("portable", status.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", previous);
        }
    }

    [Fact]
    public void DependencyResolverDoesNotAutoDownloadByDefault()
    {
        using var temp = new TestTempDirectory();
        var previous = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", temp.GetPath("missing-yt-dlp"));
            var resolver = new DependencyResolver(new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { PortableDirectory = temp.GetPath("portable") }
            });

            var ex = Assert.Throws<MissingDependencyException>(() => resolver.RequireYtDlp("test"));

            Assert.Equal("yt-dlp", ex.Dependency);
            Assert.False(Directory.Exists(temp.GetPath("portable")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_YTDLP_PATH", previous);
        }
    }

    [Fact]
    public void PortableDependencyInstallerNormalizesTargetsWithoutDuplicates()
    {
        var targets = PortableDependencyInstaller.NormalizeTargets(["ffprobe", "ffmpeg", "onnx"]);

        Assert.Equal([PortableDependencyInstaller.Ffmpeg, PortableDependencyInstaller.Onnx], targets);
    }

    [Fact]
    public void PortableDependencyInstallerUsesConfiguredPaths()
    {
        using var temp = new TestTempDirectory();
        var previousPortableDirectory = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY");
        var previousOnnxModelDirectory = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY", null);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY", null);
            var config = new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { PortableDirectory = temp.GetPath("tools") },
                Search = new SearchConfig { Onnx = new OnnxEmbeddingConfig { ModelDirectory = temp.GetPath("models") } }
            };

            var installer = new PortableDependencyInstaller(config);

            Assert.Equal(temp.GetPath("tools"), installer.Layout.PortableDirectory);
            Assert.Equal(temp.GetPath("models"), installer.Layout.OnnxModelDirectory);
            Assert.Equal(System.IO.Path.Combine(temp.GetPath("models"), "model.onnx"), installer.GetOnnxModelPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY", previousPortableDirectory);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY", previousOnnxModelDirectory);
        }
    }

    [Fact]
    public async Task PortableDependencyInstallerDownloadsOnnxFilesWithFakeHttp()
    {
        using var temp = new TestTempDirectory();
        var previousOnnxModelDirectory = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY");
        var previousOnnxModelFile = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_FILE");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY", null);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_FILE", null);
            using var handler = new RecordingHttpMessageHandler(_ => new ByteArrayContent([1, 2, 3]));
            var config = new ReplayConfig
            {
                Search = new SearchConfig { Onnx = new OnnxEmbeddingConfig { ModelDirectory = temp.GetPath("models"), ModelFile = "model.onnx" } }
            };
            var installer = new PortableDependencyInstaller(config, new HttpClient(handler, disposeHandler: false));

            var result = await installer.InstallAsync([PortableDependencyInstaller.Onnx], force: false, progress: null, CancellationToken.None);

            Assert.Single(result.Items);
            Assert.True(File.Exists(System.IO.Path.Combine(temp.GetPath("models"), "model.onnx")));
            Assert.True(File.Exists(System.IO.Path.Combine(temp.GetPath("models"), "vocab.txt")));
            Assert.Equal(4, handler.Requests.Count);
            Assert.Contains(handler.Requests, request => request.RequestUri!.ToString().Contains("/onnx/model.onnx", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY", previousOnnxModelDirectory);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_FILE", previousOnnxModelFile);
        }
    }

    [Fact]
    public async Task PortableDependencyInstallerExtractsFfmpegArchiveWithFakeHttpOnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64);

        using var temp = new TestTempDirectory();
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var ffmpeg = archive.CreateEntry("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe");
            await using (var stream = ffmpeg.Open())
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
            }

            var ffprobe = archive.CreateEntry("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe");
            await using (var stream = ffprobe.Open())
            {
                await stream.WriteAsync(new byte[] { 4, 5, 6 }, CancellationToken.None);
            }
        }

        var previousPortableDirectory = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY", null);
            using var handler = new RecordingHttpMessageHandler(_ => new ByteArrayContent(archiveStream.ToArray()));
            var installer = new PortableDependencyInstaller(new ReplayConfig
            {
                Dependencies = new DependencyPathConfig { PortableDirectory = temp.GetPath("tools") }
            }, new HttpClient(handler, disposeHandler: false));

            var result = await installer.InstallAsync([PortableDependencyInstaller.Ffmpeg], force: false, progress: null, CancellationToken.None);

            Assert.Equal(2, result.Items.Count);
            Assert.True(File.Exists(System.IO.Path.Combine(temp.GetPath("tools"), "ffmpeg.exe")));
            Assert.True(File.Exists(System.IO.Path.Combine(temp.GetPath("tools"), "ffprobe.exe")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY", previousPortableDirectory);
        }
    }

    [Fact]
    public async Task DepsPathCommandReportsConfiguredPortablePaths()
    {
        using var temp = new TestTempDirectory();
        var previousConfigPath = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", temp.GetPath("Zakira.Replay.json"));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await CliApp.RunAsync(["deps", "path"], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("Portable directory:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("yt-dlp:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("ONNX model:", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousConfigPath);
        }
    }

    [Fact]
    public void SourceLocatorRejectsMissingLocalFileSources()
    {
        var ex = Assert.Throws<ReplayException>(() => SourceLocator.ThrowIfMissingLocalPathLikeSource("missing-video.mp4"));

        Assert.Contains("Source file does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtitleConverterCreatesDeduplicatedMarkdownTranscript()
    {
        using var temp = new TestTempDirectory();
        var subtitlePath = temp.GetPath("subtitle.vtt");
        await File.WriteAllTextAsync(subtitlePath, """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            <c>Hello</c> &amp; welcome

            00:00:01.000 --> 00:00:02.000
            <c>Hello</c> &amp; welcome

            00:00:03.000 --> 00:00:04.000
            Next line
            """.Replace("\r\n", "\n", StringComparison.Ordinal), CancellationToken.None);

        var markdown = await SubtitleConverter.ToMarkdownAsync(subtitlePath, CancellationToken.None);

        Assert.Contains("**[00:00:01.000 - 00:00:02.000]** Hello & welcome", markdown, StringComparison.Ordinal);
        Assert.Contains("**[00:00:03.000 - 00:00:04.000]** Next line", markdown, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(markdown, "Hello & welcome"));
    }

    [Fact]
    public async Task DownloadBestSubtitleAsyncReturnsArtifactWhenYtDlpExitsNonZeroButSubtitleFileWasWritten()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://example.test/video", "non-zero-but-file");

        var captionsDirectory = Path.Combine(run.Directory, "captions");
        var sampleVtt = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello world\n";

        var fakeRunner = new FakeProcessRunner((fileName, args) =>
        {
            // Mirror real yt-dlp behavior: even when the exit code is non-zero (e.g., one of the
            // requested languages was unavailable), available languages still get written to disk.
            var subtitlePath = Path.Combine(captionsDirectory, "subtitle.en.vtt");
            File.WriteAllText(subtitlePath, sampleVtt);
            return new ProcessResult(fileName, args, ExitCode: 1, StandardOutput: "", StandardError: "warning: requested format unavailable");
        });

        var fakeYtDlp = temp.GetPath("fake-yt-dlp.exe");
        await File.WriteAllBytesAsync(fakeYtDlp, [], CancellationToken.None);
        var config = new ReplayConfig { Dependencies = new DependencyPathConfig { YtDlpPath = fakeYtDlp } };
        var dependencies = new DependencyResolver(config);
        var client = new YtDlpClient(dependencies, fakeRunner);

        var request = new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "",
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "non-zero-but-file");

        var artifact = await client.DownloadBestSubtitleAsync(request, run, ["en"], CancellationToken.None);

        Assert.NotNull(artifact);
        Assert.Equal("yt-dlp-subtitle", artifact!.Kind);
        Assert.True(File.Exists(artifact.SourcePath));
        Assert.EndsWith("subtitle.en.vtt", artifact.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(artifact.MarkdownPath));
        Assert.NotNull(artifact.Segments);
        Assert.NotEmpty(artifact.Segments!);
    }

    [Fact]
    public async Task DownloadBestSubtitleAsyncReturnsNullWhenYtDlpExitsNonZeroAndNoFileWritten()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("https://example.test/video", "non-zero-no-file");

        var fakeRunner = new FakeProcessRunner((fileName, args) =>
        {
            // No subtitle file is written: the captions directory exists but stays empty.
            return new ProcessResult(fileName, args, ExitCode: 1, StandardOutput: "", StandardError: "ERROR: no subtitles available");
        });

        var fakeYtDlp = temp.GetPath("fake-yt-dlp.exe");
        await File.WriteAllBytesAsync(fakeYtDlp, [], CancellationToken.None);
        var config = new ReplayConfig { Dependencies = new DependencyPathConfig { YtDlpPath = fakeYtDlp } };
        var dependencies = new DependencyResolver(config);
        var client = new YtDlpClient(dependencies, fakeRunner);

        var request = new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "",
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "non-zero-no-file");

        var artifact = await client.DownloadBestSubtitleAsync(request, run, ["en"], CancellationToken.None);

        Assert.Null(artifact);
    }

    [Fact]
    public void TranscriptNormalizerCollapsesExactRapidDuplicates()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "So, GLET have rolled out this, the"),
            new TranscriptSegment(2, null, "00:02", "So, GLET have rolled out this, the"),
            new TranscriptSegment(20, null, "00:20", "A separate later sentence remains.")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("So, GLET have rolled out this, the", normalized[0].Text);
        Assert.Equal(0, normalized[0].StartSeconds);
    }

    [Fact]
    public void TranscriptNormalizerKeepsGrowingCaptionInsteadOfFragments()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "So, GLET have rolled out this, the"),
            new TranscriptSegment(2, null, "00:02", "So, GLET have rolled out this, the Barrel 7, their new travel router. And"),
            new TranscriptSegment(3.5, null, "00:03.500", "Barrel 7, their new travel router. And")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal("So, GLET have rolled out this, the Barrel 7, their new travel router. And", segment.Text);
        Assert.DoesNotContain("So, GLET have rolled out this, the So, GLET", segment.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptNormalizerMergesOverlappingContinuationLines()
    {
        var segments = new[]
        {
            new TranscriptSegment(10, null, "00:10", "This device here supports Wi-Fi"),
            new TranscriptSegment(12, null, "00:12", "supports Wi-Fi 6 and this device here supports Wi-Fi 7"),
            new TranscriptSegment(30, null, "00:30", "A new topic starts after the duplicate window.")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("This device here supports Wi-Fi 6 and this device here supports Wi-Fi 7", normalized[0].Text);
    }

    [Fact]
    public void TranscriptNormalizerDoesNotMergeDifferentNearbyShortPhrases()
    {
        var segments = new[]
        {
            new TranscriptSegment(10, null, "00:10", "Router setup starts now"),
            new TranscriptSegment(12, null, "00:12", "Travel bag review begins")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void TranscriptNormalizerMarkdownUsesCleanedSegments()
    {
        var segments = new[]
        {
            new TranscriptSegment(1, null, "00:01", "Hello     world"),
            new TranscriptSegment(2, null, "00:02", "Hello world again")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);
        var markdown = TranscriptNormalizer.ToMarkdown(normalized);

        Assert.Contains("**[00:01 - 00:02]** Hello world again", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello     world", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptNormalizerPreservesUniqueTokensAcrossMerges()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "alpha beta gamma"),
            new TranscriptSegment(2, null, "00:02", "beta gamma delta epsilon"),
            new TranscriptSegment(4, null, "00:04", "epsilon zeta"),
            new TranscriptSegment(12, null, "00:12", "eta theta iota")
        };

        var sourceTokens = UniqueWords(string.Join(' ', segments.Select(segment => segment.Text)));
        var normalized = TranscriptNormalizer.Normalize(segments);
        var normalizedTokens = UniqueWords(string.Join(' ', normalized.Select(segment => segment.Text)));

        Assert.True(sourceTokens.IsSubsetOf(normalizedTokens), $"Missing tokens: {string.Join(", ", sourceTokens.Except(normalizedTokens))}");
    }

    [Fact]
    public void TranscriptNormalizerKeepsLegitimateRepeatedPhraseAcrossTimeGap()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "Remember to back up your data"),
            new TranscriptSegment(3, null, "00:03", "Remember to back up your data"),
            new TranscriptSegment(25, null, "00:25", "Remember to back up your data")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(3, normalized.Count);
        Assert.Equal([0, 3, 25], normalized.Select(segment => (int)segment.StartSeconds!).ToArray());
    }

    [Fact]
    public void TranscriptNormalizerMergesDuplicatesAtTwoSecondBoundary()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "Router setup begins now"),
            new TranscriptSegment(2, null, "00:02", "Router setup begins now")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Single(normalized);
    }

    [Fact]
    public void TranscriptNormalizerRecomputesMergedTimestampRange()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 1, "00:00 - 00:01", "Router setup begins"),
            new TranscriptSegment(1, 3, "00:01 - 00:03", "Router setup begins with VPN")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal(0, segment.StartSeconds);
        Assert.Equal(3, segment.EndSeconds);
        Assert.Equal("00:00 - 00:03", segment.Timestamp);
    }

    [Fact]
    public void TranscriptNormalizerDoesNotMergeDuplicatesAfterTwoSecondBoundary()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, null, "00:00", "Router setup begins now"),
            new TranscriptSegment(2.1, null, "00:02.100", "Router setup begins now")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void TranscriptNormalizerPreservesPrefixWhenCurrentContainsPreviousInMiddle()
    {
        var segments = new[]
        {
            new TranscriptSegment(10, null, "00:10", "The device supports Wi-Fi"),
            new TranscriptSegment(12, null, "00:12", "supports Wi-Fi 7 and Ethernet")
        };

        var normalized = TranscriptNormalizer.Normalize(segments);

        var segment = Assert.Single(normalized);
        Assert.Equal("The device supports Wi-Fi 7 and Ethernet", segment.Text);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("01:30", 90)]
    [InlineData("00:01:30", 90)]
    public void TimestampParseRequiredAcceptsCommonTimestampFormats(string value, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Timestamp.ParseRequired(value, "test"));
    }

    [Fact]
    public void FfmpegSilenceParserReadsStartEndPairs()
    {
        const string stderr = """
            [silencedetect @ 0x55a] silence_start: 1.234
            [silencedetect @ 0x55a] silence_end: 2.789 | silence_duration: 1.555
            [silencedetect @ 0x55a] silence_start: 12.5
            [silencedetect @ 0x55a] silence_end: 13.0 | silence_duration: 0.5
            """;

        var windows = FfmpegClient.ParseSilenceWindows(stderr);

        Assert.Equal(2, windows.Count);
        Assert.Equal(1.234, windows[0].StartSeconds);
        Assert.Equal(2.789, windows[0].EndSeconds);
        Assert.Equal(1.555, windows[0].DurationSeconds, precision: 4);
        Assert.Equal(0.5, windows[1].DurationSeconds, precision: 4);
    }

    [Fact]
    public void FfmpegSilenceParserDiscardsOrphanStartsWithoutEnd()
    {
        const string stderr = """
            [silencedetect @ 0x55a] silence_start: 1.0
            [silencedetect @ 0x55a] silence_start: 2.0
            [silencedetect @ 0x55a] silence_end: 3.0 | silence_duration: 1.0
            """;

        var windows = FfmpegClient.ParseSilenceWindows(stderr);

        var window = Assert.Single(windows);
        Assert.Equal(2.0, window.StartSeconds);
        Assert.Equal(3.0, window.EndSeconds);
    }

    [Fact]
    public void AudioChunkerComputeSplitPointsSnapsToSilenceCenters()
    {
        var silence = new[]
        {
            new SilenceWindow(595, 600, 5),
            new SilenceWindow(1190, 1195, 5),
            new SilenceWindow(1800, 1801, 1)
        };
        var options = new AudioChunkingOptions(TargetChunkDurationSeconds: 600, MinChunkDurationSeconds: 60, MaxChunkDurationSeconds: 750, OverflowToleranceSeconds: 30);

        var points = AudioChunker.ComputeSplitPoints(totalDuration: 1800, silenceWindows: silence, options).ToArray();

        Assert.Equal(0, points[0]);
        Assert.Equal(597.5, points[1], precision: 1);
        Assert.Equal(1192.5, points[2], precision: 1);
        Assert.Equal(1800, points[^1]);
    }

    [Fact]
    public void AudioChunkerComputeSplitPointsFallsBackToHardCutWhenNoSilence()
    {
        var options = new AudioChunkingOptions(TargetChunkDurationSeconds: 300, MinChunkDurationSeconds: 60, MaxChunkDurationSeconds: 360, OverflowToleranceSeconds: 0);

        var points = AudioChunker.ComputeSplitPoints(totalDuration: 720, silenceWindows: [], options).ToArray();

        Assert.Equal([0d, 300d, 600d, 720d], points);
    }

    [Fact]
    public async Task AudioChunkerReturnsSingleChunkForShortAudio()
    {
        var ffmpeg = new SilenceFakeFfmpegClient { Duration = 30 };
        var chunker = new AudioChunker(ffmpeg);
        using var temp = new TestTempDirectory();
        var run = new VideoRun("r", temp.Path);

        var result = await chunker.ChunkAsync(temp.GetPath("audio.wav"), run, new AudioChunkingOptions(), CancellationToken.None);

        Assert.Single(result.Chunks);
        Assert.Equal("chunk-001", result.Chunks[0].Id);
        Assert.Equal(0, ffmpeg.SilenceCalls);
        Assert.Equal(0, ffmpeg.RangeCalls);
    }

    [Fact]
    public async Task AudioChunkerSplitsLongAudioAtSilenceBoundaries()
    {
        var ffmpeg = new SilenceFakeFfmpegClient
        {
            Duration = 1800,
            SilenceWindows =
            [
                new SilenceWindow(595, 600, 5),
                new SilenceWindow(1190, 1195, 5)
            ]
        };
        var chunker = new AudioChunker(ffmpeg);
        using var temp = new TestTempDirectory();
        var run = new VideoRun("r", temp.GetPath("run"));
        Directory.CreateDirectory(run.Directory);

        var result = await chunker.ChunkAsync(temp.GetPath("audio.wav"), run, new AudioChunkingOptions(), CancellationToken.None);

        Assert.Equal(3, result.Chunks.Count);
        Assert.Equal("chunk-001", result.Chunks[0].Id);
        Assert.Equal("chunk-002", result.Chunks[1].Id);
        Assert.Equal("chunk-003", result.Chunks[2].Id);
        Assert.Equal(0, result.Chunks[0].StartSeconds);
        Assert.True(result.Chunks[1].StartSeconds is > 590 and < 605);
        Assert.True(result.Chunks[2].StartSeconds is > 1185 and < 1200);
        Assert.Equal(3, ffmpeg.RangeCalls);
        Assert.Equal(1, ffmpeg.SilenceCalls);
    }

    [Fact]
    public async Task ChunkedTranscriptionServicePassesThroughSingleChunkUnchanged()
    {
        var ffmpeg = new SilenceFakeFfmpegClient { Duration = 30 };
        var transcriber = new RecordingTranscriptionProvider("**[00:00 - 00:30]** hello world");
        var service = new ChunkedTranscriptionService(transcriber, new AudioChunker(ffmpeg));
        using var temp = new TestTempDirectory();
        var run = new VideoRun("r", temp.GetPath("run"));
        Directory.CreateDirectory(run.Directory);

        var result = await service.TranscribeAsync(temp.GetPath("audio.wav"), run, options: null, progress: null, CancellationToken.None);

        Assert.Single(result.Chunks.Chunks);
        Assert.Equal("**[00:00 - 00:30]** hello world", result.MarkdownTranscript);
        Assert.Single(transcriber.Calls);
    }

    [Fact]
    public async Task ChunkedTranscriptionServiceShiftsTimestampsAcrossChunks()
    {
        var ffmpeg = new SilenceFakeFfmpegClient
        {
            Duration = 1800,
            SilenceWindows =
            [
                new SilenceWindow(595, 600, 5),
                new SilenceWindow(1190, 1195, 5)
            ]
        };
        var perChunkResponses = new Queue<string>([
            "**[00:00:05 - 00:00:10]** chunk one phrase",
            "**[00:00:02 - 00:00:08]** chunk two phrase",
            "flat prose without timestamp"
        ]);
        var transcriber = new RecordingTranscriptionProvider(_ => perChunkResponses.Dequeue());
        var service = new ChunkedTranscriptionService(transcriber, new AudioChunker(ffmpeg));
        using var temp = new TestTempDirectory();
        var run = new VideoRun("r", temp.GetPath("run"));
        Directory.CreateDirectory(run.Directory);

        var result = await service.TranscribeAsync(temp.GetPath("audio.wav"), run, options: null, progress: null, CancellationToken.None);

        Assert.Equal(3, transcriber.Calls.Count);
        // First chunk timestamp 00:00:05 - 00:00:10 has zero shift; reformatted as MM:SS by Timestamp.Format
        Assert.Contains("**[00:05 - 00:10]** chunk one phrase", result.MarkdownTranscript, StringComparison.Ordinal);
        // Chunk 2 starts ~597.5s; segment "00:00:02 - 00:00:08" shifted by ~597.5s lands around 09:59 - 10:05
        Assert.Contains("chunk two phrase", result.MarkdownTranscript, StringComparison.Ordinal);
        Assert.Matches(@"\*\*\[\d{2}:\d{2}(?::\d{2})?(?:\.\d+)? - \d{2}:\d{2}(?::\d{2})?(?:\.\d+)?\]\*\* chunk two phrase", result.MarkdownTranscript);
        // Flat-prose chunk 3 should get a chunk-level timestamp wrapper
        Assert.Contains("flat prose without timestamp", result.MarkdownTranscript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedTranscriptionServiceRecordsPerChunkFailureAsWarning()
    {
        var ffmpeg = new SilenceFakeFfmpegClient
        {
            Duration = 1800,
            SilenceWindows =
            [
                new SilenceWindow(595, 600, 5),
                new SilenceWindow(1190, 1195, 5)
            ]
        };
        var attempts = 0;
        var transcriber = new RecordingTranscriptionProvider(_ =>
        {
            attempts++;
            if (attempts == 2)
            {
                throw new ReplayException("simulated network failure");
            }

            return $"chunk {attempts} content";
        });
        var service = new ChunkedTranscriptionService(transcriber, new AudioChunker(ffmpeg));
        using var temp = new TestTempDirectory();
        var run = new VideoRun("r", temp.GetPath("run"));
        Directory.CreateDirectory(run.Directory);

        var result = await service.TranscribeAsync(temp.GetPath("audio.wav"), run, options: null, progress: null, CancellationToken.None);

        Assert.Equal(3, transcriber.Calls.Count);
        var warning = Assert.Single(result.ChunkedTranscriptionWarnings);
        Assert.Equal(ReplayWarningCodes.SttChunkFailed, warning.Code);
        Assert.Equal(ReplayWarningSeverities.Error, warning.Severity);
        Assert.Contains("chunk 1 content", result.MarkdownTranscript, StringComparison.Ordinal);
        Assert.Contains("chunk 3 content", result.MarkdownTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("simulated network failure", result.MarkdownTranscript, StringComparison.Ordinal);
    }

    private sealed class SilenceFakeFfmpegClient : IFfmpegClient
    {
        public double Duration { get; init; }

        public IReadOnlyList<SilenceWindow> SilenceWindows { get; init; } = [];

        public int SilenceCalls { get; private set; }

        public int RangeCalls { get; private set; }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => Task.FromResult("audio/audio.wav");

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => Task.FromResult("clips/clip.mp4");

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => Task.FromResult<double?>(Duration);

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
        {
            SilenceCalls++;
            return Task.FromResult(SilenceWindows);
        }

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
        {
            RangeCalls++;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, $"chunk start={start.TotalSeconds:F3} duration={duration.TotalSeconds:F3}");
            return Task.CompletedTask;
        }

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>("0000000000000000");
        }
    }

    private sealed class RecordingTranscriptionProvider : ITranscriptionProvider
    {
        private readonly Func<string, string> responder;

        public RecordingTranscriptionProvider(string fixedResponse)
            : this(_ => fixedResponse)
        {
        }

        public RecordingTranscriptionProvider(Func<string, string> responder)
        {
            this.responder = responder;
        }

        public List<string> Calls { get; } = [];

        public Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
        {
            Calls.Add(audioPath);
            return Task.FromResult(responder(audioPath));
        }
    }

    [Fact]
    public void FfmpegDifferenceHashFromKnownByteBuffer()
    {
        // 9x8 grayscale: ascending row of 9 pixels, all rows identical.
        var bytes = new byte[72];
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 9; column++)
            {
                bytes[(row * 9) + column] = (byte)(column * 30);
            }
        }

        var hash = FfmpegClient.ComputeDifferenceHash(bytes);

        // For each row left < right (ascending) so all 64 bits should be 0.
        Assert.Equal("0000000000000000", hash);
    }

    [Fact]
    public void FfmpegDifferenceHashReturnsNullForShortBuffer()
    {
        var hash = FfmpegClient.ComputeDifferenceHash(new byte[10]);
        Assert.Null(hash);
    }

    [Fact]
    public void SlideGrouperCollapsesNearDuplicateHashes()
    {
        var frames = new[]
        {
            new FrameArtifact("frame-001", "frames/f1.jpg", 1, "00:01", "0000000000000000"),
            new FrameArtifact("frame-002", "frames/f2.jpg", 2, "00:02", "0000000000000003"), // 2 bits different
            new FrameArtifact("frame-003", "frames/f3.jpg", 3, "00:03", "ffffffffffffffff"), // ~64 bits different
            new FrameArtifact("frame-004", "frames/f4.jpg", 4, "00:04", "fffffffffffffffe")  // 1 bit different from 003
        };

        var slides = SlideGrouper.Group(frames, new SlideGroupingOptions(Enabled: true, HashDistance: 6));

        Assert.Equal(2, slides.Count);
        Assert.Equal(["frame-001", "frame-002"], slides[0].FrameIds);
        Assert.Equal("slide-001", slides[0].Id);
        Assert.Equal("frame-001", slides[0].PrimaryFrameId);
        Assert.Equal(["frame-003", "frame-004"], slides[1].FrameIds);
        Assert.Equal("frame-003", slides[1].PrimaryFrameId);
    }

    [Fact]
    public void SlideGrouperEmitsOneSlidePerFrameWhenDisabled()
    {
        var frames = new[]
        {
            new FrameArtifact("frame-001", "frames/f1.jpg", 1, "00:01", "0000000000000000"),
            new FrameArtifact("frame-002", "frames/f2.jpg", 2, "00:02", "0000000000000000"),
            new FrameArtifact("frame-003", "frames/f3.jpg", 3, "00:03", "0000000000000000")
        };

        var slides = SlideGrouper.Group(frames, new SlideGroupingOptions(Enabled: false));

        Assert.Equal(3, slides.Count);
        Assert.Equal(["slide-001", "slide-002", "slide-003"], slides.Select(slide => slide.Id).ToArray());
        Assert.All(slides, slide => Assert.Single(slide.FrameIds));
    }

    [Fact]
    public void SlideGrouperStartsNewSlideWhenHashIsMissing()
    {
        var frames = new[]
        {
            new FrameArtifact("frame-001", "frames/f1.jpg", 1, "00:01", "0000000000000000"),
            new FrameArtifact("frame-002", "frames/f2.jpg", 2, "00:02", null), // missing hash forces split
            new FrameArtifact("frame-003", "frames/f3.jpg", 3, "00:03", "0000000000000000")
        };

        var slides = SlideGrouper.Group(frames, new SlideGroupingOptions(Enabled: true, HashDistance: 6));

        Assert.Equal(3, slides.Count);
    }

    [Fact]
    public void StructuredResponseParserParsesWellFormedOcrJson()
    {
        const string raw = """
            {
              "freeText": "WireGuard 7.4 Gbps",
              "lines": ["WireGuard", "7.4 Gbps"],
              "tables": [{"headers": ["Metric","Value"], "rows": [["Throughput","7.4 Gbps"]]}]
            }
            """;

        var parsed = StructuredResponseParser.ParseOcr(raw);

        Assert.False(StructuredResponseParser.IsTolerantFallback(parsed));
        Assert.Equal("WireGuard 7.4 Gbps", parsed.FreeText);
        Assert.Equal(["WireGuard", "7.4 Gbps"], parsed.Lines);
        var table = Assert.Single(parsed.Tables);
        Assert.Equal(["Metric", "Value"], table.Headers);
        Assert.Equal(["Throughput", "7.4 Gbps"], table.Rows[0]);
    }

    [Fact]
    public void StructuredResponseParserStripsMarkdownFenceBeforeParsing()
    {
        const string raw = """
            ```json
            {"freeText": "Slide title", "lines": ["Slide title"], "tables": []}
            ```
            """;

        var parsed = StructuredResponseParser.ParseOcr(raw);

        Assert.Equal("Slide title", parsed.FreeText);
        Assert.Equal(["Slide title"], parsed.Lines);
    }

    [Fact]
    public void StructuredResponseParserFallsBackOnNonJsonOcrResponse()
    {
        const string raw = "Just a sentence with no JSON object.";

        var parsed = StructuredResponseParser.ParseOcr(raw);

        Assert.True(StructuredResponseParser.IsTolerantFallback(parsed));
        Assert.Equal(raw, parsed.FreeText);
        Assert.Empty(parsed.Lines);
        Assert.Empty(parsed.Tables);
    }

    [Fact]
    public void StructuredResponseParserParsesWellFormedVisionJson()
    {
        const string raw = """
            {
              "kind": "slide",
              "title": "Throughput Comparison",
              "bullets": ["WireGuard 7.4 Gbps", "OpenVPN 1.1 Gbps"],
              "codeBlocks": [{"language": "csharp", "text": "var x = 1;"}],
              "charts": [{"title": "Throughput", "axes": ["Protocol", "Gbps"], "series": ["Average"]}],
              "uiElements": ["button: Compare"],
              "freeText": "Slide compares throughput across VPN protocols."
            }
            """;

        var parsed = StructuredResponseParser.ParseVision(raw);

        Assert.False(StructuredResponseParser.IsTolerantFallback(parsed));
        Assert.Equal("slide", parsed.Kind);
        Assert.Equal("Throughput Comparison", parsed.Title);
        Assert.Equal(2, parsed.Bullets.Count);
        var code = Assert.Single(parsed.CodeBlocks);
        Assert.Equal("csharp", code.Language);
        var chart = Assert.Single(parsed.Charts);
        Assert.Equal("Throughput", chart.Title);
    }

    [Fact]
    public void StructuredResponseParserFallsBackOnNonJsonVisionResponse()
    {
        const string raw = "The slide describes throughput numbers.";

        var parsed = StructuredResponseParser.ParseVision(raw);

        Assert.True(StructuredResponseParser.IsTolerantFallback(parsed));
        Assert.Equal("other", parsed.Kind);
        Assert.Equal(raw, parsed.FreeText);
    }

    [Fact]
    public void StructuredResponseParserNormalisesUnknownVisionKindToOther()
    {
        const string raw = """
            {"kind": "weird", "freeText": "abc", "bullets": [], "codeBlocks": [], "charts": [], "uiElements": []}
            """;

        var parsed = StructuredResponseParser.ParseVision(raw);

        Assert.Equal("other", parsed.Kind);
    }

    // --- Empty-but-valid OCR results -----------------------------------------------------------
    // RapidOCR (and any local provider) produces a perfectly valid empty result when a frame
    // contains no readable text: `{"freeText": "", "lines": [], "tables": []}`. The parser
    // should NOT flag this as a fallback, and the new precise-mode entrypoint must report
    // IsFallback=false even when the structured value is fully empty.

    [Fact]
    public void ParseOcrWithModeReportsNotFallbackForGenuinelyEmptyValidJson()
    {
        const string raw = """{"freeText": "", "lines": [], "tables": []}""";

        var result = StructuredResponseParser.ParseOcrWithMode(raw);

        Assert.False(result.IsFallback, "an empty-but-parsed JSON result must not be flagged as a fallback");
        Assert.Equal(string.Empty, result.Structured.FreeText);
        Assert.Empty(result.Structured.Lines);
        Assert.Empty(result.Structured.Tables);
    }

    [Fact]
    public void IsTolerantFallbackHeuristicAlsoTreatsGenuinelyEmptyResultAsNotFallback()
    {
        // Disk-readers without access to the raw response use IsTolerantFallback as their
        // heuristic. The fix tightens the heuristic so an empty FreeText + empty arrays is
        // recognised as "successfully empty" rather than "fell back".
        var emptyValid = new OcrFrameStructured(string.Empty, [], []);
        var proseFallback = new OcrFrameStructured("Some prose the LLM emitted", [], []);

        Assert.False(StructuredResponseParser.IsTolerantFallback(emptyValid));
        Assert.True(StructuredResponseParser.IsTolerantFallback(proseFallback));
    }

    [Fact]
    public void ParseOcrWithModeReportsFallbackForProseResponse()
    {
        const string raw = "Just a sentence with no JSON object.";

        var result = StructuredResponseParser.ParseOcrWithMode(raw);

        Assert.True(result.IsFallback);
        Assert.Equal(raw, result.Structured.FreeText);
        Assert.Empty(result.Structured.Lines);
    }

    [Fact]
    public void ParseOcrWithModeReportsFallbackForMalformedJson()
    {
        const string raw = "{not actually json}";

        var result = StructuredResponseParser.ParseOcrWithMode(raw);

        Assert.True(result.IsFallback);
    }

    [Fact]
    public void ParseOcrWithModeReportsNotFallbackForLinesOnlyResult()
    {
        const string raw = """{"freeText": "Hello", "lines": ["Hello"], "tables": []}""";

        var result = StructuredResponseParser.ParseOcrWithMode(raw);

        Assert.False(result.IsFallback);
        Assert.Equal(["Hello"], result.Structured.Lines);
    }

    [Fact]
    public void ParseVisionWithModeReportsNotFallbackForGenuinelyEmptyValidJson()
    {
        const string raw = """{"kind": "slide", "title": null, "bullets": [], "codeBlocks": [], "charts": [], "uiElements": [], "freeText": ""}""";

        var result = StructuredResponseParser.ParseVisionWithMode(raw);

        Assert.False(result.IsFallback, "an empty-but-parsed JSON result must not be flagged as a fallback");
        Assert.Equal("slide", result.Structured.Kind);
        Assert.Equal(string.Empty, result.Structured.FreeText);
    }

    [Fact]
    public void ParseVisionWithModeReportsFallbackForProseResponse()
    {
        const string raw = "The slide describes throughput numbers.";

        var result = StructuredResponseParser.ParseVisionWithMode(raw);

        Assert.True(result.IsFallback);
        Assert.Equal("other", result.Structured.Kind);
    }

    [Fact]
    public void IsTolerantFallbackVisionAlsoTreatsGenuinelyEmptyResultAsNotFallback()
    {
        // Empty vision result with no FreeText is not a fallback; "other" + non-empty FreeText is.
        var emptyValid = new VisionFrameStructured("other", null, [], [], [], [], string.Empty);
        var proseFallback = new VisionFrameStructured("other", null, [], [], [], [], "Some LLM prose.");

        Assert.False(StructuredResponseParser.IsTolerantFallback(emptyValid));
        Assert.True(StructuredResponseParser.IsTolerantFallback(proseFallback));
    }

    [Fact]
    public async Task ConfigStoreSupportsSlideSettings()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("slides.enabled", "false", CancellationToken.None);
        await store.SetAsync("slides.hashDistance", "10", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.False(config.Slides.Enabled);
        Assert.Equal(10, config.Slides.HashDistance);
        Assert.Equal("False", await store.GetAsync("slides.enabled", CancellationToken.None));
        Assert.Equal("10", await store.GetAsync("slides.hashDistance", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigStoreRejectsInvalidSlideHashDistance()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("slides.hashDistance", "65", CancellationToken.None));
        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("slides.hashDistance", "-1", CancellationToken.None));
    }

    [Fact]
    public async Task CopilotVisionProviderOmitsFocusLineWhenInstructionIsEmpty()
    {
        var llm = new CapturingLlmProvider();
        var provider = new CopilotVisionProvider(llm, "test-model");

        await provider.DescribeAsync("frame.jpg", string.Empty, CancellationToken.None);

        var prompt = Assert.Single(llm.Captured).Prompt;
        Assert.DoesNotContain("Additional focus from the orchestrator", prompt, StringComparison.Ordinal);
        Assert.Contains("Extract every distinct piece of visible content", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotVisionProviderIncludesFocusLineWhenInstructionIsProvided()
    {
        var llm = new CapturingLlmProvider();
        var provider = new CopilotVisionProvider(llm, "test-model");

        await provider.DescribeAsync("frame.jpg", "Focus on slide titles and code blocks.", CancellationToken.None);

        var prompt = Assert.Single(llm.Captured).Prompt;
        Assert.Contains("Additional focus from the orchestrator: Focus on slide titles and code blocks.", prompt, StringComparison.Ordinal);
        Assert.Contains("never invent content to satisfy it", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotOcrProviderOmitsFocusLineWhenInstructionIsEmpty()
    {
        var llm = new CapturingLlmProvider();
        var provider = new CopilotOcrProvider(llm, "test-model");

        await provider.ExtractTextAsync("frame.jpg", string.Empty, CancellationToken.None);

        var prompt = Assert.Single(llm.Captured).Prompt;
        Assert.DoesNotContain("Additional focus from the orchestrator", prompt, StringComparison.Ordinal);
        Assert.Contains("Extract every readable piece of text", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotOcrProviderIncludesFocusLineWhenInstructionIsProvided()
    {
        var llm = new CapturingLlmProvider();
        var provider = new CopilotOcrProvider(llm, "test-model");

        await provider.ExtractTextAsync("frame.jpg", "Preserve indentation in code-like text.", CancellationToken.None);

        var prompt = Assert.Single(llm.Captured).Prompt;
        Assert.Contains("Additional focus from the orchestrator: Preserve indentation in code-like text.", prompt, StringComparison.Ordinal);
        Assert.Contains("never invent characters that are not visible", prompt, StringComparison.Ordinal);
    }

    private sealed class CapturingLlmProvider : ILlmProvider
    {
        public List<LlmRequest> Captured { get; } = [];

        public string Name => "capturing";

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Captured.Add(request);
            return Task.FromResult("{\"freeText\":\"\",\"lines\":[],\"tables\":[],\"kind\":\"other\",\"bullets\":[],\"codeBlocks\":[],\"charts\":[],\"uiElements\":[]}");
        }
    }

    [Fact]
    public async Task ConfigStoreSupportsFramesSceneSafetyCapSetting()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await store.SetAsync("frames.sceneSafetyCap", "5000", CancellationToken.None);
        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(5000, config.Frames.SceneSafetyCap);
        Assert.Equal("5000", await store.GetAsync("frames.sceneSafetyCap", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigStoreRejectsNonPositiveSceneSafetyCap()
    {
        using var temp = new TestTempDirectory();
        var store = new ConfigStore(temp.GetPath("config.json"));

        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("frames.sceneSafetyCap", "0", CancellationToken.None));
        await Assert.ThrowsAsync<ReplayException>(() => store.SetAsync("frames.sceneSafetyCap", "-100", CancellationToken.None));
    }

    [Fact]
    public async Task DoctorJsonOutputsMachineReadableDependencyReport()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(["doctor", "--json"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var document = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.True(document.RootElement.TryGetProperty("createdAt", out _));
        var dependencies = document.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Contains(dependencies, dependency => dependency.GetProperty("name").GetString() == "yt-dlp");
        Assert.All(dependencies, dependency => Assert.True(dependency.TryGetProperty("isFound", out _)));
    }

    [Fact]
    public async Task VersionAndInfoCommandsReportApplicationMetadata()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var versionExitCode = await CliApp.RunAsync(["version"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, versionExitCode);
        Assert.Contains(ReplayVersion.Current, stdout.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        var infoExitCode = await CliApp.RunAsync(["info", "--json"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, infoExitCode);
        Assert.Contains("configPath", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("llmProvider", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static HashSet<string> UniqueWords(string value)
    {
        return value.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '-', '>', '<'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed class FakeProcessRunner : ProcessRunner
    {
        private readonly Func<string, IReadOnlyList<string>, ProcessResult> handler;

        public FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult> handler)
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
            var args = arguments.ToArray();
            return Task.FromResult(handler(fileName, args));
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler, IDisposable
    {
        private readonly Func<HttpRequestMessage, HttpContent> responseFactory;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpContent> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = responseFactory(request)
            });
        }
    }
}
