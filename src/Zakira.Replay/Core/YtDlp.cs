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

    /// <summary>
    /// Same as <see cref="DownloadBestSubtitleAsync(AnalyzeRequest, VideoRun, IReadOnlyList{string}, CancellationToken)"/>
    /// but reuses a saved <c>--dump-single-json</c> dump (<paramref name="infoJsonPath"/>) via
    /// <c>--load-info-json</c> so yt-dlp does not run its extractor a second time. Falls back to
    /// the URL path when the dump is missing, stale, or rejected.
    /// </summary>
    Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, string? infoJsonPath, CancellationToken cancellationToken)
        => DownloadBestSubtitleAsync(request, run, subtitleLanguages, cancellationToken);

    /// <summary>Direct media URL resolution that reuses a saved info dump when available (see <see cref="DownloadBestSubtitleAsync(AnalyzeRequest, VideoRun, IReadOnlyList{string}, string?, CancellationToken)"/>).</summary>
    Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, string? infoJsonPath, CancellationToken cancellationToken)
        => GetBestMediaUrlAsync(request, cancellationToken);

    /// <summary>Media download that reuses a saved info dump when available.</summary>
    Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, string? infoJsonPath, CancellationToken cancellationToken)
        => DownloadMediaForProcessingAsync(request, run, cancellationToken);
}

/// <summary>
/// Helpers around the saved <c>yt-dlp --dump-single-json</c> dump (<c>yt-dlp.info.json</c> in the
/// run directory). The dump carries every signed format URL and caption URL yt-dlp resolved, so
/// later steps can hand it back with <c>--load-info-json</c> instead of re-running the extractor
/// (three full extractions per analyze before this - and each one a fresh chance for a
/// YouTube bot check or rate limit).
/// </summary>
public static partial class YtDlpInfoJson
{
    public const string FileName = "yt-dlp.info.json";

    /// <summary>Safety margin before the earliest signed-URL expiry at which the dump is considered stale.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    /// <summary>Returns the dump path for a run when the file exists, otherwise null.</summary>
    public static string? Locate(VideoRun run)
    {
        var path = run.GetPath(FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// True when the dump can still be handed to <c>--load-info-json</c>: it parses, and none of
    /// the signed URLs it carries (<c>expire=</c> query parameter on googlevideo / timedtext
    /// URLs) has passed. Dumps without any expiry information are treated as fresh.
    /// </summary>
    public static bool IsFresh(string path, DateTimeOffset? now = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var earliest = EarliestExpiry(document.RootElement);
            return earliest is null || (now ?? DateTimeOffset.UtcNow) + ExpiryMargin < earliest.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Earliest <c>expire=</c> timestamp found on format/caption URLs, or null when none carries one.</summary>
    public static DateTimeOffset? EarliestExpiry(JsonElement root)
    {
        DateTimeOffset? earliest = null;
        void Consider(string? url)
        {
            if (TryParseExpiry(url) is { } expiry && (earliest is null || expiry < earliest.Value))
            {
                earliest = expiry;
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("url", out var topUrl) && topUrl.ValueKind == JsonValueKind.String)
        {
            Consider(topUrl.GetString());
        }

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    Consider(url.GetString());
                }
            }
        }

        foreach (var captionsKey in new[] { "subtitles", "automatic_captions" })
        {
            if (!root.TryGetProperty(captionsKey, out var captions) || captions.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var language in captions.EnumerateObject())
            {
                if (language.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var track in language.Value.EnumerateArray())
                {
                    if (track.ValueKind == JsonValueKind.Object && track.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        Consider(url.GetString());
                    }
                }
            }
        }

        return earliest;
    }

    internal static DateTimeOffset? TryParseExpiry(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var match = ExpiryRegex().Match(url);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        // Guard against nonsense values (some CDNs use "expire" for a relative TTL).
        return seconds is > 1_000_000_000 and < 100_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    [GeneratedRegex(@"[?&/](?:expire|expires|exp)[=/](\d{9,12})(?:[&/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiryRegex();
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
        args.AddRange(["--dump-single-json", "--no-warnings", "--no-playlist"]);
        if (CommentsModes.ToExtractorArguments(request.Comments) is { } commentArguments)
        {
            // Comments are only fetched by the extractor when asked; capping them keeps the
            // probe fast (the "top" preset finishes in a few seconds on YouTube).
            args.AddRange(["--write-comments", "--extractor-args", commentArguments]);
        }
        args.Add(request.Source);
        var result = await processRunner.RunAsync(
            ytDlp,
            args,
            timeout: TimeoutsConfig.Resolve(dependencies.Timeouts.YtDlpMetadataSeconds, 300),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ProcessFailedException(ytDlp, result.Arguments, result.ExitCode, result.StandardError + result.StandardOutput);
        }

        try
        {
            var info = JsonSerializer.Deserialize<YtDlpInfo>(result.StandardOutput, YtDlpJson.Options);
            if (info is null)
            {
                throw new ReplayException("yt-dlp returned empty metadata.");
            }

            // Keep the verbatim dump: the pipeline persists it as yt-dlp.info.json so the
            // subtitle / media-URL / download steps can reuse it via --load-info-json.
            info.RawJson = result.StandardOutput.Trim();
            return info;
        }
        catch (JsonException ex)
        {
            throw new ReplayException("Failed to parse yt-dlp metadata JSON.", ex);
        }
    }

    public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken)
        => DownloadBestSubtitleAsync(request, run, subtitleLanguages, infoJsonPath: null, cancellationToken);

    public async Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, string? infoJsonPath, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("extracting existing subtitles/captions");
        var languageSpec = FormatSubtitleLanguageSpec(subtitleLanguages);
        var outputTemplate = Path.Combine(run.Directory, "captions", "subtitle.%(ext)s");

        var usableInfoJson = ResolveUsableInfoJson(infoJsonPath);
        foreach (var useInfoJson in usableInfoJson is null ? [false] : new[] { true, false })
        {
            var args = CreateBaseArguments(request);
            args.AddRange([
                "--skip-download",
                "--write-subs",
                "--write-auto-subs",
                "--sub-langs", languageSpec,
                "--sub-format", "vtt/srt/best"
            ]);
            if (!useInfoJson)
            {
                args.Add("--no-playlist");
            }
            args.AddRange(["-o", outputTemplate]);
            args.AddRange(useInfoJson ? ["--load-info-json", usableInfoJson!] : [request.Source]);
            await processRunner.RunAsync(
                ytDlp,
                args,
                timeout: TimeoutsConfig.Resolve(dependencies.Timeouts.YtDlpSubtitlesSeconds, 600),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var transcript = await TryBuildTranscriptFromCaptionsDirectoryAsync(run, cancellationToken).ConfigureAwait(false);
            if (transcript is not null)
            {
                return transcript;
            }

            // Nothing landed from the saved dump: retry once with a live extraction in case the
            // dump was rejected (yt-dlp version skew) or its caption URLs expired.
        }

        return null;
    }

    private static async Task<TranscriptArtifact?> TryBuildTranscriptFromCaptionsDirectoryAsync(VideoRun run, CancellationToken cancellationToken)
    {
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

    /// <summary>A saved dump is only handed to yt-dlp when it exists and its signed URLs have not expired.</summary>
    private static string? ResolveUsableInfoJson(string? infoJsonPath)
    {
        if (string.IsNullOrWhiteSpace(infoJsonPath) || !File.Exists(infoJsonPath))
        {
            return null;
        }

        return YtDlpInfoJson.IsFresh(infoJsonPath) ? infoJsonPath : null;
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

    /// <summary>
    /// yt-dlp format selector used by <see cref="GetBestMediaUrlAsync"/> to resolve the direct
    /// media URL that ffmpeg reads (frames, audio, clips). Branches, in order:
    /// <list type="number">
    ///   <item>a progressive https stream (video + audio in one file), mp4 preferred - the only
    ///   branch that also serves audio extraction, kept first so STT keeps working when such a
    ///   stream exists;</item>
    ///   <item>the best H.264 video-only https stream up to 720p, then any codec up to 720p -
    ///   frames are downscaled to at most 1280 px anyway, and H.264 decodes far faster than AV1/VP9
    ///   while keeping the stream (and every whole-stream pass such as scene detection) small;</item>
    ///   <item>the previous fallbacks: best video-only https stream at any resolution, then a
    ///   merged pair, then anything.</item>
    /// </list>
    /// Without the 720p branch YouTube's video-only listing resolves to its highest rendition
    /// (e.g. itag 401, 4K AV1), which is pointless for stills and made the remote reads ffmpeg
    /// performs on it painfully slow.
    /// </summary>
    internal const string DirectMediaUrlFormatSelector =
        "best[protocol=https][vcodec!=none][acodec!=none][ext=mp4]"
        + "/best[protocol=https][vcodec!=none][acodec!=none]"
        + "/bestvideo[protocol=https][vcodec^=avc1][height<=720][ext=mp4]"
        + "/bestvideo[protocol=https][height<=720][ext=mp4]"
        + "/best[protocol=https][vcodec!=none][ext=mp4]"
        + "/best[protocol=https][vcodec!=none]"
        + "/bv*+ba/b";

    public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        => GetBestMediaUrlAsync(request, infoJsonPath: null, cancellationToken);

    public async Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, string? infoJsonPath, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("resolving direct media URL for ffmpeg");
        var usableInfoJson = ResolveUsableInfoJson(infoJsonPath);
        foreach (var useInfoJson in usableInfoJson is null ? [false] : new[] { true, false })
        {
            var args = CreateBaseArguments(request);
            if (!useInfoJson)
            {
                args.Add("--no-playlist");
            }
            args.AddRange(["-f", DirectMediaUrlFormatSelector, "-g"]);
            args.AddRange(useInfoJson ? ["--load-info-json", usableInfoJson!] : [request.Source]);
            var result = await processRunner.RunAsync(
                ytDlp,
                args,
                timeout: TimeoutsConfig.Resolve(dependencies.Timeouts.YtDlpMediaUrlSeconds, 300),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                // With the saved dump: fall through to a live extraction. Without it: no URL.
                continue;
            }

            var url = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken)
        => DownloadMediaForProcessingAsync(request, run, infoJsonPath: null, cancellationToken);

    public async Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, string? infoJsonPath, CancellationToken cancellationToken)
    {
        var ytDlp = dependencies.RequireYtDlp("downloading media for local ffmpeg processing");
        var mediaDirectory = run.GetPath("media");
        Directory.CreateDirectory(mediaDirectory);

        var outputTemplate = Path.Combine(mediaDirectory, "source.%(ext)s");
        var usableInfoJson = ResolveUsableInfoJson(infoJsonPath);
        foreach (var useInfoJson in usableInfoJson is null ? [false] : new[] { true, false })
        {
            var args = CreateBaseArguments(request);
            if (!useInfoJson)
            {
                args.Add("--no-playlist");
            }
            args.AddRange([
                "-f", "best[height<=720][ext=mp4]/best[height<=720]/best[ext=mp4]/best",
                "--merge-output-format", "mp4",
                "-o", outputTemplate
            ]);
            args.AddRange(useInfoJson ? ["--load-info-json", usableInfoJson!] : [request.Source]);
            var result = await processRunner.RunAsync(
                ytDlp,
                args,
                timeout: TimeoutsConfig.Resolve(dependencies.Timeouts.YtDlpDownloadSeconds, 1800),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                continue;
            }

            var downloaded = Directory.EnumerateFiles(mediaDirectory, "source.*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (downloaded is not null)
            {
                return downloaded;
            }
        }

        return null;
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

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>yt-dlp's <c>YYYYMMDD</c> upload date.</summary>
    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; set; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; set; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; set; }

    [JsonPropertyName("comment_count")]
    public long? CommentCount { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    /// <summary>Creator-authored chapters (YouTube description timestamps, container chapters).</summary>
    [JsonPropertyName("chapters")]
    public List<YtDlpChapter>? Chapters { get; set; }

    /// <summary>Present only when the probe ran with <c>--write-comments</c> (see <see cref="AnalyzeRequest.Comments"/>).</summary>
    [JsonPropertyName("comments")]
    public List<YtDlpComment>? Comments { get; set; }

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

    /// <summary>
    /// Verbatim <c>--dump-single-json</c> output this instance was parsed from. Not serialized
    /// into <c>metadata.json</c>; the pipeline persists it as <see cref="YtDlpInfoJson.FileName"/>
    /// so later yt-dlp steps can <c>--load-info-json</c> it. Null for local files and for the
    /// synthetic info used by the browser fallback.
    /// </summary>
    [JsonIgnore]
    public string? RawJson { get; set; }

    /// <summary>
    /// True when the extractor advertised at least one manual or automatic caption track. When
    /// this is false on a real yt-dlp dump there is nothing for the subtitle step to download and
    /// the extraction can be skipped entirely.
    /// </summary>
    [JsonIgnore]
    public bool AdvertisesCaptions =>
        (Subtitles is { Count: > 0 } && Subtitles.Values.Any(HasTracks))
        || (AutomaticCaptions is { Count: > 0 } && AutomaticCaptions.Values.Any(HasTracks));

    private static bool HasTracks(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() > 0 : element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
}

public sealed class AvailableSubtitleLanguage
{
    [JsonPropertyName("hasManual")]
    public bool HasManual { get; set; }

    [JsonPropertyName("hasAuto")]
    public bool HasAuto { get; set; }
}

public sealed class YtDlpChapter
{
    [JsonPropertyName("start_time")]
    public double? StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double? EndTime { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class YtDlpComment
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("author_id")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("author_is_uploader")]
    public bool? AuthorIsUploader { get; set; }

    [JsonPropertyName("is_pinned")]
    public bool? IsPinned { get; set; }

    [JsonPropertyName("is_favorited")]
    public bool? IsFavorited { get; set; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; set; }

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>"root" for top-level comments, otherwise the parent comment id.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

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
        var cues = new List<ParsedCue>();

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
            var speakerKind = SpeakerLabelKind.None;
            var spokenLines = new List<string>();
            var spokenLinesWithLabel = new List<string>();
            foreach (var line in lines.Where(line => !line.Contains("-->", StringComparison.Ordinal)))
            {
                var parsed = ExtractSpeakerAndCleanLine(line);
                if (parsed.SpeakerDisplayName is not null && speakerDisplayName is null)
                {
                    speakerDisplayName = parsed.SpeakerDisplayName;
                    speakerKind = parsed.Kind;
                }

                if (!string.IsNullOrWhiteSpace(parsed.CleanedText))
                {
                    spokenLines.Add(parsed.CleanedText);
                }

                if (!string.IsNullOrWhiteSpace(parsed.CleanedTextWithLabel))
                {
                    spokenLinesWithLabel.Add(parsed.CleanedTextWithLabel);
                }
            }

            var spoken = WhitespaceRegex().Replace(string.Join(' ', spokenLines), " ").Trim();
            var spokenWithLabel = WhitespaceRegex().Replace(string.Join(' ', spokenLinesWithLabel), " ").Trim();
            if (spoken.Length == 0 && spokenWithLabel.Length == 0)
            {
                continue;
            }

            cues.Add(new ParsedCue(startTimestamp, endTimestamp, speakerDisplayName, speakerKind, spoken, spokenWithLabel));
        }

        // `Name: text` is only trusted when the document behaves like a speaker-labelled
        // transcript (some label recurs). Otherwise every colon candidate is folded back into
        // the spoken text - see SpeakerLabelHeuristics for the evidence behind this gate.
        var acceptColonLabels = SpeakerLabelHeuristics.AcceptColonLabels(
            cues.Where(cue => cue.SpeakerKind == SpeakerLabelKind.Colon).Select(cue => cue.SpeakerDisplayName));

        var segments = new List<TranscriptSegment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in cues)
        {
            var speakerDisplayName = cue.SpeakerDisplayName;
            var spoken = cue.Spoken;
            if (cue.SpeakerKind == SpeakerLabelKind.Colon && !acceptColonLabels)
            {
                speakerDisplayName = null;
                spoken = cue.SpokenWithLabel;
            }

            if (spoken.Length == 0)
            {
                continue;
            }

            var dedupeKey = $"{cue.StartTimestamp}|{speakerDisplayName ?? string.Empty}|{spoken}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            var startSeconds = ParseTimestampSeconds(cue.StartTimestamp);
            var endSeconds = cue.EndTimestamp is null ? null : ParseTimestampSeconds(cue.EndTimestamp);
            var timestamp = cue.EndTimestamp is null ? cue.StartTimestamp : $"{cue.StartTimestamp} - {cue.EndTimestamp}";
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

    private enum SpeakerLabelKind
    {
        None,
        VoiceTag,
        Bracket,
        Colon
    }

    private sealed record ParsedCue(
        string StartTimestamp,
        string? EndTimestamp,
        string? SpeakerDisplayName,
        SpeakerLabelKind SpeakerKind,
        string Spoken,
        string SpokenWithLabel);

    private sealed record ParsedLine(
        string? SpeakerDisplayName,
        SpeakerLabelKind Kind,
        string CleanedText,
        string CleanedTextWithLabel);

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

    private static ParsedLine ExtractSpeakerAndCleanLine(string line)
    {
        // Try VTT voice tag first: <v Speaker Name>text</v> or self-terminating <v Speaker Name>.
        // Explicit markup is always trusted (SharePoint/Teams emit <v @1> for unnamed speakers).
        var voiceMatch = VttVoiceRegex().Match(line);
        if (voiceMatch.Success)
        {
            var name = voiceMatch.Groups[1].Value.Trim();
            var residual = (voiceMatch.Groups[2].Value + line[(voiceMatch.Index + voiceMatch.Length)..]).Trim();
            var cleaned = CleanSubtitleLine(residual);
            return string.IsNullOrWhiteSpace(name)
                ? new ParsedLine(null, SpeakerLabelKind.None, cleaned, cleaned)
                : new ParsedLine(name, SpeakerLabelKind.VoiceTag, cleaned, cleaned);
        }

        var cleanedLine = CleanSubtitleLine(line);

        // Bracketed speaker: [Speaker Name] text. Sound annotations ([music], [laughter]) and
        // prose-shaped brackets stay in the text.
        var bracketMatch = BracketSpeakerRegex().Match(cleanedLine);
        if (bracketMatch.Success)
        {
            var name = bracketMatch.Groups[1].Value.Trim();
            if (SpeakerLabelHeuristics.LooksLikeSpeakerLabel(name))
            {
                return new ParsedLine(name, SpeakerLabelKind.Bracket, bracketMatch.Groups[2].Value.Trim(), cleanedLine);
            }

            return new ParsedLine(null, SpeakerLabelKind.None, cleanedLine, cleanedLine);
        }

        // Colon-separated speaker prefix: Speaker Name: text. Shape-checked here; the caller
        // applies the document-level recurrence gate before trusting it.
        var colonMatch = ColonSpeakerRegex().Match(cleanedLine);
        if (colonMatch.Success && SpeakerLabelHeuristics.LooksLikeSpeakerLabel(colonMatch.Groups[1].Value))
        {
            return new ParsedLine(colonMatch.Groups[1].Value.Trim(), SpeakerLabelKind.Colon, colonMatch.Groups[2].Value.Trim(), cleanedLine);
        }

        return new ParsedLine(null, SpeakerLabelKind.None, cleanedLine, cleanedLine);
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

    [GeneratedRegex("^([\\p{L}\\p{N}@#][\\p{L}\\p{N}@#_ '\\-\\.]{0,58}[\\p{L}\\p{N}\\.]|[\\p{L}\\p{N}@#])\\s*:\\s+(.+)$")]
    private static partial Regex ColonSpeakerRegex();
}
