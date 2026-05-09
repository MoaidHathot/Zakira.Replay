using System.Globalization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface IFfmpegClient
{
    Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, CancellationToken cancellationToken);

    Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken);

    Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken);

    Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken);
}

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
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return [];
        }

        if (strategy.Equals(FrameSelectionStrategies.Scene, StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractSceneFramesAsync(mediaSource, run, count, cancellationToken).ConfigureAwait(false);
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
            var fileName = $"frame-{i:000}-{label.Replace(':', '-').Replace('.', '-')}.jpg";
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
                frames.Add(new FrameArtifact(relativePath.Replace('\\', '/'), timestamp, label));
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
            frames.Add(new FrameArtifact(Path.GetRelativePath(run.Directory, files[i]).Replace('\\', '/'), timestamp, label));
        }

        return frames;
    }

    private async Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesAsync(
        string mediaSource,
        VideoRun run,
        int count,
        CancellationToken cancellationToken)
    {
        var ffmpeg = dependencies.RequireFfmpeg("extracting scene-change frames from video media");
        var outputPattern = run.GetPath(Path.Combine("frames", "scene-%03d.jpg"));
        var result = await processRunner.RunAsync(
            ffmpeg,
            [
                "-hide_banner",
                "-i", mediaSource,
                "-vf", "select=gt(scene\\,0.35),showinfo",
                "-vsync", "vfr",
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-q:v", "2",
                "-y",
                outputPattern
            ],
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var timestamps = FfmpegOutputPatterns.ShowInfoTimeRegex().Matches(result.StandardError)
            .Select(match => double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var files = Directory.EnumerateFiles(run.GetPath("frames"), "scene-*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();

        var frames = new List<FrameArtifact>();
        for (var i = 0; i < files.Length; i++)
        {
            var timestamp = i < timestamps.Length ? timestamps[i] : i;
            var label = Timestamp.Format(timestamp);
            frames.Add(new FrameArtifact(Path.GetRelativePath(run.Directory, files[i]).Replace('\\', '/'), timestamp, label));
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
        return await ExtractFramesAsync(mediaSource, run, count, duration, FrameSelectionStrategies.Interval, cancellationToken).ConfigureAwait(false);
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
}

public static partial class FfmpegOutputPatterns
{
    [GeneratedRegex("pts_time:([0-9.]+)")]
    public static partial Regex ShowInfoTimeRegex();
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
