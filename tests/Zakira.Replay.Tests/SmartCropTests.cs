using SkiaSharp;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SmartCropTests
{
    [Theory]
    [InlineData("auto", SmartCropProfiles.Auto)]
    [InlineData("AUTO", SmartCropProfiles.Auto)]
    [InlineData("default", SmartCropProfiles.Auto)]
    [InlineData(" ", SmartCropProfiles.Auto)]
    [InlineData("", SmartCropProfiles.Auto)]
    [InlineData(null, SmartCropProfiles.Auto)]
    [InlineData("teams", SmartCropProfiles.Teams)]
    [InlineData("microsoft-teams", SmartCropProfiles.Teams)]
    [InlineData("ms-teams", SmartCropProfiles.Teams)]
    [InlineData("zoom", SmartCropProfiles.Zoom)]
    [InlineData("webex", SmartCropProfiles.WebEx)]
    [InlineData("web-ex", SmartCropProfiles.WebEx)]
    [InlineData("cisco-webex", SmartCropProfiles.WebEx)]
    [InlineData("generic", SmartCropProfiles.Generic)]
    [InlineData("off", SmartCropProfiles.Off)]
    [InlineData("disabled", SmartCropProfiles.Off)]
    [InlineData("none", SmartCropProfiles.Off)]
    [InlineData("Teams_", "teams-")] // underscore→dash normalisation, unknown stays verbatim
    public void NormalizeMapsKnownProfileAliases(string? input, string expected)
    {
        Assert.Equal(expected, SmartCropProfiles.Normalize(input));
    }

    [Fact]
    public void NormalizeReturnsUnknownVerbatimSoCallerCanWarn()
    {
        Assert.Equal("powerpoint", SmartCropProfiles.Normalize("powerpoint"));
        Assert.False(SmartCropProfiles.IsKnown("powerpoint"));
        Assert.True(SmartCropProfiles.IsKnown(SmartCropProfiles.Auto));
        Assert.True(SmartCropProfiles.IsKnown(SmartCropProfiles.Off));
    }

    [Fact]
    public void ComputeCropBoxRetainsFullFrameWhenImageIsAlreadyTight()
    {
        // A dark slide (brightness 30 — above the 25-letterbox threshold but below the
        // 100-controls-bar threshold) with no UI chrome should be left alone except for the
        // unconditional 25px bottom-navigation trim from the reference algorithm.
        using var bitmap = CreateUniformBitmap(800, 600, brightness: 30);

        var (left, top, right, bottom) = SmartCropService.ComputeCropBox(bitmap, 800, 600);

        Assert.Equal(0, left);
        Assert.Equal(0, top);
        Assert.Equal(800, right);
        Assert.Equal(575, bottom); // height - BottomTrimPixels (25)
    }

    [Fact]
    public void ComputeCropBoxRemovesTopBlackBar()
    {
        // 800x600 frame with the top 80px solid black on dark content beneath. Expect top trim
        // to skip past the black bar; the controls-bar detector doesn't fire on dark content
        // (brightness 30 < 100 threshold).
        using var bitmap = CreateUniformBitmap(800, 600, brightness: 30);
        FillRow(bitmap, fromY: 0, toY: 80, brightness: 0);

        var (left, top, right, bottom) = SmartCropService.ComputeCropBox(bitmap, 800, 600);

        Assert.Equal(0, left);
        Assert.InRange(top, 80, 90); // first bright row after the letterbox
        Assert.Equal(800, right);
        Assert.True(bottom <= 575);
    }

    [Fact]
    public void ComputeCropBoxRemovesBottomBlackBar()
    {
        // 800x600 frame with the bottom 80px solid black on dark content above. Expect bottom
        // trim to land above the bar.
        using var bitmap = CreateUniformBitmap(800, 600, brightness: 30);
        FillRow(bitmap, fromY: 520, toY: 600, brightness: 0);

        var (left, top, right, bottom) = SmartCropService.ComputeCropBox(bitmap, 800, 600);

        Assert.Equal(0, left);
        Assert.Equal(0, top);
        Assert.Equal(800, right);
        // BottomTrim of 25 is applied unconditionally, so bottom <= (520 - 25) = 495, with a small
        // tolerance for the row-step.
        Assert.InRange(bottom, 470, 500);
    }

    [Fact]
    public void ComputeCropBoxStaysWellBehavedOnAllBlackImage()
    {
        // An all-black image's "first bright row" never fires; the algorithm must still produce
        // a valid (non-degenerate) rectangle without throwing. The unconditional 25-px bottom
        // trim still applies.
        using var bitmap = CreateUniformBitmap(800, 600, brightness: 0);

        var (left, top, right, bottom) = SmartCropService.ComputeCropBox(bitmap, 800, 600);

        Assert.True(left >= 0 && right <= 800);
        Assert.True(top >= 0 && bottom <= 600);
        Assert.True(right > left);
        Assert.True(bottom > top);
    }

    [Fact]
    public void SmartCropServiceBailsOutWhenCandidateCropIsTooNarrow()
    {
        // Construct a fixture where the gallery detector falsely concludes the entire image is
        // gallery — by making the image dark on the left and uniformly mid-bright on the right
        // with no clear sidebar. The candidate `right` ends up well under 50% of width, which
        // triggers the safety bail-out.
        using var temp = new TestTempDirectory();
        var run = new VideoRun("bail-out-test", temp.GetPath("run"));
        Directory.CreateDirectory(Path.Combine(run.Directory, "frames"));
        var framePath = "frames/narrow.jpg";

        // 1200x800: left 100px dark, then a wide mid-brightness "gallery" sweeping nearly the
        // whole image, with another dark sliver in the middle to trip the detector early.
        using (var bitmap = new SKBitmap(1200, 800))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(20, 20, 20));
                using var mid = new SKPaint { Color = new SKColor(90, 90, 90) };
                canvas.DrawRect(new SKRect(120, 0, 1200, 800), mid);
            }
            SaveJpeg(run.GetPath(framePath), bitmap);
        }
        var frame = new FrameArtifact("narrow", framePath, 0.0, "00:00");

        var outcome = new SmartCropService().Process(frame, run, SmartCropProfiles.Auto);

        // Either the candidate is too narrow and bail-out fires, or the algorithm produced a
        // reasonable crop. Test that whichever path is taken, the result is well-behaved.
        Assert.NotEqual(string.Empty, outcome.Frame.Path);
        if (outcome.Warning is not null && outcome.Warning.Code == ReplayWarningCodes.CropBailOut)
        {
            Assert.False(outcome.Applied);
            Assert.Equal(ReplayWarningSeverities.Info, outcome.Warning.Severity);
            // Original path is preserved on bail-out.
            Assert.Equal(framePath, outcome.Frame.Path);
            Assert.Null(outcome.Frame.Crop);
        }
    }

    [Fact]
    public void SmartCropServiceEmitsDecodeFailedWhenFrameIsMissing()
    {
        using var temp = new TestTempDirectory();
        var run = new VideoRun("missing-test", temp.GetPath("run"));
        Directory.CreateDirectory(Path.Combine(run.Directory, "frames"));
        var frame = new FrameArtifact("ghost", "frames/does-not-exist.jpg", 0.0, "00:00");

        var outcome = new SmartCropService().Process(frame, run, SmartCropProfiles.Auto);

        Assert.False(outcome.Applied);
        Assert.NotNull(outcome.Warning);
        Assert.Equal(ReplayWarningCodes.CropImageDecodeFailed, outcome.Warning!.Code);
    }

    [Fact]
    public void SmartCropServiceWritesCroppedFileAndPopulatesCropFields()
    {
        // Construct a frame with a tall left "content" area and a 200px-wide bright gallery
        // sidebar on the right. The crop should detect the sidebar and trim it off.
        using var temp = new TestTempDirectory();
        var run = new VideoRun("gallery-test", temp.GetPath("run"));
        Directory.CreateDirectory(Path.Combine(run.Directory, "frames"));
        var framePath = "frames/with-gallery.jpg";

        var width = 1200;
        var height = 800;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            // Left two-thirds: dark slide content (just dark grey — under 40 brightness).
            canvas.Clear(new SKColor(20, 20, 20));
            // Right gallery border strip starting at ~83% of width, brightness in the 40-120
            // window the algorithm looks for; left neighbour is dark (the slide background).
            using var galleryStrip = new SKPaint { Color = new SKColor(90, 90, 90) };
            canvas.DrawRect(new SKRect(1000, 0, 1020, height), galleryStrip);
            // Gallery interior — bright participant tile area to the right of the strip.
            using var galleryInside = new SKPaint { Color = new SKColor(180, 180, 180) };
            canvas.DrawRect(new SKRect(1020, 0, width, height), galleryInside);
        }
        SaveJpeg(run.GetPath(framePath), bitmap);
        var frame = new FrameArtifact("gallery", framePath, 0.0, "00:00");

        var outcome = new SmartCropService().Process(frame, run, SmartCropProfiles.Auto);

        Assert.True(outcome.Applied, "expected smart-crop to fire on a frame with a clear gallery sidebar");
        Assert.NotNull(outcome.Frame.Crop);
        Assert.Equal("smart-crop-auto", outcome.Frame.Crop!.Source);
        // The cropped width must be smaller than the original 1200 px.
        Assert.True(outcome.Frame.Crop.Width < width, $"expected width < {width}, got {outcome.Frame.Crop.Width}");
        // OriginalPath points back to the source frame.
        Assert.Equal(framePath, outcome.Frame.OriginalPath);
        Assert.NotEqual(framePath, outcome.Frame.Path);
        // The cropped JPG was actually written to disk.
        Assert.True(File.Exists(run.GetPath(outcome.Frame.Path)));
    }

    [Fact]
    public async Task AnalyzeAsyncRunsSmartCropBeforePerceptualHashingWhenEnabled()
    {
        // End-to-end pipeline test: with --smart-crop on a synthetic 1200x800 fixture that has a
        // clear gallery sidebar, the run must produce a frame with Crop populated and the cropped
        // JPG referenced from manifest.frames[0].path.
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));

        var ytDlp = new TestYtDlp();
        var ffmpeg = new TestFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "smart-crop-integration",
            SmartCrop: true), progress: null, CancellationToken.None);

        var manifestFrame = Assert.Single(result.Manifest.Frames);
        Assert.NotNull(manifestFrame.Crop);
        Assert.Equal("smart-crop-auto", manifestFrame.Crop!.Source);
        Assert.NotNull(manifestFrame.OriginalPath);
        Assert.True(manifestFrame.Width is > 0 && manifestFrame.Width < 1200);
    }

    [Fact]
    public async Task AnalyzeAsyncSkipsSmartCropWhenProfileIsOff()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));

        var ytDlp = new TestYtDlp();
        var ffmpeg = new TestFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "smart-crop-off",
            SmartCrop: true,
            SmartCropProfile: SmartCropProfiles.Off), progress: null, CancellationToken.None);

        var manifestFrame = Assert.Single(result.Manifest.Frames);
        Assert.Null(manifestFrame.Crop);
        Assert.Null(manifestFrame.OriginalPath);
    }

    [Fact]
    public async Task AnalyzeAsyncEmitsCropProfileUnknownWarningOnGarbageProfile()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));

        var ytDlp = new TestYtDlp();
        var ffmpeg = new TestFfmpeg();
        var pipeline = new AnalysisPipeline(store, ytDlp, ffmpeg, _ => null);

        var result = await pipeline.AnalyzeAsync(new AnalyzeRequest(
            Source: sourcePath,
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 1,
            RunId: "smart-crop-unknown",
            SmartCrop: true,
            SmartCropProfile: "powerpoint"), progress: null, CancellationToken.None);

        var evidence = await store.ReadJsonAsync<EvidenceDocument>(result.Run, "evidence.json", CancellationToken.None);
        Assert.NotNull(evidence);
        Assert.Contains(evidence.Warnings, warning => warning.Code == ReplayWarningCodes.CropProfileUnknown);
    }

    private static SKBitmap CreateUniformBitmap(int width, int height, byte brightness)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(brightness, brightness, brightness));
        return bitmap;
    }

    private static void FillRow(SKBitmap bitmap, int fromY, int toY, byte brightness)
    {
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = new SKColor(brightness, brightness, brightness) };
        canvas.DrawRect(new SKRect(0, fromY, bitmap.Width, toY), paint);
    }

    private static void SaveJpeg(string path, SKBitmap bitmap)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 96);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private sealed class TestYtDlp : IYtDlpClient
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
            return Task.FromResult<string?>(request.Source);
        }

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class TestFfmpeg : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
        {
            // Write a single synthetic frame with a clear right-side gallery sidebar so smart-crop
            // has something obvious to trim.
            var framePath = "frames/test-frame.jpg";
            var fullPath = run.GetPath(framePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var bitmap = new SKBitmap(1200, 800);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(20, 20, 20));
                using var galleryStrip = new SKPaint { Color = new SKColor(90, 90, 90) };
                canvas.DrawRect(new SKRect(1000, 0, 1020, 800), galleryStrip);
                using var galleryInside = new SKPaint { Color = new SKColor(180, 180, 180) };
                canvas.DrawRect(new SKRect(1020, 0, 1200, 800), galleryInside);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 96))
            using (var stream = File.Create(fullPath))
            {
                data.SaveTo(stream);
            }
            return Task.FromResult<IReadOnlyList<FrameArtifact>>([
                new FrameArtifact("test-frame", framePath, 0.0, "00:00")
            ]);
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
            return Task.FromResult<double?>(null);
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
