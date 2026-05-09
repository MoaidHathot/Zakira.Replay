using Zakira.Replay.Core;
using Zakira.Replay.Cli;
using System.IO.Compression;
using System.Net;

namespace Zakira.Replay.Tests;

public sealed class CoreTests
{
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
        Assert.Contains("0.1.0", stdout.ToString(), StringComparison.Ordinal);

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
