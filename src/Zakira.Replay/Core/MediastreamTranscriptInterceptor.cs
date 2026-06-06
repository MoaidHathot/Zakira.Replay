using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Zakira.Replay.Core;

/// <summary>
/// Extracts transcripts (and exposes the HLS media URL) from Microsoft
/// <c>mediastream.microsoft.com</c> Shaka-player embed pages. This is the player Microsoft Build
/// "InstaVOD" sessions use when the recording was post-produced through the Harmonic media
/// pipeline rather than uploaded straight to Medius &mdash; e.g. BRK247's <c>onDemandUrl</c>:
/// <code>https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK247IVOD.json</code>
/// </summary>
/// <remarks>
/// <para><b>Why this needs its own profile.</b> The page at <c>player.html?path=...</c> is just a
/// Shaka-player Web wrapper; it contains no inline <c>captionsConfiguration</c> like Medius does.
/// The actual configuration is a separate JSON document at the path supplied via the
/// <c>path=</c> query parameter. That JSON exposes the HLS master playlist URL fragment plus the
/// CDN host map; combined they yield a direct-hit <c>master.m3u8</c> on
/// <c>stream.event.microsoft.com</c>. The master playlist advertises an
/// <c>#EXT-X-MEDIA:TYPE=SUBTITLES</c> entry whose <c>URI=</c> points at a sub-playlist of 600-700
/// individual <c>Segment(N).vtt</c> files (4 s each). Each segment is a rolling caption
/// (CEA-608/708-style word-by-word growth: <c>ac</c> &rarr; <c>actu</c> &rarr; <c>actual</c>),
/// so the segments must be downloaded in parallel and deduplicated before they're useful as a
/// transcript.</para>
/// <para><b>What this class does at runtime.</b>
/// <list type="number">
/// <item>The Playwright capture loop calls <see cref="OnResponse"/> for every response, including
/// the iframe document the host page loaded. The handler recognises mediastream
/// <c>player.html?path=</c> URLs (passive discovery) and stashes the page URL so
/// <see cref="DownloadAsync"/> can derive the config-JSON URL from the <c>path=</c> query.</item>
/// <item>The download pass fetches the config JSON via the authenticated Playwright context,
/// resolves <c>coreConfig.cdns[origin][].hostName</c> + <c>manifests.main[].manifest</c> into a
/// single HLS master URL, fetches and parses the master playlist for its subtitle
/// <c>#EXT-X-MEDIA:TYPE=SUBTITLES</c> URI, fetches the subtitle playlist, fetches every
/// <c>Segment(N).vtt</c> in parallel (bounded concurrency), runs <see cref="DedupeRollingVttSegments"/>
/// to collapse the rolling growth, and persists one merged <c>mediastream-NNNN-&lt;lang&gt;.vtt</c>
/// under the run's <c>captions/</c> directory.</item>
/// </list></para>
/// <para><b>Why the dedupe matters.</b> A 47-minute session like BRK247 emits ~700 4-second VTT
/// segments. Naively concatenating them yields tens of thousands of cues showing every
/// keystroke of a word being typed. The dedupe takes just the LAST cue of each segment (the
/// most-complete state during those 4 s), then runs a prefix-extension pass: if cue N+1 starts
/// with cue N as a prefix, cue N is a partial that grew into cue N+1 (drop N, keep N+1); when
/// cue N+1 is unrelated text, cue N is the completed phrase (emit it). Result: roughly one
/// stable cue per spoken phrase rather than per keystroke.</para>
/// </remarks>
internal sealed partial class MediastreamTranscriptInterceptor : IInlineCaptionInterceptor
{
    private readonly BrowserCaptureRequest request;
    private readonly List<ReplayWarning> warnings;
    private readonly List<string> discoveredPlayerUrls = [];
    private readonly object lockObj = new();
    private string? mediaUrl;

    /// <summary>
    /// Bounded concurrency for the per-segment VTT fetch. The CDN (Azure-fronted
    /// <c>stream.event.microsoft.com</c>) tolerates well beyond this, but 16 is the sweet
    /// spot empirically: throughput stops scaling and per-segment latency variance grows
    /// past this point. Tunable via config in a future revision if needed.
    /// </summary>
    private const int DefaultSegmentFetchConcurrency = 16;

    public MediastreamTranscriptInterceptor(BrowserCaptureRequest request, List<ReplayWarning> warnings)
    {
        this.request = request;
        this.warnings = warnings;
    }

    /// <inheritdoc />
    public string Name => "mediastream";

    /// <inheritdoc />
    public bool HasDiscoveries
    {
        get { lock (lockObj) { return discoveredPlayerUrls.Count > 0; } }
    }

    /// <summary>
    /// HLS master-playlist URL discovered by parsing the player config JSON. Populated lazily
    /// during the first <see cref="DownloadAsync"/> call (the config JSON isn't observed
    /// during navigation &mdash; the player fetches it via XHR after Load, by which time we've
    /// usually already unsubscribed) and used by downstream callers that only need the media
    /// URL for frame extraction (<c>frames --at</c>, the <c>analyze --frames N</c> inline-media
    /// sidestep) without paying for the caption download.
    /// </summary>
    public string? DiscoveredMediaUrl
    {
        get { lock (lockObj) { return mediaUrl; } }
    }

    public void OnResponse(object? sender, IResponse response)
    {
        try
        {
            if (response.Status >= 400) return;
            if (!IsMediastreamPlayerUrl(response.Url)) return;

            // Discovery is URL-only here: we don't need the response body. The actual config
            // JSON is fetched on-demand in DownloadAsync because the JSON URL is derived from
            // the player URL's `path=` query param, not from the response payload.
            lock (lockObj)
            {
                if (!discoveredPlayerUrls.Contains(response.Url, StringComparer.Ordinal))
                {
                    discoveredPlayerUrls.Add(response.Url);
                }
            }
        }
        catch
        {
            // never throw from the event handler
        }
    }

    /// <summary>
    /// Resolve every discovered player URL to a transcript: fetch the config JSON, build the
    /// HLS master URL, find the subtitle track, fetch its segments in parallel, dedupe the
    /// rolling cues, and persist the merged VTT. Returns the persisted captions (empty when
    /// nothing was discovered or every step failed).
    /// </summary>
    public async Task<IReadOnlyList<BrowserCapturedCaption>> DownloadAsync(
        IBrowserContext context,
        IReadOnlyList<string> languagePreferences,
        CancellationToken cancellationToken)
    {
        string[] snapshot;
        lock (lockObj)
        {
            snapshot = discoveredPlayerUrls.ToArray();
        }
        if (snapshot.Length == 0) return [];

        // Heuristic for which player URL to honour first when multiple were observed (the host
        // page sometimes loads several iframes during navigation). Prefer the first
        // "InstaVOD"/main-content URL; embeds for related sessions / B-rolls would otherwise
        // win the race.
        var primaryPlayerUrl = snapshot
            .FirstOrDefault(u => u.Contains("IVOD", StringComparison.OrdinalIgnoreCase))
            ?? snapshot[0];

        var configJsonUrl = BuildConfigJsonUrl(primaryPlayerUrl);
        if (configJsonUrl is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Mediastream player URL had no parseable 'path=' query parameter: {primaryPlayerUrl}",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var config = await FetchJsonAsync<MediastreamConfig>(context, configJsonUrl, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            // FetchJsonAsync already emitted a structured warning on failure.
            return [];
        }

        var masterUrl = BuildHlsMasterUrl(config);
        if (masterUrl is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Mediastream config JSON at {configJsonUrl} did not expose a usable coreConfig.manifests.main[] entry " +
                $"resolvable through coreConfig.cdns[].hostName.",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        // Stash the media URL for downstream callers (FrameCaptureService etc.) even when the
        // caption pipeline ends up empty &mdash; the player iframe and frame capture still work.
        lock (lockObj)
        {
            mediaUrl ??= masterUrl;
        }

        warnings.Add(new ReplayWarning(
            ReplayWarningCodes.CaptureMediastreamTranscriptDiscovered,
            $"Mediastream player config resolved to HLS master: {masterUrl}",
            Source: "mediastream",
            Severity: ReplayWarningSeverities.Info));

        var masterText = await FetchTextAsync(context, masterUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(masterText))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Failed to fetch HLS master playlist for mediastream session at {masterUrl}.",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        // Resolve the master playlist's base URL (everything up to and including the last '/')
        // for the subtitle URI to resolve against.
        var masterBaseUri = new Uri(masterUrl);
        var preferredLanguage = SelectPreferredLanguage(languagePreferences);
        var subtitleRef = SelectSubtitlePlaylist(masterText, preferredLanguage);
        if (subtitleRef is null)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"HLS master playlist at {masterUrl} had no #EXT-X-MEDIA:TYPE=SUBTITLES entry " +
                $"(preferred language '{preferredLanguage ?? "any"}').",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var subtitlePlaylistUrl = new Uri(masterBaseUri, subtitleRef.Uri).ToString();
        var subtitlePlaylistText = await FetchTextAsync(context, subtitlePlaylistUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(subtitlePlaylistText))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Failed to fetch subtitle playlist at {subtitlePlaylistUrl} for mediastream session.",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var segments = ExtractSegmentTimeline(subtitlePlaylistText);
        if (segments.Count == 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Subtitle playlist at {subtitlePlaylistUrl} listed no VTT segments.",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var subtitleBaseUri = new Uri(subtitlePlaylistUrl);
        var stopwatch = Stopwatch.StartNew();
        var fetched = await FetchSegmentBodiesAsync(
            context,
            segments,
            subtitleBaseUri,
            DefaultSegmentFetchConcurrency,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var nonEmpty = fetched.Where(s => !string.IsNullOrWhiteSpace(s.VttText)).ToArray();
        if (nonEmpty.Length == 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Every VTT segment fetch failed for mediastream session at {subtitlePlaylistUrl} " +
                $"(segments listed: {segments.Count}).",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var mergedVtt = DedupeRollingVttSegments(nonEmpty);
        if (string.IsNullOrWhiteSpace(mergedVtt))
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Mediastream rolling-caption dedupe produced no cues from {nonEmpty.Length} segment(s).",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return [];
        }

        var captionsDir = request.Run.GetPath("captions");
        Directory.CreateDirectory(captionsDir);

        var languageSlug = (subtitleRef.Language ?? "eng").ToLowerInvariant();
        var fileName = $"mediastream-0001-{languageSlug}.vtt";
        var fullPath = Path.Combine(captionsDir, fileName);
        var vttBytes = Encoding.UTF8.GetBytes(mergedVtt);
        await File.WriteAllBytesAsync(fullPath, vttBytes, cancellationToken).ConfigureAwait(false);

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(vttBytes)).ToLowerInvariant();
        var caption = new BrowserCapturedCaption(
            // Distinct ordinal-base from Medius (3000) and Stream (2000) so dedupe across
            // interceptors stays disjoint.
            Ordinal: 4001,
            Url: configJsonUrl,
            RelativePath: $"captions/{fileName}",
            InferredLanguage: subtitleRef.Language,
            LanguageSource: subtitleRef.Language is null ? null : "mediastream-hls-language",
            ByteCount: vttBytes.LongLength,
            ContentSha256: hash,
            ContentType: "text/vtt");

        warnings.Add(new ReplayWarning(
            ReplayWarningCodes.CaptureMediastreamTranscriptDownloaded,
            $"Mediastream transcript extracted: {nonEmpty.Length}/{segments.Count} segment(s) " +
            $"fetched + deduped \u2192 {vttBytes.Length:N0} bytes ({fileName}), " +
            $"language={subtitleRef.Language ?? "unknown"}, elapsed={stopwatch.Elapsed.TotalSeconds:N1}s.",
            Source: "mediastream",
            Severity: ReplayWarningSeverities.Info));

        return [caption];
    }

    // -----------------------------------------------------------------------------------------
    // Pure helpers (no I/O). Everything below is unit-tested via the matching test class.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Returns true when <paramref name="url"/> is a Microsoft mediastream Shaka-player wrapper
    /// page bearing a <c>path=...</c> query parameter we can derive the config-JSON URL from.
    /// Conservative on host (only <c>mediastream.microsoft.com</c>) and path (must contain
    /// <c>player.html</c>); the body parse is the real gate, so a false positive just costs us
    /// one HTTP GET that returns no useful payload.
    /// </summary>
    internal static bool IsMediastreamPlayerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (!host.Equals("mediastream.microsoft.com", StringComparison.Ordinal)) return false;

        if (!uri.AbsolutePath.Contains("player.html", StringComparison.OrdinalIgnoreCase)) return false;

        // Must carry the path= query that points to the JSON config.
        var query = uri.Query;
        return query.Contains("path=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Given the player wrapper URL (e.g. <c>.../player.html?path=/events/.../Config-X.json</c>),
    /// derive the absolute URL of the JSON config file. The <c>path=</c> value is always rooted
    /// at <c>mediastream.microsoft.com</c>. Returns <c>null</c> when no <c>path=</c> parameter
    /// is present, when it's empty, or when it produces an invalid URI.
    /// </summary>
    internal static string? BuildConfigJsonUrl(string playerUrl)
    {
        if (string.IsNullOrWhiteSpace(playerUrl)) return null;
        if (!Uri.TryCreate(playerUrl, UriKind.Absolute, out var uri)) return null;

        // Manually parse the query so we don't need an extra dependency. The shape is always
        // ?path=/events/... so a simple string scan suffices and is more robust against URL
        // encoding edge cases than HttpUtility.ParseQueryString.
        var query = uri.Query.TrimStart('?');
        string? pathValue = null;
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq];
            if (!name.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
            pathValue = Uri.UnescapeDataString(pair[(eq + 1)..]);
            break;
        }

        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        if (!pathValue.StartsWith('/')) pathValue = "/" + pathValue;

        // Anchored at the same host the player URL came from. Hardcoding the host would break
        // if a future stage env uses a different mediastream subdomain.
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return origin + pathValue;
    }

    /// <summary>
    /// Resolve <c>coreConfig.cdns[origin][].hostName</c> + <c>coreConfig.manifests.main[].manifest</c>
    /// into a single absolute HLS master URL. Picks the first <c>main</c> manifest entry, looks
    /// up its <c>origin</c> in the CDN map, and uses the first host with non-zero weight. Skips
    /// the <c>asl</c>/<c>isl</c>/<c>bsl</c> sign-language overlays. Returns <c>null</c> when
    /// the JSON is missing any required field or weight=0 wins across the board.
    /// </summary>
    internal static string? BuildHlsMasterUrl(MediastreamConfig config)
    {
        if (config?.CoreConfig is not { } coreConfig) return null;
        if (coreConfig.Manifests is not { } manifests) return null;
        if (manifests.Main is not { Count: > 0 } main) return null;
        if (coreConfig.Cdns is not { } cdns) return null;

        foreach (var entry in main)
        {
            if (string.IsNullOrWhiteSpace(entry.Manifest)) continue;
            if (string.IsNullOrWhiteSpace(entry.Origin)) continue;
            if (!cdns.TryGetValue(entry.Origin, out var hosts) || hosts is not { Count: > 0 }) continue;

            // Highest non-zero weight wins; tie-break by first occurrence.
            var preferred = hosts
                .Where(h => !string.IsNullOrWhiteSpace(h.HostName) && h.Weight > 0)
                .OrderByDescending(h => h.Weight)
                .FirstOrDefault();
            if (preferred is null) continue;

            var host = preferred.HostName!.TrimEnd('/');
            var path = entry.Manifest.StartsWith('/') ? entry.Manifest : "/" + entry.Manifest;
            return host + path;
        }
        return null;
    }

    /// <summary>
    /// Pick the first language preference that looks like a usable BCP-47-ish tag, or
    /// <c>null</c> when none qualifies. Used to bias subtitle-track selection from the HLS
    /// master playlist; <see cref="SelectSubtitlePlaylist"/> falls back to the first subtitle
    /// track when no preference matches.
    /// </summary>
    internal static string? SelectPreferredLanguage(IReadOnlyList<string>? preferences)
    {
        if (preferences is null) return null;
        foreach (var p in preferences)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (p.Equals("auto", StringComparison.OrdinalIgnoreCase)) continue;
            return p;
        }
        return null;
    }

    /// <summary>
    /// Parse the HLS master playlist for its first <c>#EXT-X-MEDIA:TYPE=SUBTITLES</c> entry,
    /// preferring the one whose <c>LANGUAGE</c> attribute matches <paramref name="preferredLanguage"/>
    /// when supplied. Returns the relative URI + advertised language code (e.g.
    /// <c>"ENG"</c>/<c>"eng"</c>/<c>"en"</c> &mdash; the caller normalises). Null when no
    /// subtitle media entry exists.
    /// </summary>
    internal static SubtitleTrackRef? SelectSubtitlePlaylist(string masterPlaylistText, string? preferredLanguage)
    {
        if (string.IsNullOrWhiteSpace(masterPlaylistText)) return null;

        var lines = masterPlaylistText.Split('\n');
        var candidates = new List<SubtitleTrackRef>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) continue;
            // Quick filter; cuts attribute parsing for AUDIO/CLOSED-CAPTIONS/VIDEO entries.
            if (!line.Contains("TYPE=SUBTITLES", StringComparison.Ordinal)) continue;

            var uri = ExtractTagAttribute(line, "URI");
            if (string.IsNullOrWhiteSpace(uri)) continue;
            var language = ExtractTagAttribute(line, "LANGUAGE");
            candidates.Add(new SubtitleTrackRef(uri!, language));
        }
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var match = candidates.FirstOrDefault(c => LanguageEquivalent(c.Language, preferredLanguage));
            if (match is not null) return match;
        }
        return candidates[0];
    }

    /// <summary>
    /// Best-effort 3-letter / 2-letter language equivalence. Matches when the primary subtag
    /// (first 2-3 letters, case-insensitive) lines up. Handles the common
    /// <c>"ENG"</c>/<c>"en"</c>/<c>"en-US"</c> mismatch between HLS and BCP-47.
    /// </summary>
    private static bool LanguageEquivalent(string? hls, string preferred)
    {
        if (string.IsNullOrWhiteSpace(hls) || string.IsNullOrWhiteSpace(preferred)) return false;
        // 3-letter ISO 639-2 (Bibliographic) <-> 2-letter ISO 639-1 for the most common cases
        // we see on Build / Ignite captions. Keep the table tiny; the master HLS we observe
        // only ever advertises ENG, JPN, FRA, DEU, ZHO, KOR, ESP, ITA, POR, RUS, ARA, HIN.
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "en", ["jpn"] = "ja", ["fra"] = "fr", ["fre"] = "fr",
            ["deu"] = "de", ["ger"] = "de", ["zho"] = "zh", ["chi"] = "zh",
            ["kor"] = "ko", ["esp"] = "es", ["spa"] = "es", ["ita"] = "it",
            ["por"] = "pt", ["rus"] = "ru", ["ara"] = "ar", ["hin"] = "hi",
        };
        var hlsKey = hls.Split('-')[0];
        var prefKey = preferred.Split('-')[0];
        if (string.Equals(hlsKey, prefKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (table.TryGetValue(hlsKey, out var hlsAs2) && string.Equals(hlsAs2, prefKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (table.TryGetValue(prefKey, out var prefAs2) && string.Equals(prefAs2, hlsKey, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Extract a quoted attribute value from an <c>#EXT-X-*</c> tag line. Handles the
    /// <c>NAME="value"</c> and <c>NAME=value</c> forms; values containing embedded quotes are
    /// out of spec for HLS attribute lists, so a simple regex suffices.
    /// </summary>
    internal static string? ExtractTagAttribute(string line, string attributeName)
    {
        // Quoted form: NAME="value"
        var quotedRegex = new Regex($"\\b{Regex.Escape(attributeName)}=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        var match = quotedRegex.Match(line);
        if (match.Success) return match.Groups[1].Value;

        // Bareword form: NAME=value (terminated by comma or end-of-line)
        var bareRegex = new Regex($"\\b{Regex.Escape(attributeName)}=([^,\\s]+)", RegexOptions.IgnoreCase);
        match = bareRegex.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Parse the subtitle playlist's <c>#EXTINF:N,</c> + segment-URI pairs into a sequence of
    /// (startSeconds, segmentUri) tuples. Start times are cumulative across the playlist; the
    /// first segment starts at 0. Skips comment and tag lines. Returns an empty list when the
    /// playlist has no <c>#EXTM3U</c> header (defensive: we don't want to silently confuse a
    /// random HTML response for a playlist).
    /// </summary>
    internal static IReadOnlyList<VttSegmentRef> ExtractSegmentTimeline(string subtitlePlaylistText)
    {
        if (string.IsNullOrWhiteSpace(subtitlePlaylistText)) return [];
        var lines = subtitlePlaylistText.Split('\n');
        if (lines.Length == 0 || !lines[0].TrimEnd('\r').StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            return [];
        }

        var result = new List<VttSegmentRef>();
        double cumulative = 0;
        double? pendingDuration = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                // Format: #EXTINF:<seconds>,<optional-title>
                var payload = line.Substring("#EXTINF:".Length);
                var comma = payload.IndexOf(',');
                if (comma >= 0) payload = payload[..comma];
                if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    pendingDuration = seconds;
                }
                continue;
            }
            if (line.StartsWith('#')) continue; // any other tag (PROGRAM-DATE-TIME, MAP, KEY, ...)

            // Bare URI line; pair it with the pending EXTINF.
            if (pendingDuration is { } d)
            {
                result.Add(new VttSegmentRef(line, cumulative, d));
                cumulative += d;
                pendingDuration = null;
            }
        }
        return result;
    }

    /// <summary>
    /// Collapse a series of rolling-VTT segments into a clean, deduped WebVTT body. Each
    /// segment's payload is the raw VTT text downloaded from
    /// <c>.../Stream(N)/Segment(M).vtt</c>; this method extracts the segment's final cue
    /// (highest-end-timestamp cue, representing the most complete state at the end of the
    /// 4-second window) and takes ALL its visible lines. The on-screen caption typically
    /// occupies 1-3 lines: the BOTTOM line is the currently-growing tail, the lines ABOVE
    /// are completed phrases still visible from a moment ago. Color wrappers
    /// (<c>&lt;c.gray&gt;...&lt;/c&gt;</c>, <c>&lt;c.yellow&gt;...&lt;/c&gt;</c>, etc.) are
    /// stripped, and the flat sequence of lines is fed through an adjacent-dedupe pass:
    /// <list type="bullet">
    ///   <item>Identical adjacent lines (the same phrase staying on-screen across multiple
    ///         segments) collapse to a single emission whose window stretches across all
    ///         occurrences.</item>
    ///   <item>When line N+1 starts with line N as a strict prefix, line N was a partial
    ///         that grew into line N+1 (drop N, keep N+1, stretch the window).</item>
    ///   <item>Otherwise both lines are distinct phrases and each gets its own
    ///         <c>[start, nextStart)</c> emission.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Empty segments (the speaker said nothing during those 4 s, or every line was
    /// whitespace after stripping) are silently skipped. The very last buffered phrase
    /// gets <c>end = last-segment-end</c> so the transcript covers the full session
    /// duration even when the speaker trailed off rather than completing a sentence.
    /// </remarks>
    internal static string DedupeRollingVttSegments(IReadOnlyList<FetchedVttSegment> segments)
    {
        if (segments is null || segments.Count == 0) return string.Empty;

        // Phase 1: project each segment to its final-cue's full line set. Each segment's
        // on-screen caption typically holds 1-3 lines: the bottom is the currently-growing
        // tail; lines above are recently-completed phrases still visible. When a phrase
        // "stays on screen" across N adjacent segments (its line keeps re-appearing in each
        // segment's set), we want it emitted ONCE - so we skip any line that was already in
        // the IMMEDIATELY-PRECEDING segment's set. This collapses the screen-residence
        // redundancy without losing the per-segment growth on the bottom line.
        var perLine = new List<(double Start, double End, string Text)>();
        IReadOnlyList<string> previousLines = [];
        foreach (var seg in segments)
        {
            var lines = ExtractFinalCueLines(seg.VttText);
            if (lines.Count == 0)
            {
                previousLines = [];
                continue;
            }
            foreach (var line in lines)
            {
                if (previousLines.Contains(line, StringComparer.Ordinal)) continue;
                perLine.Add((seg.StartSeconds, seg.StartSeconds + seg.DurationSeconds, line));
            }
            previousLines = lines;
        }
        if (perLine.Count == 0) return string.Empty;

        // Phase 2: adjacent-dedupe with prefix-extension. With the screen-residence
        // redundancy already collapsed in phase 1, the remaining duplicates are growing
        // tails: a phrase that grew letter-by-letter across multiple segments appears as a
        // chain of prefix-related entries (e.g. "but the context win" -> "but the context
        // window" -> "but the context window is what"). Collapse each chain to its longest
        // form with a stretched window.
        var emissions = new List<(double Start, double End, string Text)>();
        double? bufferStart = null;
        string? bufferText = null;
        double bufferEnd = 0;
        foreach (var line in perLine)
        {
            if (bufferText is null)
            {
                bufferText = line.Text;
                bufferStart = line.Start;
                bufferEnd = line.End;
                continue;
            }
            if (line.Text.Equals(bufferText, StringComparison.Ordinal) ||
                line.Text.StartsWith(bufferText, StringComparison.Ordinal) ||
                bufferText.StartsWith(line.Text, StringComparison.Ordinal))
            {
                bufferText = line.Text.Length >= bufferText.Length ? line.Text : bufferText;
                bufferEnd = Math.Max(bufferEnd, line.End);
                continue;
            }
            emissions.Add((bufferStart!.Value, line.Start, bufferText));
            bufferText = line.Text;
            bufferStart = line.Start;
            bufferEnd = line.End;
        }
        if (bufferText is not null)
        {
            emissions.Add((bufferStart!.Value, bufferEnd, bufferText));
        }
        if (emissions.Count == 0) return string.Empty;

        // Phase 3: render as standard WebVTT.
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        for (var i = 0; i < emissions.Count; i++)
        {
            var e = emissions[i];
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.Append(FormatVttTimestamp(e.Start));
            sb.Append(" --> ");
            sb.Append(FormatVttTimestamp(e.End));
            sb.AppendLine();
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract ALL visible lines of the LAST cue in a single VTT segment. The "last cue" is
    /// the cue with the highest <c>--&gt;</c> end timestamp; its line set is the on-screen
    /// caption at the end of the segment's window (typically 1-3 lines &mdash; bottom is the
    /// currently-growing tail, lines above are recently-completed phrases still visible).
    /// Color wrappers are stripped; lines that are empty after stripping are filtered out.
    /// Returns an empty list when the segment has no cues or every line is empty.
    /// </summary>
    internal static IReadOnlyList<string> ExtractFinalCueLines(string vttText)
    {
        if (string.IsNullOrWhiteSpace(vttText)) return [];

        var cues = SplitIntoCueBlocks(vttText);
        if (cues.Count == 0) return [];

        (double endSeconds, string[] textLines)? best = null;
        foreach (var cue in cues)
        {
            if (cue.EndSeconds < 0) continue;
            if (best is null || cue.EndSeconds > best.Value.endSeconds)
            {
                best = (cue.EndSeconds, cue.TextLines);
            }
        }
        if (best is null) return [];

        var result = new List<string>(best.Value.textLines.Length);
        foreach (var line in best.Value.textLines)
        {
            var stripped = StripColorTags(line).Trim();
            if (stripped.Length > 0) result.Add(stripped);
        }
        return result;
    }

    /// <summary>
    /// Convenience accessor: extract just the LAST visible line of a segment's final cue
    /// (the live tail). Equivalent to <c>ExtractFinalCueLines(vttText).LastOrDefault()</c>.
    /// Kept as a separate API because some external callers and tests want only the tail.
    /// </summary>
    internal static string? ExtractFinalCueLastLine(string vttText)
    {
        var lines = ExtractFinalCueLines(vttText);
        return lines.Count > 0 ? lines[^1] : null;
    }

    /// <summary>
    /// Strip the <c>&lt;c.color&gt;</c>/<c>&lt;/c&gt;</c> wrappers Microsoft's encoder uses to
    /// distinguish "in-progress" (gray) from "completed" (white/yellow) text. We keep the inner
    /// text; the color signal is not useful for a transcript.
    /// </summary>
    internal static string StripColorTags(string line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? string.Empty;
        // <c.foo>, <c.foo.bar>, <c>, </c>
        return ColorTagRegex().Replace(line, string.Empty);
    }

    internal static IReadOnlyList<VttCueBlock> SplitIntoCueBlocks(string vttText)
    {
        var result = new List<VttCueBlock>();
        var lines = vttText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var current = new List<string>();
        double cueStart = -1, cueEnd = -1;
        bool inCue = false;
        void Flush()
        {
            if (inCue && current.Count > 0)
            {
                result.Add(new VttCueBlock(cueStart, cueEnd, [.. current]));
            }
            current.Clear();
            cueStart = -1; cueEnd = -1; inCue = false;
        }
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (trimmed.StartsWith("WEBVTT", StringComparison.Ordinal)) { Flush(); continue; }
            if (trimmed.StartsWith("X-TIMESTAMP-MAP", StringComparison.Ordinal)) { Flush(); continue; }
            if (trimmed.StartsWith("NOTE", StringComparison.Ordinal)) { Flush(); continue; }

            // Timing line: HH:MM:SS.mmm --> HH:MM:SS.mmm [optional cue settings]
            var arrowIdx = trimmed.IndexOf("-->", StringComparison.Ordinal);
            if (arrowIdx > 0 && cueStart < 0)
            {
                var leftSide = trimmed[..arrowIdx].Trim();
                var rightSide = trimmed[(arrowIdx + 3)..].Trim();
                var endTimeRaw = rightSide.Split(' ', 2)[0];
                if (TryParseVttTimestamp(leftSide, out var s) && TryParseVttTimestamp(endTimeRaw, out var e))
                {
                    cueStart = s;
                    cueEnd = e;
                    inCue = true;
                    continue;
                }
            }

            // Otherwise, payload text line (or an identifier we ignore).
            if (inCue)
            {
                current.Add(trimmed);
            }
            // Lines before the first arrow are treated as cue identifiers and discarded.
        }
        Flush();
        return result;
    }

    internal static bool TryParseVttTimestamp(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Forms accepted: HH:MM:SS.mmm | MM:SS.mmm
        var parts = text.Split(':');
        try
        {
            if (parts.Length == 3)
            {
                var h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                var m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                var s = double.Parse(parts[2], CultureInfo.InvariantCulture);
                seconds = h * 3600 + m * 60 + s;
                return true;
            }
            if (parts.Length == 2)
            {
                var m = double.Parse(parts[0], CultureInfo.InvariantCulture);
                var s = double.Parse(parts[1], CultureInfo.InvariantCulture);
                seconds = m * 60 + s;
                return true;
            }
        }
        catch (FormatException) { }
        catch (OverflowException) { }
        return false;
    }

    private static string FormatVttTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var totalMs = (long)Math.Round(seconds * 1000.0);
        var hours = totalMs / 3_600_000;
        var minutes = (totalMs / 60_000) % 60;
        var secs = (totalMs / 1_000) % 60;
        var ms = totalMs % 1_000;
        return $"{hours:D2}:{minutes:D2}:{secs:D2}.{ms:D3}";
    }

    // -----------------------------------------------------------------------------------------
    // I/O helpers used by DownloadAsync. Kept private so tests don't have to mock Playwright.
    // -----------------------------------------------------------------------------------------

    private async Task<T?> FetchJsonAsync<T>(IBrowserContext context, string url, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var apiResponse = await context.APIRequest.GetAsync(url, new APIRequestContextOptions { Timeout = 30_000 }).ConfigureAwait(false);
            if (!apiResponse.Ok)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                    $"Mediastream config fetch returned HTTP {apiResponse.Status} for {url}.",
                    Source: "mediastream",
                    Severity: ReplayWarningSeverities.Warning));
                return null;
            }
            var body = await apiResponse.BodyAsync().ConfigureAwait(false);
            if (body.Length == 0) return null;
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (Exception ex) when (ex is PlaywrightException or HttpRequestException or TimeoutException or JsonException)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureMediastreamTranscriptFailed,
                $"Mediastream config fetch failed for {url}: {ex.Message}",
                Source: "mediastream",
                Severity: ReplayWarningSeverities.Warning));
            return null;
        }
    }

    private async Task<string?> FetchTextAsync(IBrowserContext context, string url, CancellationToken cancellationToken)
    {
        try
        {
            var apiResponse = await context.APIRequest.GetAsync(url, new APIRequestContextOptions { Timeout = 30_000 }).ConfigureAwait(false);
            if (!apiResponse.Ok) return null;
            var body = await apiResponse.BodyAsync().ConfigureAwait(false);
            return body.Length == 0 ? null : Encoding.UTF8.GetString(body);
        }
        catch (Exception ex) when (ex is PlaywrightException or HttpRequestException or TimeoutException)
        {
            // Caller emits its own warning with the originating step name.
            _ = ex; // explicit acknowledgment for review
            return null;
        }
    }

    private async Task<IReadOnlyList<FetchedVttSegment>> FetchSegmentBodiesAsync(
        IBrowserContext context,
        IReadOnlyList<VttSegmentRef> segments,
        Uri baseUri,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var results = new FetchedVttSegment[segments.Count];
        using var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var idx = i;
            var segment = segments[i];
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var url = new Uri(baseUri, segment.Uri).ToString();
                    var text = await FetchTextAsync(context, url, cancellationToken).ConfigureAwait(false) ?? string.Empty;
                    results[idx] = new FetchedVttSegment(segment.StartSeconds, segment.DurationSeconds, text);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // <c.gray>, <c.gray.bright>, </c> — color wrappers on the live-caption tail.
    [GeneratedRegex(@"</?c(?:\.[A-Za-z0-9_-]+)*>", RegexOptions.Compiled)]
    private static partial Regex ColorTagRegex();
}

// ---------------------------------------------------------------------------------------------
// Plain data records for parsed config + intermediate state. Kept at file scope so the test
// assembly (granted via InternalsVisibleTo) can construct them without reflection.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Subset of the <c>mediastream.microsoft.com</c> player config JSON we actually use. The real
/// JSON has many more fields (autoplay flags, thumbnail paths, ASL streams, telemetry IDs,
/// audio-track menus, &hellip;); we deliberately ignore them so a future schema change in
/// unrelated fields doesn't break parsing.
/// </summary>
internal sealed class MediastreamConfig
{
    public string? Id { get; set; }
    public MediastreamCoreConfig? CoreConfig { get; set; }
}

internal sealed class MediastreamCoreConfig
{
    public string? VideoTitle { get; set; }
    public string? PageTitle { get; set; }
    public string? EventName { get; set; }
    public string? SessionName { get; set; }
    public Dictionary<string, List<MediastreamCdnHost>>? Cdns { get; set; }
    public MediastreamManifestSet? Manifests { get; set; }
}

internal sealed class MediastreamCdnHost
{
    public string? HostName { get; set; }
    public int Weight { get; set; }
}

internal sealed class MediastreamManifestSet
{
    public List<MediastreamManifestEntry>? Main { get; set; }
    // Deliberately not deserialised: asl, isl, bsl &mdash; sign-language overlays we never want
    // to use for transcript or frame extraction.
}

internal sealed class MediastreamManifestEntry
{
    public string? Origin { get; set; }
    public string? Manifest { get; set; }
    public int Weight { get; set; }
}

/// <summary>
/// A subtitle track advertised by the HLS master playlist. <see cref="Uri"/> is relative to
/// the master playlist URL; <see cref="Language"/> is whatever the <c>LANGUAGE</c> attribute
/// carried (could be 2-letter, 3-letter, or null when the master didn't tag it).
/// </summary>
internal sealed record SubtitleTrackRef(string Uri, string? Language);

/// <summary>
/// A single <c>Segment(N).vtt</c> reference parsed from the subtitle playlist:
/// <see cref="Uri"/> is relative to the subtitle playlist; <see cref="StartSeconds"/> is
/// cumulative from playlist start; <see cref="DurationSeconds"/> is the EXTINF value.
/// </summary>
internal sealed record VttSegmentRef(string Uri, double StartSeconds, double DurationSeconds);

/// <summary>
/// A subtitle segment after its body was fetched. <see cref="VttText"/> is the raw, undeduped
/// rolling-caption WebVTT payload; <see cref="DedupeRollingVttSegments"/> consumes a sequence
/// of these to produce the merged transcript.
/// </summary>
internal sealed record FetchedVttSegment(double StartSeconds, double DurationSeconds, string VttText);

/// <summary>
/// One cue parsed out of a single VTT segment: timing + the text lines as they appeared
/// between the timing line and the next blank line. Used by
/// <see cref="MediastreamTranscriptInterceptor.ExtractFinalCueLastLine"/> internally.
/// </summary>
internal sealed record VttCueBlock(double StartSeconds, double EndSeconds, string[] TextLines);
