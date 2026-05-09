using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface IYtDlpClient
{
    Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken);

    Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken);

    Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken);

    Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken);
}

public sealed class YtDlpClient : IYtDlpClient
{
    private readonly DependencyResolver dependencies;
    private readonly ProcessRunner processRunner;

    public YtDlpClient(DependencyResolver dependencies, ProcessRunner processRunner)
    {
        this.dependencies = dependencies;
        this.processRunner = processRunner;
    }

    public async Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("extracting media metadata and caption information");
        var args = CreateBaseArguments(request);
        args.AddRange(["--dump-single-json", "--no-warnings", "--no-playlist", request.Source]);
        var result = await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ProcessFailedException(ytDlp, result.Arguments, result.ExitCode, result.StandardError + result.StandardOutput);
        }

        try
        {
            var info = JsonSerializer.Deserialize<YtDlpInfo>(result.StandardOutput, YtDlpJson.Options);
            return info ?? throw new ReplayException("yt-dlp returned empty metadata.");
        }
        catch (JsonException ex)
        {
            throw new ReplayException("Failed to parse yt-dlp metadata JSON.", ex);
        }
    }

    public async Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("extracting existing subtitles/captions");
        var outputTemplate = Path.Combine(run.Directory, "captions", "subtitle.%(ext)s");
        var args = CreateBaseArguments(request);
        args.AddRange([
            "--skip-download",
            "--write-subs",
            "--write-auto-subs",
            "--sub-langs", "en.*,en,live_chat",
            "--sub-format", "vtt/srt/best",
            "--no-playlist",
            "-o", outputTemplate,
            request.Source
        ]);
        var result = await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var subtitle = Directory.EnumerateFiles(Path.Combine(run.Directory, "captions"), "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        if (subtitle is null)
        {
            return null;
        }

        var markdown = await SubtitleConverter.ToMarkdownAsync(subtitle, cancellationToken).ConfigureAwait(false);
        var markdownPath = run.GetPath("transcript.md");
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken).ConfigureAwait(false);

        return new TranscriptArtifact(subtitle, markdownPath, "yt-dlp-subtitle");
    }

    public async Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("resolving direct media URL for ffmpeg");
        var args = CreateBaseArguments(request);
        args.AddRange([
            "--no-playlist",
            "-f", "best[protocol=https][vcodec!=none][acodec!=none][ext=mp4]/best[protocol=https][vcodec!=none][acodec!=none]/best[protocol=https][vcodec!=none][ext=mp4]/best[protocol=https][vcodec!=none]/bv*+ba/b",
            "-g", request.Source
        ]);
        var result = await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    public async Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("downloading media for local ffmpeg processing");
        var mediaDirectory = run.GetPath("media");
        Directory.CreateDirectory(mediaDirectory);

        var outputTemplate = Path.Combine(mediaDirectory, "source.%(ext)s");
        var args = CreateBaseArguments(request);
        args.AddRange([
            "--no-playlist",
            "-f", "best[height<=720][ext=mp4]/best[height<=720]/best[ext=mp4]/best",
            "--merge-output-format", "mp4",
            "-o", outputTemplate,
            request.Source
        ]);
        var result = await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeSpan.FromMinutes(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        return Directory.EnumerateFiles(mediaDirectory, "source.*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static List<string> CreateBaseArguments(AnalyzeRequest request)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.CookiesPath))
        {
            args.AddRange(["--cookies", request.CookiesPath]);
        }

        if (!string.IsNullOrWhiteSpace(request.CookiesFromBrowser))
        {
            args.AddRange(["--cookies-from-browser", request.CookiesFromBrowser]);
        }

        return args;
    }
}

public sealed record TranscriptArtifact(string SourcePath, string MarkdownPath, string Kind);

public sealed class YtDlpInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("webpage_url")]
    public string? WebpageUrl { get; set; }

    [JsonPropertyName("duration")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("uploader")]
    public string? Uploader { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal static class YtDlpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public static partial class SubtitleConverter
{
    public static async Task<string> ToMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var blocks = Regex.Split(text.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n+");
        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                .Where(line => !line.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase))
                .Where(line => !line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
                .Where(line => !NumberOnlyRegex().IsMatch(line))
                .ToArray();

            var timing = lines.FirstOrDefault(line => line.Contains("-->", StringComparison.Ordinal));
            if (timing is null)
            {
                continue;
            }

            var timingParts = timing.Split("-->", StringSplitOptions.TrimEntries);
            var timestamp = timingParts[0];
            var endTimestamp = timingParts.Length > 1 ? timingParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0] : null;
            var spoken = string.Join(' ', lines.Where(line => !line.Contains("-->", StringComparison.Ordinal))
                .Select(CleanSubtitleLine)
                .Where(line => !string.IsNullOrWhiteSpace(line)));

            spoken = WhitespaceRegex().Replace(spoken, " ").Trim();
            if (spoken.Length == 0 || !seen.Add($"{timestamp}|{spoken}"))
            {
                continue;
            }

            builder.Append("**[").Append(timestamp);
            if (!string.IsNullOrWhiteSpace(endTimestamp))
            {
                builder.Append(" - ").Append(endTimestamp);
            }

            builder.Append("]** ").AppendLine(spoken);
        }

        return builder.ToString();
    }

    private static string CleanSubtitleLine(string line)
    {
        var cleaned = HtmlTagRegex().Replace(line, string.Empty);
        cleaned = cleaned.Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
        return cleaned;
    }

    [GeneratedRegex("^\\d+$")]
    private static partial Regex NumberOnlyRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
