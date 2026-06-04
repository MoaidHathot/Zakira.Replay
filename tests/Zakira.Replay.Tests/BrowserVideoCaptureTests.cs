using System.Text;
using SkiaSharp;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class BrowserVideoCaptureTests
{
    [Theory]
    [InlineData("ytdlp", CaptureModes.YtDlp)]
    [InlineData("yt-dlp", CaptureModes.YtDlp)]
    [InlineData("ffmpeg", CaptureModes.YtDlp)]
    [InlineData("default", CaptureModes.YtDlp)]
    [InlineData("", CaptureModes.YtDlp)]
    [InlineData(null, CaptureModes.YtDlp)]
    [InlineData("auto", CaptureModes.Auto)]
    [InlineData("AUTO", CaptureModes.Auto)]
    [InlineData("browser", CaptureModes.Browser)]
    [InlineData("playwright", CaptureModes.Browser)]
    [InlineData("chromium", CaptureModes.Browser)]
    [InlineData("edge", CaptureModes.Browser)]
    [InlineData(" page ", CaptureModes.Browser)]
    public void NormalizeMapsAliasesToCanonicalModes(string? input, string expected)
    {
        Assert.Equal(expected, CaptureModes.Normalize(input));
    }

    [Fact]
    public void NormalizeReturnsUnknownVerbatimSoCallerCanWarn()
    {
        Assert.Equal("kiosk-mode", CaptureModes.Normalize("kiosk-mode"));
        Assert.False(CaptureModes.IsKnown("kiosk-mode"));
        Assert.True(CaptureModes.IsKnown(CaptureModes.Auto));
        Assert.True(CaptureModes.IsKnown(CaptureModes.YtDlp));
        Assert.True(CaptureModes.IsKnown(CaptureModes.Browser));
    }

    [Fact]
    public void ComputeTimestampsProducesEvenlySpacedInteriorPoints()
    {
        // 120s video, 7 frames → 120/8 * {1..7} = 15, 30, 45, 60, 75, 90, 105.
        var ts = PlaywrightVideoCaptureClient.ComputeTimestamps(120.0, 7);

        Assert.Equal(7, ts.Count);
        Assert.Equal(15.0, ts[0]);
        Assert.Equal(105.0, ts[6]);
        // Strictly increasing, strictly inside (0, duration).
        for (var i = 1; i < ts.Count; i++)
        {
            Assert.True(ts[i] > ts[i - 1]);
        }
        Assert.True(ts[0] > 0);
        Assert.True(ts[^1] < 120.0);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(-1, 7)]
    [InlineData(60, 0)]
    [InlineData(60, -1)]
    public void ComputeTimestampsReturnsEmptyForInvalidInput(double duration, int frameCount)
    {
        Assert.Empty(PlaywrightVideoCaptureClient.ComputeTimestamps(duration, frameCount));
    }

    [Fact]
    public async Task AnalyzeAsyncRoutesToBrowserCaptureWhenCaptureModeIsBrowser()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            }
        };

        var ytDlp = new RecordingYtDlpClient
        {
            // Pretend yt-dlp has no media URL — would normally trigger FRAMES_NO_MEDIA, but with
            // CaptureMode=browser we skip the yt-dlp branch entirely.
            ResolvedMediaUrl = null
        };
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video123",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 7,
            RunId: "browser-capture-routing",
            CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

        Assert.True(browser.WasCalled, "expected browser capture client to be invoked");
        Assert.False(ffmpeg.ExtractFramesCalled, "expected ffmpeg.ExtractFrames to be skipped when CaptureMode=browser");
        var frame = Assert.Single(result.Manifest.Frames);
        Assert.Equal("scene-0001", frame.Id);
    }

    [Fact]
    public async Task AnalyzeAsyncAutoModeFallsBackToBrowserWhenYtDlpCannotResolveMedia()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            }
        };
        var ytDlp = new RecordingYtDlpClient
        {
            ResolvedMediaUrl = null // yt-dlp -g comes back empty
        };
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video123",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 7,
            RunId: "browser-capture-auto-fallback",
            CaptureMode: CaptureModes.Auto), progress: null, CancellationToken.None);

        Assert.True(browser.WasCalled);
        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.CaptureBrowserFallback);
    }

    [Fact]
    public async Task AnalyzeAsyncDefaultsToYtDlpWhenCaptureModeIsNotSet()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture();
        var ytDlp = new RecordingYtDlpClient
        {
            ResolvedMediaUrl = "https://cdn.example.test/video.mp4"
        };
        var ffmpeg = new RecordingFfmpegClient
        {
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            }
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/public-video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "ytdlp-default"), progress: null, CancellationToken.None);

        Assert.True(ffmpeg.ExtractFramesCalled, "expected default capture mode to use ffmpeg");
        Assert.False(browser.WasCalled, "browser client should not have been invoked");
        Assert.Single(result.Manifest.Frames);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsCaptureUnknownModeWarningOnGarbageMode()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var ytDlp = new RecordingYtDlpClient
        {
            ResolvedMediaUrl = "https://cdn.example.test/video.mp4"
        };
        var ffmpeg = new RecordingFfmpegClient
        {
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 0.0, "00:00")];
            }
        };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/public-video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "capture-unknown",
            CaptureMode: "obs-studio"), progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.CaptureUnknownMode);
        // Falls back to ytdlp.
        Assert.True(ffmpeg.ExtractFramesCalled);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsCaptureBrowserUnavailableWhenNoClientInjected()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var ytDlp = new RecordingYtDlpClient { ResolvedMediaUrl = null };
        var ffmpeg = new RecordingFfmpegClient();
        // No browser-capture client provided.
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video123",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 7,
            RunId: "browser-capture-missing-client",
            CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, warning => warning.Code == ReplayWarningCodes.CaptureBrowserUnavailable);
    }

    [Fact]
    public async Task AnalyzeAsyncFillsTranscriptFromBrowserDiscoveredCaptionsWhenNoneOtherwise()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            },
            CaptionProducer = run =>
            {
                // Simulate that the network listener already wrote two caption files: a French
                // one and an English one. The pipeline picks based on the configured language
                // preference (default `auto` means original-language wins).
                var captionsDir = run.GetPath("captions");
                Directory.CreateDirectory(captionsDir);
                var enRelative = "captions/browser-0001.vtt";
                var frRelative = "captions/browser-0002.vtt";
                File.WriteAllText(run.GetPath(enRelative), VttFixture("Hello world"));
                File.WriteAllText(run.GetPath(frRelative), VttFixture("Bonjour le monde"));
                return
                [
                    new BrowserCapturedCaption(1, "https://cdn.test/Caption_en-US.vtt", enRelative, "en-US", "url-Caption_<lang>", 100, "h1", "text/vtt"),
                    new BrowserCapturedCaption(2, "https://cdn.test/Caption_fr.vtt", frRelative, "fr", "url-Caption_<lang>", 100, "h2", "text/vtt")
                ];
            }
        };
        var ytDlp = new RecordingYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "test",
                Title = "Test Video",
                WebpageUrl = "https://example.test/private-portal/video123",
                Language = "en"
            },
            ResolvedMediaUrl = null
        };
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video123",
            VisionInstruction: string.Empty,
            IncludeTranscript: true,
            FrameCount: 1,
            RunId: "browser-captions-fill-transcript",
            CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

        // Transcript was filled from the browser-discovered English caption (matches info.Language).
        Assert.NotNull(result.Manifest.TranscriptPath);
        Assert.Equal("transcript.md", result.Manifest.TranscriptPath);
        var transcriptText = await File.ReadAllTextAsync(result.Run.GetPath("transcript.md"), CancellationToken.None);
        Assert.Contains("Hello world", transcriptText, StringComparison.Ordinal);

        // discovered.json was persisted with both captions and the original-language hint.
        Assert.True(File.Exists(result.Run.GetPath("captions/discovered.json")));
        var discovered = await store.ReadJsonAsync<BrowserCapturedCaptionsManifest>(result.Run, "captions/discovered.json", CancellationToken.None);
        Assert.NotNull(discovered);
        Assert.Equal("en", discovered!.OriginalLanguage);
        Assert.Equal(2, discovered.Captions.Count);

        // No transcript-not-found warning (it was retroactively cleared).
        Assert.DoesNotContain(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.TranscriptNotFound || w.Code == ReplayWarningCodes.TranscriptNotFoundNoStt);
    }

    [Fact]
    public async Task AnalyzeAsyncWritesSecondaryTranscriptsWhenRequested()
    {
        // Same two-caption setup as the primary test, but request 'fr' as a secondary
        // language. Expect: transcript.md (primary, en), transcript.fr.md (secondary), and
        // manifest.SecondaryTranscripts surfacing the fr entry as a relative path.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            },
            CaptionProducer = run =>
            {
                var captionsDir = run.GetPath("captions");
                Directory.CreateDirectory(captionsDir);
                var enRelative = "captions/browser-0001.vtt";
                var frRelative = "captions/browser-0002.vtt";
                File.WriteAllText(run.GetPath(enRelative), VttFixture("Hello world"));
                File.WriteAllText(run.GetPath(frRelative), VttFixture("Bonjour le monde"));
                return
                [
                    new BrowserCapturedCaption(1, "https://cdn.test/Caption_en-US.vtt", enRelative, "en-US", "url-Caption_<lang>", 100, "h1", "text/vtt"),
                    new BrowserCapturedCaption(2, "https://cdn.test/Caption_fr.vtt",    frRelative, "fr",    "url-Caption_<lang>", 100, "h2", "text/vtt")
                ];
            }
        };
        var ytDlp = new RecordingYtDlpClient
        {
            Info = new YtDlpInfo
            {
                Id = "test",
                Title = "Test Video",
                WebpageUrl = "https://example.test/private-portal/video123",
                Language = "en"
            },
            ResolvedMediaUrl = null
        };
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video123",
            VisionInstruction: string.Empty,
            IncludeTranscript: true,
            FrameCount: 1,
            RunId: "browser-secondary-transcripts",
            CaptureMode: CaptureModes.Browser,
            SecondaryCaptionLanguages: ["fr"]),
            progress: null, CancellationToken.None);

        // Primary unchanged: transcript.md is the English one.
        Assert.Equal("transcript.md", result.Manifest.TranscriptPath);
        Assert.Contains("Hello world", await File.ReadAllTextAsync(result.Run.GetPath("transcript.md"), CancellationToken.None), StringComparison.Ordinal);

        // Secondary: transcript.fr.md exists with the French cue content, and the manifest
        // surfaces a relative path agents can read deterministically.
        Assert.True(File.Exists(result.Run.GetPath("transcript.fr.md")));
        Assert.Contains("Bonjour le monde", await File.ReadAllTextAsync(result.Run.GetPath("transcript.fr.md"), CancellationToken.None), StringComparison.Ordinal);

        Assert.NotNull(result.Manifest.SecondaryTranscripts);
        var secondary = Assert.Single(result.Manifest.SecondaryTranscripts!);
        Assert.Equal("fr", secondary.Language);
        Assert.Equal("transcript.fr.md", secondary.MarkdownPath);
        // SourcePath is the original caption file, not the .md.
        Assert.Equal("captions/browser-0002.vtt", secondary.SourcePath);
    }

    [Fact]
    public async Task AnalyzeAsyncOmitsSecondaryTranscriptsByDefault()
    {
        // Regression guard for the "off by default" contract: omitting
        // SecondaryCaptionLanguages must produce zero secondary transcripts even when multiple
        // languages are downloaded into captions/.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            },
            CaptionProducer = run =>
            {
                var captionsDir = run.GetPath("captions");
                Directory.CreateDirectory(captionsDir);
                var enRelative = "captions/browser-0001.vtt";
                var frRelative = "captions/browser-0002.vtt";
                File.WriteAllText(run.GetPath(enRelative), VttFixture("Hello world"));
                File.WriteAllText(run.GetPath(frRelative), VttFixture("Bonjour le monde"));
                return
                [
                    new BrowserCapturedCaption(1, "https://cdn.test/Caption_en-US.vtt", enRelative, "en-US", "url-Caption_<lang>", 100, "h1", "text/vtt"),
                    new BrowserCapturedCaption(2, "https://cdn.test/Caption_fr.vtt",    frRelative, "fr",    "url-Caption_<lang>", 100, "h2", "text/vtt")
                ];
            }
        };
        var ytDlp = new RecordingYtDlpClient { Info = new YtDlpInfo { Id = "test", Language = "en" } };
        var pipeline = new AnalysisPipeline(store, ytDlp, new RecordingFfmpegClient(), _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/no-secondary",
            VisionInstruction: string.Empty,
            IncludeTranscript: true,
            FrameCount: 1,
            RunId: "browser-no-secondary",
            CaptureMode: CaptureModes.Browser),
            progress: null, CancellationToken.None);

        Assert.False(File.Exists(result.Run.GetPath("transcript.fr.md")));
        Assert.True(result.Manifest.SecondaryTranscripts is null || result.Manifest.SecondaryTranscripts.Count == 0);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsCaptionsBrowserNetworkNoneWhenNoCaptionsCaptured()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            }
            // No CaptionProducer — listener saw no caption responses.
        };
        var ytDlp = new RecordingYtDlpClient { ResolvedMediaUrl = null };
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: true,
            FrameCount: 1,
            RunId: "browser-captions-none",
            CaptureMode: CaptureModes.Browser), progress: null, CancellationToken.None);

        Assert.Contains(result.Manifest.Warnings, w => w.Code == ReplayWarningCodes.CaptionsBrowserNetworkNone);
        Assert.Null(result.Manifest.TranscriptPath);
    }

    [Fact]
    public async Task AnalyzeAsyncDoesNotOverwriteExistingTranscriptWithBrowserCaptions()
    {
        using var temp = new TestTempDirectory();

        // Use a local file with a sidecar VTT so the existing transcript path fires before the
        // frame step, then verify browser captions don't clobber it.
        var sourcePath = temp.GetPath("local.mp4");
        await File.WriteAllBytesAsync(sourcePath, new byte[] { 0 }, CancellationToken.None);
        var sidecarPath = temp.GetPath("local.vtt");
        await File.WriteAllTextAsync(sidecarPath, VttFixture("Sidecar transcript"), CancellationToken.None);

        var store = new ArtifactStore(temp.GetPath("runs"));
        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = _ => [],
            CaptionProducer = run =>
            {
                var captionsDir = run.GetPath("captions");
                Directory.CreateDirectory(captionsDir);
                var rel = "captions/browser-0001.vtt";
                File.WriteAllText(run.GetPath(rel), VttFixture("Browser-discovered fallback"));
                return [new BrowserCapturedCaption(1, "https://cdn.test/Caption_en.vtt", rel, "en", "url-Caption_<lang>", 50, "h1", "text/vtt")];
            }
        };
        var ytDlp = new RecordingYtDlpClient();
        var ffmpeg = new RecordingFfmpegClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: string.Empty,
            IncludeTranscript: true,
            FrameCount: 0,
            RunId: "browser-captions-no-overwrite"), progress: null, CancellationToken.None);

        var transcriptText = await File.ReadAllTextAsync(result.Run.GetPath("transcript.md"), CancellationToken.None);
        Assert.Contains("Sidecar transcript", transcriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("Browser-discovered fallback", transcriptText, StringComparison.Ordinal);
    }

    private static string VttFixture(string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        sb.AppendLine("00:00:00.000 --> 00:00:05.000");
        sb.AppendLine(text);
        sb.AppendLine();
        return sb.ToString();
    }

    private static void WriteJpeg(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(640, 360);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(40, 40, 40));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    [Fact]
    public async Task AnalyzeAsyncFallsBackToInlineMediaUrlWhenDurationUnresolvedAndUrlDiscovered()
    {
        // Simulates the KEY01 / Medius case: the JS player never boots so DurationSeconds is
        // null and the in-browser FrameProducer captured nothing, BUT the Medius interceptor
        // recovered the HLS URL from the embed HTML and surfaced it as InlineMediaUrl. The
        // pipeline must hand that URL to ffmpeg and produce frames.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = null,
            FrameProducer = _ => [],
            InlineMediaUrl = "https://stream.event.microsoft.com/prodwe/x/master.m3u8"
        };
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 7200 };
        var ytDlp = new RecordingYtDlpClient { Info = new YtDlpInfo { Id = "key01", Language = "en" }, ResolvedMediaUrl = null };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 5,
            RunId: "sidestep-fallback",
            CaptureMode: CaptureModes.Browser),
            progress: null, CancellationToken.None);

        // ffmpeg received the inline HLS URL (not yt-dlp's null, not a local download path).
        Assert.Equal("https://stream.event.microsoft.com/prodwe/x/master.m3u8", ffmpeg.LastMediaSource);
        // 5 frames produced via the sidestep — no empty-frames result.
        Assert.Equal(5, result.Manifest.Frames.Count);
        // Info breadcrumb explains the path taken.
        Assert.Contains(result.Manifest.Warnings,
            w => w.Code == ReplayWarningCodes.CaptureBrowserFallback && w.Message.Contains("duration-unresolved-fallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncPreferInlineMediaSkipsFullCaptureAndUsesSidestep()
    {
        // --prefer-inline-media bypasses the full browser capture; the MetadataOnly probe
        // returns the inline URL, ffmpeg seeks from it. Cuts a ~25s probe to ~3-5s on
        // sources where we know the player won't boot.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = null,
            FrameProducer = _ => [],
            InlineMediaUrl = "https://stream.event.microsoft.com/prodwe/x/master.m3u8"
        };
        var ffmpeg = new RecordingFfmpegClient { ProbedDuration = 7200 };
        var ytDlp = new RecordingYtDlpClient { ResolvedMediaUrl = null };
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 3,
            RunId: "prefer-inline-media",
            CaptureMode: CaptureModes.Browser,
            PreferInlineMedia: true),
            progress: null, CancellationToken.None);

        Assert.NotNull(browser.LastRequest);
        Assert.True(browser.LastRequest!.MetadataOnly,
            "PreferInlineMedia must drive the browser via the MetadataOnly fast path, not the full capture.");
        Assert.Equal(3, result.Manifest.Frames.Count);
        Assert.Equal("https://stream.event.microsoft.com/prodwe/x/master.m3u8", ffmpeg.LastMediaSource);
        Assert.Contains(result.Manifest.Warnings,
            w => w.Code == ReplayWarningCodes.CaptureBrowserFallback && w.Message.Contains("prefer-inline-media", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncPreferInlineMediaFallsThroughToFullCaptureWhenNoInlineUrl()
    {
        // PreferInlineMedia is best-effort: when the probe yields no URL, we fall back to the
        // regular browser-capture path so the request still has a chance to produce frames.
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));

        var browser = new FakeBrowserCapture
        {
            DurationSeconds = 60.0,
            FrameProducer = run =>
            {
                var framePath = "frames/scene-0001.jpg";
                WriteJpeg(run.GetPath(framePath));
                return [new FrameArtifact("scene-0001", framePath, 7.5, "00:07")];
            },
            InlineMediaUrl = null,
        };
        var ffmpeg = new RecordingFfmpegClient();
        var ytDlp = new RecordingYtDlpClient();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null, browser);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: "https://example.test/private-portal/video",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "prefer-inline-fallthrough",
            CaptureMode: CaptureModes.Browser,
            PreferInlineMedia: true),
            progress: null, CancellationToken.None);

        // We got the frame from the full-capture path (FrameProducer ran, not ffmpeg).
        Assert.Single(result.Manifest.Frames);
        Assert.Null(ffmpeg.LastMediaSource);
        Assert.Contains(result.Manifest.Warnings,
            w => w.Code == ReplayWarningCodes.CaptureBrowserFallback && w.Message.Contains("no inline media URL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsyncResolvesAutoplayPolicyFromHostMapAndThreadsItToBrowser()
    {
        // capture.browser.autoplayPolicyByHost says "build.microsoft.com" gets the no-gesture
        // policy. CLI override is unset. The resolved policy must reach the browser request.
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        await File.WriteAllTextAsync(configPath, """
        {
          "capture": {
            "browser": {
              "autoplayPolicyByHost": { "build.microsoft.com": "no-user-gesture-required" }
            }
          }
        }
        """, CancellationToken.None);

        var previousEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", configPath);
        try
        {
            var store = new ArtifactStore(temp.GetPath("runs"));
            var browser = new FakeBrowserCapture { DurationSeconds = 60.0, FrameProducer = _ => [] };
            var pipeline = new AnalysisPipeline(store, new RecordingYtDlpClient(), new RecordingFfmpegClient(), _ => null, browser);

            await pipeline.AnalyzeAsync(new AnalyzeRequest(
                Source: "https://build.microsoft.com/en-US/sessions/KEY01?source=sessions",
                VisionInstruction: string.Empty,
                IncludeTranscript: false,
                FrameCount: 1,
                RunId: "autoplay-host-map",
                CaptureMode: CaptureModes.Browser),
                progress: null, CancellationToken.None);

            Assert.Equal("no-user-gesture-required", browser.LastAutoplayPolicy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", previousEnv);
        }
    }

    private sealed class FakeBrowserCapture : IBrowserVideoCaptureClient
    {
        public bool WasCalled { get; private set; }

        public double? DurationSeconds { get; init; }

        public Func<VideoRun, IReadOnlyList<FrameArtifact>>? FrameProducer { get; init; }

        public Func<VideoRun, IReadOnlyList<BrowserCapturedCaption>>? CaptionProducer { get; init; }

        public List<ReplayWarning> Warnings { get; } = [];

        /// <summary>
        /// Inline media URL the fake reports as if a registered interceptor had discovered it.
        /// When set + <see cref="DurationSeconds"/> null + <see cref="FrameProducer"/> null/empty,
        /// exercises the automatic sidestep fallback path in CaptureFramesAndCaptionsWithBrowserAsync.
        /// </summary>
        public string? InlineMediaUrl { get; init; }

        /// <summary>Records the last request so tests can assert MetadataOnly was set.</summary>
        public BrowserCaptureRequest? LastRequest { get; private set; }

        /// <summary>Records the resolved AutoplayPolicy passed through to the launch path.</summary>
        public string? LastAutoplayPolicy { get; private set; }

        public Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastRequest = request;
            LastAutoplayPolicy = request.AutoplayPolicy;
            var frames = FrameProducer?.Invoke(request.Run) ?? [];
            var captions = CaptionProducer?.Invoke(request.Run) ?? [];
            return Task.FromResult(new BrowserCaptureResult(frames, DurationSeconds, Warnings, captions, InlineMediaUrl: InlineMediaUrl));
        }
    }

    private sealed class RecordingYtDlpClient : IYtDlpClient
    {
        public YtDlpInfo Info { get; init; } = new()
        {
            Id = "test",
            Title = "Test",
            WebpageUrl = "https://example.test/video"
        };

        public string? ResolvedMediaUrl { get; init; }

        public string? DownloadedMediaPath { get; init; }

        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Info);
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        {
            return Task.FromResult<TranscriptArtifact?>(null);
        }

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResolvedMediaUrl);
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult(DownloadedMediaPath);
        }
    }

    private sealed class RecordingFfmpegClient : IFfmpegClient
    {
        public bool ExtractFramesCalled { get; private set; }

        public Func<VideoRun, IReadOnlyList<FrameArtifact>>? FrameProducer { get; init; }

        /// <summary>Optional probed-duration value returned to the pipeline.</summary>
        public double? ProbedDuration { get; init; }

        /// <summary>Records the last mediaSource ExtractFramesAsync was called with.</summary>
        public string? LastMediaSource { get; private set; }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
        {
            ExtractFramesCalled = true;
            LastMediaSource = mediaSource;
            // When no producer was wired up, synthesise `count` placeholder frames so tests that
            // only care about the path-taken (e.g. sidestep wired correctly) get a non-empty result.
            var frames = FrameProducer?.Invoke(run)
                ?? Enumerable.Range(0, Math.Max(0, count))
                    .Select(i => new FrameArtifact($"scene-{i + 1:0000}", $"frames/scene-{i + 1:0000}.jpg", i, Timestamp.Format(i)))
                    .ToArray();
            return Task.FromResult(frames);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        }

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([]);
        }

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult("audio/audio.wav");
        }

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
        {
            return Task.FromResult("clips/clip.mp4");
        }

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
        {
            return Task.FromResult(ProbedDuration);
        }

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SilenceWindow>>([]);
        }

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>("0000000000000000");
        }
    }
}
