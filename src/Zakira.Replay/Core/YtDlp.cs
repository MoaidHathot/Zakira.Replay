using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface IYtDlpClient
{
    Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken);

    Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken);

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

    public async Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("extracting existing subtitles/captions");
        var languageSpec = FormatSubtitleLanguageSpec(subtitleLanguages);
        var outputTemplate = Path.Combine(run.Directory, "captions", "subtitle.%(ext)s");
        var args = CreateBaseArguments(request);
        args.AddRange([
            "--skip-download",
            "--write-subs",
            "--write-auto-subs",
            "--sub-langs", languageSpec,
            "--sub-format", "vtt/srt/best",
            "--no-playlist",
            "-o", outputTemplate,
            request.Source
        ]);
        await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // yt-dlp can exit non-zero when one of the requested languages is unavailable while still
        // writing the available languages' subtitle files. Trust the filesystem: if a .vtt/.srt
        // landed in captions/, use it.
        var captionsDirectory = Path.Combine(run.Directory, "captions");
        if (!Directory.Exists(captionsDirectory))
        {
            return null;
        }

        var subtitle = Directory.EnumerateFiles(captionsDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        if (subtitle is null)
        {
            return null;
        }

        var segments = await SubtitleConverter.ParseSegmentsAsync(subtitle, cancellationToken).ConfigureAwait(false);
        var markdownPath = run.GetPath("transcript.md");
        await File.WriteAllTextAsync(markdownPath, SubtitleConverter.ToMarkdown(segments), cancellationToken).ConfigureAwait(false);

        return new TranscriptArtifact(subtitle, markdownPath, "yt-dlp-subtitle", segments);
    }

    private static string FormatSubtitleLanguageSpec(IReadOnlyList<string> subtitleLanguages)
    {
        if (subtitleLanguages.Count == 0)
        {
            return "en.*,en,live_chat";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();
        foreach (var rawLanguage in subtitleLanguages)
        {
            if (string.IsNullOrWhiteSpace(rawLanguage))
            {
                continue;
            }

            var language = rawLanguage.Trim();
            if (language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(language))
            {
                entries.Add(language);
            }
        }

        if (entries.Count == 0)
        {
            return "en.*,en,live_chat";
        }

        return string.Join(',', entries);
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

public sealed record TranscriptArtifact(
    string SourcePath,
    string MarkdownPath,
    string Kind,
    IReadOnlyList<TranscriptSegment>? Segments = null,
    IReadOnlyList<SecondaryTranscriptArtifact>? Secondary = null);

/// <summary>
/// A non-primary-language transcript persisted alongside <c>transcript.md</c>. Emitted only when
/// the caller opts in via <see cref="AnalyzeRequest.SecondaryCaptionLanguages"/> (default: none)
/// and a matching caption was downloaded by the browser-capture or yt-dlp paths. The file is
/// always written as <c>transcript.&lt;language&gt;.md</c>; the source caption path is
/// preserved for traceability.
/// </summary>
public sealed record SecondaryTranscriptArtifact(
    string Language,
    string MarkdownPath,
    string SourcePath);

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

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Manual subtitle tracks per language code, as advertised by yt-dlp.
    /// Keys are BCP-47-style language codes; values are arrays of available format descriptors.
    /// </summary>
    [JsonPropertyName("subtitles")]
    public Dictionary<string, JsonElement>? Subtitles { get; set; }

    /// <summary>
    /// Auto-generated/captioned subtitle tracks per language code, as advertised by yt-dlp.
    /// </summary>
    [JsonPropertyName("automatic_captions")]
    public Dictionary<string, JsonElement>? AutomaticCaptions { get; set; }

    /// <summary>
    /// Derived per-language summary written to <c>metadata.json</c>: which languages have manual
    /// subtitles, automatic captions, or both. Set by <see cref="AnalysisPipeline"/> after metadata
    /// is fetched; ignored when deserializing yt-dlp output.
    /// </summary>
    [JsonPropertyName("availableSubtitleLanguages")]
    public Dictionary<string, AvailableSubtitleLanguage>? AvailableSubtitleLanguages { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AvailableSubtitleLanguage
{
    [JsonPropertyName("hasManual")]
    public bool HasManual { get; set; }

    [JsonPropertyName("hasAuto")]
    public bool HasAuto { get; set; }
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
    /// <summary>
    /// Parses a VTT or SRT file into structured transcript segments, attaching <c>SpeakerId</c>
    /// and <c>SpeakerDisplayName</c> when the source carries speaker tags. Recognises VTT voice
    /// spans (<c>&lt;v Speaker Name&gt;...&lt;/v&gt;</c> or self-terminating <c>&lt;v Speaker Name&gt;</c>),
    /// SRT line prefixes (<c>Speaker Name: utterance</c>), and bracketed prefixes
    /// (<c>[Speaker Name] utterance</c>). Lines without an attributable speaker carry <c>null</c>.
    /// </summary>
    public static async Task<IReadOnlyList<TranscriptSegment>> ParseSegmentsAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseSegments(text);
    }

    public static IReadOnlyList<TranscriptSegment> ParseSegments(string text)
    {
        var blocks = Regex.Split(text.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n+");
        var segments = new List<TranscriptSegment>();
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
            var startTimestamp = timingParts[0];
            var endTimestamp = timingParts.Length > 1 ? timingParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0] : null;

            string? speakerDisplayName = null;
            var spokenLines = new List<string>();
            foreach (var line in lines.Where(line => !line.Contains("-->", StringComparison.Ordinal)))
            {
                var (lineSpeaker, cleaned) = ExtractSpeakerAndCleanLine(line);
                if (lineSpeaker is not null && speakerDisplayName is null)
                {
                    speakerDisplayName = lineSpeaker;
                }

                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    spokenLines.Add(cleaned);
                }
            }

            var spoken = WhitespaceRegex().Replace(string.Join(' ', spokenLines), " ").Trim();
            if (spoken.Length == 0)
            {
                continue;
            }

            var dedupeKey = $"{startTimestamp}|{speakerDisplayName ?? string.Empty}|{spoken}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var startSeconds = ParseTimestampSeconds(startTimestamp);
            var endSeconds = endTimestamp is null ? null : ParseTimestampSeconds(endTimestamp);
            var timestamp = endTimestamp is null ? startTimestamp : $"{startTimestamp} - {endTimestamp}";
            segments.Add(new TranscriptSegment(
                StartSeconds: startSeconds,
                EndSeconds: endSeconds,
                Timestamp: timestamp,
                Text: spoken,
                SpeakerId: NormalizeSpeakerId(speakerDisplayName),
                SpeakerDisplayName: speakerDisplayName));
        }

        return segments;
    }

    public static async Task<string> ToMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        var segments = await ParseSegmentsAsync(path, cancellationToken).ConfigureAwait(false);
        return ToMarkdown(segments);
    }

    public static string ToMarkdown(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            builder.Append("**[").Append(segment.Timestamp ?? string.Empty).Append("]** ");
            if (!string.IsNullOrWhiteSpace(segment.SpeakerDisplayName))
            {
                builder.Append('[').Append(segment.SpeakerDisplayName).Append("] ");
            }

            builder.AppendLine(segment.Text);
        }

        return builder.ToString();
    }

    private static (string? SpeakerDisplayName, string CleanedText) ExtractSpeakerAndCleanLine(string line)
    {
        // Try VTT voice tag first: <v Speaker Name>text</v> or self-terminating <v Speaker Name>
        var voiceMatch = VttVoiceRegex().Match(line);
        if (voiceMatch.Success)
        {
            var name = voiceMatch.Groups[1].Value.Trim();
            var residual = (voiceMatch.Groups[2].Value + line[(voiceMatch.Index + voiceMatch.Length)..]).Trim();
            var cleaned = CleanSubtitleLine(residual);
            return (string.IsNullOrWhiteSpace(name) ? null : name, cleaned);
        }

        var cleanedLine = CleanSubtitleLine(line);

        // Bracketed speaker: [Speaker Name] text
        var bracketMatch = BracketSpeakerRegex().Match(cleanedLine);
        if (bracketMatch.Success)
        {
            var name = bracketMatch.Groups[1].Value.Trim();
            return (string.IsNullOrWhiteSpace(name) ? null : name, bracketMatch.Groups[2].Value.Trim());
        }

        // Colon-separated speaker prefix: Speaker Name: text. Limited to short, name-shaped prefixes
        // to avoid swallowing punctuation like time mentions ("at 12:00") or sentences with colons.
        var colonMatch = ColonSpeakerRegex().Match(cleanedLine);
        if (colonMatch.Success && IsLikelySpeakerLabel(colonMatch.Groups[1].Value))
        {
            return (colonMatch.Groups[1].Value.Trim(), colonMatch.Groups[2].Value.Trim());
        }

        return (null, cleanedLine);
    }

    /// <summary>
    /// Heuristic: a speaker label is a short string (≤ 60 chars) made of letters, digits, spaces,
    /// hyphens, and apostrophes; not all-numeric, not a single character.
    /// </summary>
    private static bool IsLikelySpeakerLabel(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length is 0 or > 60)
        {
            return false;
        }

        if (trimmed.All(char.IsDigit))
        {
            return false;
        }

        return SpeakerLabelShapeRegex().IsMatch(trimmed);
    }

    private static string? NormalizeSpeakerId(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var slug = Slug.Create(displayName, 80);
        return string.IsNullOrEmpty(slug) ? null : slug;
    }

    private static double? ParseTimestampSeconds(string timestamp)
    {
        var normalized = timestamp.Trim().Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
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

    [GeneratedRegex("<v(?:\\.[^\\s>]*)?\\s+([^>]+)>([^<]*)(?:</v>)?", RegexOptions.IgnoreCase)]
    private static partial Regex VttVoiceRegex();

    [GeneratedRegex("^\\[([^\\]]{1,60})\\]\\s*(.+)$")]
    private static partial Regex BracketSpeakerRegex();

    [GeneratedRegex("^([\\p{L}\\p{N}][\\p{L}\\p{N} '\\-\\.]{0,58}[\\p{L}\\p{N}\\.])\\s*:\\s+(.+)$")]
    private static partial Regex ColonSpeakerRegex();

    [GeneratedRegex("^[\\p{L}\\p{N}][\\p{L}\\p{N} '\\-\\.]*[\\p{L}\\p{N}\\.]$")]
    private static partial Regex SpeakerLabelShapeRegex();
}
