using SkiaSharp;

namespace Zakira.Replay.Core;

/// <summary>
/// Stable identifiers for smart-crop profiles. Profile names are part of the public artifact
/// contract — they appear in <c>FrameCropBox.Source</c> as <c>smart-crop-{profile}</c>, in
/// <c>crop.profile</c> config, and in the CLI <c>--smart-crop-profile</c> flag. Today all
/// non-<see cref="Off"/> profiles share the same algorithm; the value is preserved so future
/// platform-specific tunings (different gallery-detection thresholds for Teams vs Zoom vs
/// WebEx) can branch on it without breaking existing artifacts.
/// </summary>
public static class SmartCropProfiles
{
    public const string Auto = "auto";

    public const string Teams = "teams";

    public const string Zoom = "zoom";

    public const string WebEx = "webex";

    public const string Generic = "generic";

    public const string Off = "off";

    /// <summary>
    /// Normalises a user-supplied profile string. Unknown values are returned verbatim so the
    /// caller can emit a <c>CROP_PROFILE_UNKNOWN</c> warning rather than silently fall back.
    /// </summary>
    public static string Normalize(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return Auto;
        }

        return profile.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "" or "auto" or "default" => Auto,
            "teams" or "microsoft-teams" or "ms-teams" => Teams,
            "zoom" => Zoom,
            "webex" or "cisco-webex" or "web-ex" => WebEx,
            "generic" => Generic,
            "off" or "disabled" or "none" => Off,
            var value => value
        };
    }

    /// <summary>
    /// True when the profile is a recognised crop-on profile (anything except <see cref="Off"/>
    /// or an unknown value).
    /// </summary>
    public static bool IsKnown(string profile)
    {
        return profile is Auto or Teams or Zoom or WebEx or Generic or Off;
    }
}

/// <summary>
/// Result of running smart-crop against a single frame. <see cref="Applied"/> distinguishes
/// "we cropped" from "we looked but the image was already tight" or "we hit the safety bail-out
/// and returned the original dimensions". Failed cases carry an explanatory
/// <see cref="ReplayWarning"/>.
/// </summary>
public sealed record SmartCropOutcome(
    FrameArtifact Frame,
    bool Applied,
    ReplayWarning? Warning);

public interface ISmartCropService
{
    SmartCropOutcome Process(FrameArtifact frame, VideoRun run, string profile);
}

/// <summary>
/// SkiaSharp-backed smart-crop. Port of the reference algorithm from the squad-skills
/// <c>conference-book-of-news</c> SKILL, adapted to a managed image library that we already
/// depend on transitively via RapidOcrNet.
/// </summary>
/// <remarks>
/// Algorithm (one frame in, one cropped frame out):
/// <list type="number">
/// <item><description>Trim top/bottom letterbox: scan rows for the first/last with at least 3
/// "bright" sample points (brightness &gt; 25).</description></item>
/// <item><description>Trim controls bar: within the first 80px of remaining content, find the
/// last fully-bright row (likely a UI control strip) and crop past it +5px.</description></item>
/// <item><description>Trim participant gallery sidebar: scan x from 90% &#x2192; 60% of width
/// for a thin bright strip that has a dark area to its immediate left (the slide content vs the
/// gallery). 60% of y-sample hits required.</description></item>
/// <item><description>Trim bottom navigation: 25px.</description></item>
/// </list>
/// Safety bail-out: if the candidate crop removes more than 50% of width or leaves less than
/// 30% of height, return the original frame unchanged and emit
/// <see cref="ReplayWarningCodes.CropBailOut"/>.
/// </remarks>
public sealed class SmartCropService : ISmartCropService
{
    public const double BrightnessThresholdDark = 25.0;
    public const double BrightnessThresholdControls = 100.0;
    public const int MaxControlsBarHeight = 80;
    public const int GalleryScanStartFraction = 90;
    public const int GalleryScanEndFraction = 60;
    public const double GalleryHitRatio = 0.6;
    public const int BottomTrimPixels = 25;
    public const double MinRetainedWidthFraction = 0.5;
    public const double MinRetainedHeightFraction = 0.3;

    public SmartCropOutcome Process(FrameArtifact frame, VideoRun run, string profile)
    {
        var sourcePath = run.GetPath(frame.Path);
        if (!File.Exists(sourcePath))
        {
            return new SmartCropOutcome(frame, Applied: false, Warning: new ReplayWarning(
                ReplayWarningCodes.CropImageDecodeFailed,
                $"Smart-crop could not read frame: file not found at '{sourcePath}'.",
                Source: "crop",
                Severity: ReplayWarningSeverities.Warning));
        }

        SKBitmap? bitmap;
        try
        {
            bitmap = SKBitmap.Decode(sourcePath);
        }
        catch (Exception ex)
        {
            return new SmartCropOutcome(frame, Applied: false, Warning: new ReplayWarning(
                ReplayWarningCodes.CropImageDecodeFailed,
                $"Smart-crop could not decode {frame.Path}: {ex.Message}",
                Source: "crop",
                Severity: ReplayWarningSeverities.Warning));
        }

        if (bitmap is null)
        {
            return new SmartCropOutcome(frame, Applied: false, Warning: new ReplayWarning(
                ReplayWarningCodes.CropImageDecodeFailed,
                $"Smart-crop could not decode {frame.Path}: SKBitmap.Decode returned null.",
                Source: "crop",
                Severity: ReplayWarningSeverities.Warning));
        }

        using (bitmap)
        {
            return ProcessDecoded(frame, run, profile, bitmap);
        }
    }

    private SmartCropOutcome ProcessDecoded(FrameArtifact frame, VideoRun run, string profile, SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var (cropLeft, cropTop, cropRight, cropBottom) = ComputeCropBox(bitmap, width, height);

        var cropWidth = cropRight - cropLeft;
        var cropHeight = cropBottom - cropTop;
        var noCropApplied = cropLeft == 0 && cropTop == 0 && cropRight == width && cropBottom == height;

        // Safety bail-out — overly aggressive crops are worse than no crop at all.
        if (cropWidth < width * MinRetainedWidthFraction || cropHeight < height * MinRetainedHeightFraction)
        {
            var bailedOut = !noCropApplied;
            var annotated = frame with { Width = width, Height = height };
            return new SmartCropOutcome(
                annotated,
                Applied: false,
                Warning: bailedOut
                    ? new ReplayWarning(
                        ReplayWarningCodes.CropBailOut,
                        $"Smart-crop bail-out for {frame.Id}: candidate crop {cropWidth}x{cropHeight} from {width}x{height} removed too much; original frame retained.",
                        Source: "crop",
                        Severity: ReplayWarningSeverities.Info)
                    : null);
        }

        // No crop needed — frame already tight. Still record source dimensions for downstream.
        if (noCropApplied)
        {
            return new SmartCropOutcome(
                frame with { Width = width, Height = height },
                Applied: false,
                Warning: null);
        }

        // Write the cropped variant alongside the original frame.
        var directory = Path.GetDirectoryName(frame.Path) ?? "frames";
        var basename = Path.GetFileNameWithoutExtension(frame.Path);
        var croppedRelative = $"{directory.Replace('\\', '/')}/{basename}-cropped.jpg";
        var croppedFullPath = run.GetPath(croppedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(croppedFullPath)!);

        try
        {
            using var cropped = new SKBitmap(cropWidth, cropHeight);
            var destination = new SKRectI(0, 0, cropWidth, cropHeight);
            var source = new SKRectI(cropLeft, cropTop, cropRight, cropBottom);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(bitmap, source, destination);
            }

            using var image = SKImage.FromBitmap(cropped);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 96);
            using var stream = File.Create(croppedFullPath);
            data.SaveTo(stream);
        }
        catch (Exception ex)
        {
            return new SmartCropOutcome(
                frame with { Width = width, Height = height },
                Applied: false,
                Warning: new ReplayWarning(
                    ReplayWarningCodes.CropOutputFailed,
                    $"Smart-crop encode failed for {frame.Id}: {ex.Message}",
                    Source: "crop",
                    Severity: ReplayWarningSeverities.Warning));
        }

        var box = new FrameCropBox(cropLeft, cropTop, cropWidth, cropHeight, $"smart-crop-{profile}");
        return new SmartCropOutcome(
            frame with
            {
                Path = croppedRelative,
                OriginalPath = frame.Path,
                Width = cropWidth,
                Height = cropHeight,
                Crop = box,
                // Drop any stale perceptual hash; the next stage recomputes it on the cropped image.
                PerceptualHash = null
            },
            Applied: true,
            Warning: null);
    }

    /// <summary>
    /// Pure pixel-sampling crop computation. Exposed (internal) so unit tests can drive the
    /// algorithm against synthetic <see cref="SKBitmap"/> fixtures without writing files.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) ComputeCropBox(SKBitmap bitmap, int width, int height)
    {
        // 1. Trim top letterbox.
        var top = 0;
        for (var y = 0; y < height / 3; y += 3)
        {
            if (CountBrightSamples(bitmap, y, width, samples: 10, BrightnessThresholdDark) >= 3)
            {
                top = y;
                break;
            }
        }

        // 2. Trim bottom letterbox.
        var bottom = height;
        for (var y = height - 1; y > height * 2 / 3; y -= 3)
        {
            if (CountBrightSamples(bitmap, y, width, samples: 10, BrightnessThresholdDark) >= 3)
            {
                bottom = y + 1;
                break;
            }
        }

        // 3. Trim controls bar: highest fully-bright row in the top MaxControlsBarHeight px.
        var controlsEnd = top;
        var horizontalSamples = Math.Max(1, width / 20);
        var requiredBrightSamples = Math.Max(1, horizontalSamples / 2);
        for (var y = top; y < Math.Min(top + MaxControlsBarHeight, height); y += 2)
        {
            if (CountBrightSamples(bitmap, y, width, samples: horizontalSamples, BrightnessThresholdControls) >= requiredBrightSamples)
            {
                controlsEnd = y + 2;
            }
        }
        if (controlsEnd > top)
        {
            top = Math.Min(controlsEnd + 5, height);
        }

        // 4. Trim participant gallery sidebar.
        var right = width;
        if (bottom - top > 100) // need enough vertical room to sample
        {
            var sampleYs = SampleRange(top + 50, bottom - 50, step: 30);
            if (sampleYs.Count > 0)
            {
                var xStart = width * GalleryScanStartFraction / 100;
                var xEnd = width * GalleryScanEndFraction / 100;
                for (var xCheck = xStart; xCheck > xEnd; xCheck -= 2)
                {
                    var hits = 0;
                    foreach (var ySample in sampleYs)
                    {
                        var brightness = SamplePixelBrightness(bitmap, xCheck, ySample);
                        var brightnessLeft = SamplePixelBrightness(bitmap, Math.Max(0, xCheck - 30), ySample);
                        if (brightness is > 40 and < 120
                            && brightnessLeft < 40
                            && brightness > brightnessLeft + 15)
                        {
                            hits++;
                        }
                    }

                    if (hits >= sampleYs.Count * GalleryHitRatio)
                    {
                        right = Math.Max(0, xCheck - 5);
                        break;
                    }
                }
            }
        }

        // 5. Trim bottom navigation bar (Teams/Zoom slide-control strip).
        bottom = Math.Max(top + 100, bottom - BottomTrimPixels);

        // Clamp into [0, width/height] to guarantee a valid rectangle.
        var left = Math.Clamp(0, 0, width);
        right = Math.Clamp(right, left, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, top, height);

        return (left, top, right, bottom);
    }

    private static int CountBrightSamples(SKBitmap bitmap, int y, int width, int samples, double threshold)
    {
        if (y < 0 || y >= bitmap.Height || width <= 0)
        {
            return 0;
        }

        var step = Math.Max(1, width / samples);
        var count = 0;
        for (var x = 0; x < width; x += step)
        {
            if (SamplePixelBrightness(bitmap, x, y) > threshold)
            {
                count++;
            }
        }

        return count;
    }

    private static double SamplePixelBrightness(SKBitmap bitmap, int x, int y)
    {
        if (x < 0 || x >= bitmap.Width || y < 0 || y >= bitmap.Height)
        {
            return 0;
        }

        var pixel = bitmap.GetPixel(x, y);
        return (pixel.Red + pixel.Green + pixel.Blue) / 3.0;
    }

    private static List<int> SampleRange(int start, int endExclusive, int step)
    {
        var values = new List<int>();
        if (step <= 0 || start >= endExclusive)
        {
            return values;
        }

        for (var v = start; v < endExclusive; v += step)
        {
            values.Add(v);
        }

        return values;
    }
}
