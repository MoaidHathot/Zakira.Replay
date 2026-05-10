using System.Globalization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface IFfmpegClient
{
    Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken);

    Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken);

    Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken);

    Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken);

    Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken);

    Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Computes a 64-bit perceptual difference hash (dHash) for a still image and returns it as
    /// a 16-character lowercase hex string. The image is downscaled to 9x8 grayscale by ffmpeg
    /// itself; no managed image library is required. Returns <c>null</c> when the hash cannot
    /// be computed (e.g. the image is unreadable).
    /// </summary>
    Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken);
}

/// <summary>
/// A contiguous span of audio considered silence by ffmpeg's <c>silencedetect</c> filter.
/// </summary>
public sealed record SilenceWindow(double StartSeconds, double EndSeconds, double DurationSeconds);

/// <summary>
/// Tunables for ffmpeg's <c>silencedetect</c> filter.
/// </summary>
/// <param name="NoiseDb">Noise threshold below which audio is considered silent. Defaults to -30 dB.</param>
/// <param name="MinSilenceSeconds">Minimum contiguous silence duration before a window is reported. Defaults to 0.5 s.</param>
public sealed record SilenceDetectionOptions(double NoiseDb = -30, double MinSilenceSeconds = 0.5);

public sealed class FfmpegClient : IFfmpegClient
{
    private readonly DependencyResolver dependencies;
    private readonly ProcessRunner processRunner;

    public FfmpegClient(DependencyResolver dependencies, ProcessRunner processRunner)
    {
        this.dependencies = dependencies;
        this.processRunner = processRunner;
    }

    public async Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(
        string mediaSource,
        VideoRun run,
        int count,
        double? durationSeconds,
        string strategy,
        int sceneSafetyCap,
        CancellationToken cancellationToken)
    {
        if (count <= 0 && !strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractSceneFramesAsync(mediaSource, run, sceneSafetyCap, cancellationToken).ConfigureAwait(false);
        }

        if (strategy.Equals(FrameSelectionStrategies.EveryFrame, StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractEveryFrameAsync(mediaSource, run, count, cancellationToken).ConfigureAwait(false);
        }

        var ffmpeg = dependencies.RequireFfmpeg("extracting frames from video media");
        var duration = durationSeconds ?? await TryProbeDurationAsync(mediaSource, cancellationToken).ConfigureAwait(false);
        if (duration is null || duration <= 1)
        {
            throw new ReplayException("Cannot extract timestamped frames because video duration is unknown.");
        }

        var frames = new List<FrameArtifact>();
        for (var i = 1; i <= count; i++)
        {
            var timestamp = duration.Value * i / (count + 1);
            var label = Timestamp.Format(timestamp);
            var frameId = $"frame-{i:000}";
            var fileName = $"{frameId}-{label.Replace(':', '-').Replace('.', '-')}.jpg";
            var relativePath = Path.Combine("frames", fileName);
            var outputPath = run.GetPath(relativePath);

            var result = await processRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", mediaSource,
                    "-frames:v", "1",
                    "-q:v", "2",
                    "-y",
                    outputPath
                ],
                timeout: TimeSpan.FromMinutes(3),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0 && File.Exists(outputPath))
            {
                frames.Add(new FrameArtifact(frameId, relativePath.Replace('\\', '/'), timestamp, label));
            }
        }

        return frames;
    }

    private async Task<IReadOnlyList<FrameArtifact>> ExtractEveryFrameAsync(
        string mediaSource,
        VideoRun run,
        int count,
        CancellationToken cancellationToken)
    {
        var ffmpeg = dependencies.RequireFfmpeg("extracting every frame from video media");
        var outputPattern = run.GetPath(Path.Combine("frames", "frame-%06d.jpg"));
        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-i", mediaSource,
                "-vf", "showinfo",
                "-vsync", "0",
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-q:v", "2",
                "-y",
                outputPattern
            ],
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            result.EnsureSuccess();
        }

        var timestamps = FfmpegOutputPatterns.ShowInfoTimeRegex().Matches(result.StandardError)
            .Select(match => double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var files = Directory.EnumerateFiles(run.GetPath("frames"), "frame-*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();

        var frames = new List<FrameArtifact>();
        for (var i = 0; i < files.Length; i++)
        {
            var timestamp = i < timestamps.Length ? timestamps[i] : i;
            var label = Timestamp.Format(timestamp);
            var frameId = $"frame-{i + 1:000}";
            frames.Add(new FrameArtifact(frameId, Path.GetRelativePath(run.Directory, files[i]).Replace('\\', '/'), timestamp, label));
        }

        return frames;
    }

    private async Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesAsync(
        string mediaSource,
        VideoRun run,
        int sceneSafetyCap,
        CancellationToken cancellationToken)
    {
        var safetyCap = Math.Max(1, sceneSafetyCap);
        var ffmpeg = dependencies.RequireFfmpeg("extracting scene-change frames from video media");
        var outputPattern = run.GetPath(Path.Combine("frames", "scene-%04d.jpg"));
        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-i", mediaSource,
                "-vf", "select=gt(scene\\,0.35),showinfo",
                "-vsync", "vfr",
                "-frames:v", safetyCap.ToString(CultureInfo.InvariantCulture),
                "-q:v", "2",
                "-y",
                outputPattern
            ],
            timeout: TimeSpan.FromMinutes(15),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var timestamps = FfmpegOutputPatterns.ShowInfoTimeRegex().Matches(result.StandardError)
            .Select(match => double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var files = Directory.EnumerateFiles(run.GetPath("frames"), "scene-*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(safetyCap)
            .ToArray();

        var frames = new List<FrameArtifact>();
        for (var i = 0; i < files.Length; i++)
        {
            var timestamp = i < timestamps.Length ? timestamps[i] : i;
            var label = Timestamp.Format(timestamp);
            var frameId = $"scene-{i + 1:0000}";
            frames.Add(new FrameArtifact(frameId, Path.GetRelativePath(run.Directory, files[i]).Replace('\\', '/'), timestamp, label));
        }

        if (frames.Count > 0)
        {
            return frames;
        }

        if (result.ExitCode != 0 && !result.StandardError.Contains("No filtered frames", StringComparison.OrdinalIgnoreCase))
        {
            result.EnsureSuccess();
        }

        var duration = await TryProbeDurationAsync(mediaSource, cancellationToken).ConfigureAwait(false);
        // No scene cuts detected; fall back to a small interval sample so the orchestrator still has frames to inspect.
        return await ExtractFramesAsync(mediaSource, run, count: 7, duration, FrameSelectionStrategies.Interval, sceneSafetyCap, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
    {
        var ffmpeg = dependencies.RequireFfmpeg("extracting audio from video media");
        var relativePath = Path.Combine("audio", "audio.wav");
        var outputPath = run.GetPath(relativePath);

        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-i", mediaSource,
                "-vn",
                "-ac", "1",
                "-ar", "16000",
                "-y",
                outputPath
            ],
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        result.EnsureSuccess();
        return relativePath.Replace('\\', '/');
    }

    public async Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
    {
        if (end <= start)
        {
            throw new ReplayException("Clip end timestamp must be after start timestamp.");
        }

        var ffmpeg = dependencies.RequireFfmpeg("extracting clips from video media");
        var fileName = string.IsNullOrWhiteSpace(outputName)
            ? $"clip-{Timestamp.FileSafe(start.TotalSeconds)}-{Timestamp.FileSafe(end.TotalSeconds)}.mp4"
            : FfmpegFileNames.EnsureMp4Extension(Slug.Create(outputName, 80));
        var relativePath = Path.Combine("clips", fileName);
        var outputPath = run.GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-to", end.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", mediaSource,
                "-map", "0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-y",
                outputPath
            ],
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            result = await processRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-to", end.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", mediaSource,
                    "-c:v", "libx264",
                    "-c:a", "aac",
                    "-movflags", "+faststart",
                    "-y",
                    outputPath
                ],
                timeout: TimeSpan.FromMinutes(20),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            result.EnsureSuccess();
        }

        return relativePath.Replace('\\', '/');
    }

    public async Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
    {
        var ffprobe = dependencies.RequireFfprobe("probing video duration for frame extraction");
        var result = await processRunner.RunAsync(
            ffprobe,
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                mediaSource
            ],
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : null;
    }

    public async Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
    {
        var ffmpeg = dependencies.RequireFfmpeg("detecting silence boundaries for audio chunking");
        var noise = options.NoiseDb.ToString("0.###", CultureInfo.InvariantCulture);
        var minSilence = options.MinSilenceSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-nostats",
                "-i", mediaSource,
                "-af", $"silencedetect=noise={noise}dB:d={minSilence}",
                "-f", "null",
                "-"
            ],
            timeout: TimeSpan.FromMinutes(15),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return [];
        }

        return ParseSilenceWindows(result.StandardError);
    }

    public async Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ReplayException($"Audio range duration must be positive: {duration}.");
        }

        var ffmpeg = dependencies.RequireFfmpeg("extracting audio range for chunked transcription");
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", mediaSource,
                "-vn",
                "-ac", "1",
                "-ar", "16000",
                "-y",
                outputPath
            ],
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        result.EnsureSuccess();
    }

    public async Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        var ffmpeg = dependencies.RequireFfmpeg("computing perceptual hashes for slide grouping");
        var tempPath = Path.Combine(Path.GetTempPath(), $"zakira-replay-dhash-{Guid.NewGuid():N}.bin");
        try
        {
            var result = await processRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-i", imagePath,
                    "-vf", "scale=9:8,format=gray",
                    "-f", "rawvideo",
                    "-y",
                    tempPath
                ],
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(tempPath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            return ComputeDifferenceHash(bytes);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup; ignore filesystem races.
            }
        }
    }

    /// <summary>
    /// dHash from a 9x8 grayscale byte buffer (72 bytes). For each row, compares each pixel with
    /// the next column and emits a bit (1 if left > right, else 0). 8 rows × 8 comparisons = 64 bits.
    /// Returned as a 16-character lowercase hex string.
    /// </summary>
    internal static string? ComputeDifferenceHash(byte[] grayscale9x8)
    {
        if (grayscale9x8.Length < 72)
        {
            return null;
        }

        ulong hash = 0;
        var bit = 0;
        for (var row = 0; row < 8; row++)
        {
            var rowOffset = row * 9;
            for (var column = 0; column < 8; column++)
            {
                var left = grayscale9x8[rowOffset + column];
                var right = grayscale9x8[rowOffset + column + 1];
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<SilenceWindow> ParseSilenceWindows(string ffmpegStderr)
    {
        var windows = new List<SilenceWindow>();
        double? pendingStart = null;
        foreach (Match entry in FfmpegOutputPatterns.SilenceLineRegex().Matches(ffmpegStderr))
        {
            var kind = entry.Groups[1].Value;
            if (!double.TryParse(entry.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            if (kind.Equals("silence_start", StringComparison.Ordinal))
            {
                pendingStart = seconds;
            }
            else if (kind.Equals("silence_end", StringComparison.Ordinal) && pendingStart is not null)
            {
                var duration = entry.Groups[3].Success && double.TryParse(entry.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)
                    ? parsedDuration
                    : Math.Max(0, seconds - pendingStart.Value);
                windows.Add(new SilenceWindow(pendingStart.Value, seconds, duration));
                pendingStart = null;
            }
        }

        return windows;
    }
}

public static partial class FfmpegOutputPatterns
{
    [GeneratedRegex("pts_time:([0-9.]+)")]
    public static partial Regex ShowInfoTimeRegex();

    [GeneratedRegex("(silence_start|silence_end): ([0-9.]+)(?: \\| silence_duration: ([0-9.]+))?")]
    public static partial Regex SilenceLineRegex();
}

public static class Timestamp
{
    public static string Format(double seconds)
    {
        var timeSpan = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return timeSpan.TotalHours >= 1
            ? $"{(int)timeSpan.TotalHours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}"
            : $"{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
    }

    public static string FileSafe(double seconds)
    {
        return Format(seconds).Replace(':', '-').Replace('.', '-');
    }

    public static TimeSpan ParseRequired(string value, string name)
    {
        var normalized = value.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            throw new ReplayException($"Invalid {name} timestamp: {value}. Use seconds, MM:SS, or HH:MM:SS.");
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            || !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            throw new ReplayException($"Invalid {name} timestamp: {value}. Use seconds, MM:SS, or HH:MM:SS.");
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            throw new ReplayException($"Invalid {name} timestamp: {value}. Use seconds, MM:SS, or HH:MM:SS.");
        }

        return TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + parsedSeconds);
    }
}

internal static class FfmpegFileNames
{
    public static string EnsureMp4Extension(string value)
    {
        return value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? value : value + ".mp4";
    }
}
