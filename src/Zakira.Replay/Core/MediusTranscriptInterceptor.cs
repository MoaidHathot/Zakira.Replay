using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Zakira.Replay.Core;

/// <summary>
/// Extracts transcripts from Microsoft Medius / Microsoft Events embed pages
/// (<c>medius.microsoft.com/Embed/...</c>, <c>medius.studios.ms/Embed/...</c>).
/// </summary>
/// <remarks>
/// <para>Background: Medius serves video as HLS-over-MSE through Shaka Player. The
/// <c>&lt;video&gt;</c> element is fed a <c>blob:</c> URL and the actual captions are separate
/// <c>Caption_&lt;lang&gt;.vtt</c> blobs on <c>mediusdl.event.microsoft.com</c>. The existing
/// <see cref="CaptionResponseCollector"/> can catch those <em>only</em> while the player is
/// playing — but in headless capture the MSE/Shaka player frequently never boots, so the caption
/// fetch never fires and we get <c>CAPTIONS_BROWSER_NETWORK_NONE</c>.</para>
/// <para>The key observation: the embed <em>HTML document itself</em> contains a complete,
/// SAS-signed caption manifest inline, regardless of whether playback ever starts:</para>
/// <code>
/// const captionsConfiguration = {
///   "languageList": [
///     { "src": "https://mediusdl.event.microsoft.com/video-7534294/Caption_en-US.vtt?sv=...&sr=c&sig=...&se=...&sp=r",
///       "srclang": "en", "kind": "subtitles", "label": "english" },
///     ...
///   ],
///   "defaultLanguage": "Off", ...
/// };
/// </code>
/// <para>This observer watches for that document response, parses <c>languageList</c>, then
/// downloads the caption(s) matching the run's language preferences via their self-authorising
/// SAS URLs. Because the SAS token is embedded, the download needs no cookies and — crucially —
/// no playback. This is the durable path for Medius/Ignite/Build session transcripts.</para>
/// </remarks>
internal sealed partial class MediusTranscriptInterceptor : IInlineCaptionInterceptor
{
    private readonly BrowserCaptureRequest request;
    private readonly List<ReplayWarning> warnings;
    private readonly List<MediusCaption> discovered = [];
    private readonly object lockObj = new();
    private bool announced;
    private string? mediaUrl;

    public MediusTranscriptInterceptor(BrowserCaptureRequest request, List<ReplayWarning> warnings)
    {
        this.request = request;
        this.warnings = warnings;
    }

    /// <inheritdoc />
    public string Name => "medius";

    /// <inheritdoc />
    public bool HasDiscoveries
    {
        get { lock (lockObj) { return discovered.Count > 0; } }
    }

    /// <summary>
    /// First HLS master-playlist URL discovered in the embed page's <c>coreConfiguration</c>
    /// block. Populated alongside the captions during the same response parse, so a caller
    /// that only needs the media URL (e.g. for ad-hoc <c>frames --at</c> spot capture against
    /// a Medius/Build session) can read it without paying for caption downloads.
    /// </summary>
    /// <remarks>
    /// The URL is host-direct (<c>stream.event.microsoft.com/prod*/...master.m3u8</c>), not
    /// SAS-signed — public Build/Ignite/Medius sessions accept it without cookies or proxy.
    /// ffmpeg can <c>-ss N -i URL -frames:v 1</c> against it in single-digit seconds for
    /// each clip near a keyframe (~18s observed for KEY01 at 02:00 on first call).
    /// </remarks>
    public string? DiscoveredMediaUrl
    {
        get { lock (lockObj) { return mediaUrl; } }
    }

    public void OnResponse(object? sender, IResponse response)
    {
        try
        {
            if (response.Status >= 400) return;
            if (!IsMediusEmbedUrl(response.Url)) return;

            // The caption manifest lives in the HTML document. Fire-and-forget the body read;
            // results are stashed for the later download pass. Never throw from the handler.
            _ = LoadAndParseAsync(response);
        }
        catch
        {
            // never throw from the event handler
        }
    }

    private async Task LoadAndParseAsync(IResponse response)
    {
        byte[] body;
        try
        {
            body = await response.BodyAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return;
        }

        if (body.Length == 0) return;
        var html = Encoding.UTF8.GetString(body);

        // Same response carries both the captions manifest and the player's coreConfiguration
        // (which holds the HLS master playlist URL). Extract both so a media-URL-only caller
        // doesn't have to re-fetch the page just to learn the m3u8 location.
        var discoveredMediaUrl = TryExtractMediaUrl(html);
        if (discoveredMediaUrl is not null)
        {
            lock (lockObj)
            {
                mediaUrl ??= discoveredMediaUrl;
            }
        }

        var captions = TryExtractCaptionConfig(html);
        if (captions.Count == 0) return;

        lock (lockObj)
        {
            foreach (var caption in captions)
            {
                if (!discovered.Any(existing => string.Equals(existing.Src, caption.Src, StringComparison.Ordinal)))
                {
                    discovered.Add(caption);
                }
            }

            if (!announced && discovered.Count > 0)
            {
                announced = true;
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureMediusTranscriptDiscovered,
                    $"Medius embed page advertised {discovered.Count} caption language(s) inline: " +
                    string.Join(", ", discovered.Take(12).Select(c => c.Language ?? c.SrcLang ?? "?")) +
                    (discovered.Count > 12 ? ", ..." : string.Empty) + ".",
                    Source: "medius",
                    Severity: ReplayWarningSeverities.Info));
            }
        }
    }

    /// <summary>
    /// Returns true when the URL is a Medius / Microsoft Events embed page that can carry an
    /// inline <c>captionsConfiguration</c> block. Conservative on host; the body parse is the
    /// real gate, so a false positive just means we read a body and find nothing.
    /// </summary>
    internal static bool IsMediusEmbedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        var isMediusHost = host.EndsWith("medius.microsoft.com", StringComparison.Ordinal)
            || host.EndsWith("medius.studios.ms", StringComparison.Ordinal)
            || host.Contains(".event.microsoft.com", StringComparison.Ordinal) && host.StartsWith("medius", StringComparison.Ordinal);
        if (!isMediusHost) return false;

        // Embed/player pages only — skip the static-asset and DL CDNs.
        return uri.AbsolutePath.Contains("/Embed/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/embed/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Find the first HLS master-playlist URL inside the embed page's <c>coreConfiguration</c>
    /// JS assignment. Pure (no I/O) so the brace-matching and JSON walk can be unit-tested.
    /// Returns null when the block is absent or no manifest entry exposes a usable URL.
    /// </summary>
    /// <remarks>
    /// Shape we mine (verified against KEY01 / Microsoft Build):
    /// <code>
    /// let coreConfiguration = {
    ///   "manifests": {
    ///     "main": [
    ///       { "manifest": "https://stream.event.microsoft.com/prodwe/.../master.m3u8", "weight": 80, ... },
    ///       { "manifest": "https://stream.event.microsoft.com/prodnc/.../master.m3u8", "weight": 20, ... }
    ///     ],
    ///     "asl": [ ... ]   // American Sign Language stream; deliberately skipped
    ///   }
    /// };
    /// </code>
    /// We prefer <c>manifests.main</c> over <c>asl</c>/<c>isl</c>/<c>bsl</c> so spot-frames
    /// reflect the speaker's slide content, not the sign-language overlay. Within <c>main</c>
    /// the first entry wins (Medius orders by weight already).
    /// </remarks>
    internal static string? TryExtractMediaUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var marker = html.IndexOf("coreConfiguration", StringComparison.Ordinal);
        if (marker < 0) return null;

        var open = html.IndexOf('{', marker);
        if (open < 0) return null;
        var json = ExtractBraceBalancedJson(html, open);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("manifests", out var manifests)
                || manifests.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!manifests.TryGetProperty("main", out var main) || main.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var entry in main.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("manifest", out var url)) continue;
                if (url.ValueKind != JsonValueKind.String) continue;
                var u = url.GetString();
                if (!string.IsNullOrWhiteSpace(u)) return u;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<MediusCaption> TryExtractCaptionConfig(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var marker = html.IndexOf("captionsConfiguration", StringComparison.Ordinal);
        if (marker < 0) return [];

        // Find the first '{' after the assignment, then brace-match to its close. Brace counting
        // (rather than a non-greedy regex) survives '}' characters embedded inside string values.
        var open = html.IndexOf('{', marker);
        if (open < 0) return [];
        var json = ExtractBraceBalancedJson(html, open);
        if (json is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("languageList", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<MediusCaption>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var src = TryGetString(entry, "src");
                if (string.IsNullOrWhiteSpace(src)) continue;

                var srcLang = TryGetString(entry, "srclang");
                var label = TryGetString(entry, "label");
                // Prefer the BCP-47-ish tag embedded in the file name (Caption_en-US.vtt) over the
                // terse srclang attribute, which Medius sometimes sets to non-standard values
                // (e.g. "bd" for Bangla, "br" for Portuguese-Brazil).
                var language = InferLanguageFromSrc(src) ?? (string.IsNullOrWhiteSpace(srcLang) ? null : srcLang);

                result.Add(new MediusCaption(src!, srcLang, label, language));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Download the caption(s) matching <paramref name="languagePreferences"/> via their SAS
    /// URLs and persist them under <c>captions/</c>. When no preference matches, falls back to
    /// English, then the first advertised language, so a transcript is always produced when the
    /// page advertised any. Returns the persisted captions (empty when nothing downloaded).
    /// </summary>
    public async Task<IReadOnlyList<BrowserCapturedCaption>> DownloadAsync(
        IBrowserContext context,
        IReadOnlyList<string> languagePreferences,
        CancellationToken cancellationToken)
    {
        MediusCaption[] snapshot;
        lock (lockObj)
        {
            snapshot = discovered.ToArray();
        }
        if (snapshot.Length == 0) return [];

        var selected = SelectForDownload(snapshot, languagePreferences);
        if (selected.Count == 0) return [];

        var captionsDir = request.Run.GetPath("captions");
        Directory.CreateDirectory(captionsDir);

        var captions = new List<BrowserCapturedCaption>();
        const int ordinalBase = 3000;
        var ordinal = 0;

        foreach (var caption in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;

            byte[]? body;
            try
            {
                var apiResponse = await context.APIRequest.GetAsync(caption.Src, new APIRequestContextOptions
                {
                    Timeout = 60_000
                }).ConfigureAwait(false);
                if (!apiResponse.Ok)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureMediusTranscriptFailed,
                        $"Medius caption download returned HTTP {apiResponse.Status} for language " +
                        $"'{caption.Language ?? caption.SrcLang ?? "?"}'.",
                        Source: "medius",
                        Severity: ReplayWarningSeverities.Warning));
                    continue;
                }
                body = await apiResponse.BodyAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or HttpRequestException or TimeoutException)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureMediusTranscriptFailed,
                    $"Medius caption download failed for language '{caption.Language ?? caption.SrcLang ?? "?"}': {ex.Message}",
                    Source: "medius",
                    Severity: ReplayWarningSeverities.Warning));
                continue;
            }

            if (body is null || body.Length == 0)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureMediusTranscriptFailed,
                    $"Medius caption download returned an empty body for language " +
                    $"'{caption.Language ?? caption.SrcLang ?? "?"}'.",
                    Source: "medius",
                    Severity: ReplayWarningSeverities.Warning));
                continue;
            }

            var text = Encoding.UTF8.GetString(body);
            var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (!trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureMediusTranscriptFailed,
                    $"Medius caption for language '{caption.Language ?? caption.SrcLang ?? "?"}' " +
                    $"was not WebVTT (starts with '{new string(trimmed.Take(16).ToArray())}').",
                    Source: "medius",
                    Severity: ReplayWarningSeverities.Warning));
                continue;
            }

            var languageSlug = (caption.Language ?? caption.SrcLang ?? "medius")
                .Replace('/', '-').Replace('\\', '-').Trim();
            var fileName = $"medius-{ordinal:0000}-{languageSlug}.vtt";
            var fullPath = Path.Combine(captionsDir, fileName);
            await File.WriteAllBytesAsync(fullPath, body, cancellationToken).ConfigureAwait(false);

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
            captions.Add(new BrowserCapturedCaption(
                Ordinal: ordinalBase + ordinal,
                Url: caption.Src,
                RelativePath: $"captions/{fileName}",
                InferredLanguage: caption.Language,
                LanguageSource: caption.Language is null ? null : "medius-captionsConfiguration",
                ByteCount: body.LongLength,
                ContentSha256: hash,
                ContentType: "text/vtt"));

            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediusTranscriptDownloaded,
                $"Downloaded Medius caption: language={caption.Language ?? caption.SrcLang ?? "unknown"} " +
                $"({caption.Label ?? "no label"}), bytes={body.Length:N0} \u2192 {fullPath}.",
                Source: "medius",
                Severity: ReplayWarningSeverities.Info));
        }

        return captions;
    }

    /// <summary>
    /// Choose which advertised languages to actually fetch. Each preference (in order) pulls in
    /// the first matching language; <c>auto</c> / no-match falls back to English then the first
    /// entry. Dedupes by source URL so we never download the same blob twice.
    /// </summary>
    internal static IReadOnlyList<MediusCaption> SelectForDownload(
        IReadOnlyList<MediusCaption> available,
        IReadOnlyList<string> languagePreferences)
    {
        if (available.Count == 0) return [];

        var picks = new List<MediusCaption>();
        void Add(MediusCaption? caption)
        {
            if (caption is null) return;
            if (picks.Any(p => string.Equals(p.Src, caption.Src, StringComparison.Ordinal))) return;
            picks.Add(caption);
        }

        foreach (var preference in languagePreferences ?? [])
        {
            var match = available.FirstOrDefault(c => BrowserCaptionInterceptor.LanguageMatches(c.Language ?? c.SrcLang, preference));
            Add(match);
        }

        if (picks.Count == 0)
        {
            var english = available.FirstOrDefault(c => BrowserCaptionInterceptor.LanguageMatches(c.Language ?? c.SrcLang, "en"));
            Add(english ?? available[0]);
        }

        return picks;
    }

    /// <summary>
    /// Walk forward from the opening brace at <paramref name="openIndex"/>, counting brace depth
    /// while skipping over string literals (so braces inside strings don't unbalance the count),
    /// and return the substring up to and including the matching close brace. Null when the
    /// braces never balance (truncated/malformed input).
    /// </summary>
    private static string? ExtractBraceBalancedJson(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(openIndex, i - openIndex + 1);
                    }
                    break;
            }
        }
        return null;
    }

    internal static string? InferLanguageFromSrc(string src)
    {
        var match = CaptionFileNameRegex().Match(src);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    // Caption_en-US.vtt / Caption_zh-Hans.vtt / Caption_fr.vtt — same family the network-side
    // BrowserCaptionInterceptor recognises, captured here from the manifest src URL.
    [GeneratedRegex(@"/Caption_([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\.(?:vtt|srt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CaptionFileNameRegex();
}

/// <summary>
/// One entry from a Medius embed page's <c>captionsConfiguration.languageList</c>.
/// <paramref name="Src"/> is the self-authorising SAS URL; <paramref name="Language"/> is the
/// BCP-47-ish tag inferred from the file name (preferred over the terser <paramref name="SrcLang"/>).
/// </summary>
internal sealed record MediusCaption(string Src, string? SrcLang, string? Label, string? Language);
