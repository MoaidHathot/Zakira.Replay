using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Helpers for the Playwright network interceptor that watches for caption files
/// (<c>.vtt</c>, <c>.srt</c>) downloaded by the page during playback. Pure functions so the
/// language-inference rules can be unit-tested without launching a browser.
/// </summary>
internal static partial class BrowserCaptionInterceptor
{
    /// <summary>
    /// True when the URL's path (ignoring query string and fragment) ends in a recognised
    /// caption-file extension. Generous on purpose: the network listener filters every
    /// response, so we want fast cheap rejection of obviously-not-captions URLs.
    /// </summary>
    public static bool IsCaptionUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best-effort BCP-47 language inference from the URL. Returns <c>null</c> when no signal
    /// is present. The second tuple element identifies which heuristic produced the match (for
    /// audit and debugging).
    /// </summary>
    public static (string? Language, string? Source) InferLanguageFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (null, null);
        }

        // 1) Microsoft Medius style: Caption_en-US.vtt, Caption_fr.vtt
        var mediusMatch = MediusCaptionRegex().Match(url);
        if (mediusMatch.Success)
        {
            return (mediusMatch.Groups[1].Value, "url-Caption_<lang>");
        }

        // 2) Path segment like /captions/en/foo.vtt, /lang/zh-Hans/foo.vtt — checked BEFORE the
        // bare-filename pattern so URLs that have BOTH (a path-segment language indicator AND a
        // semantically-meaningful filename) prefer the structural signal.
        var pathMatch = LanguageInPathRegex().Match(url);
        if (pathMatch.Success)
        {
            return (pathMatch.Groups[1].Value, "url-path-segment");
        }

        // 3) File name like subtitle_es-ES.vtt, captions.fr.vtt, en-US.vtt
        var fileNameMatch = LanguageInFileNameRegex().Match(url);
        if (fileNameMatch.Success)
        {
            return (fileNameMatch.Groups[1].Value, "url-filename");
        }

        // 4) Query string like ?lang=en or ?hl=es or ?language=fr or ?l=de or ?tlang=en
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
        {
            // Manual scan because HttpUtility isn't available; URLs with multiple ?lang= are rare.
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }
                var name = Uri.UnescapeDataString(parts[0]).Trim().ToLowerInvariant();
                var value = Uri.UnescapeDataString(parts[1]).Trim();
                if (name is "lang" or "language" or "l" or "hl" or "tlang" && IsPlausibleLanguageCode(value))
                {
                    return (NormalizeLanguageCode(value), $"url-query-{name}");
                }
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Picks the best caption from a discovered set, given an ordered list of language
    /// preferences (e.g. produced by <see cref="AnalysisPipeline.ResolveSubtitleLanguages"/>).
    /// Returns <c>null</c> when the set is empty. When no caption matches a preference, falls
    /// back to the first caption that has a known language, then to the first caption overall.
    /// </summary>
    public static BrowserCapturedCaption? PickBest(IReadOnlyList<BrowserCapturedCaption> captions, IReadOnlyList<string> languagePreferences)
    {
        if (captions.Count == 0)
        {
            return null;
        }

        if (captions.Count == 1)
        {
            return captions[0];
        }

        foreach (var preference in languagePreferences)
        {
            var match = captions.FirstOrDefault(caption => LanguageMatches(caption.InferredLanguage, preference));
            if (match is not null)
            {
                return match;
            }
        }

        return captions.FirstOrDefault(caption => !string.IsNullOrWhiteSpace(caption.InferredLanguage)) ?? captions[0];
    }

    /// <summary>
    /// Loose match between a caption's inferred language and a preference. Equal when both
    /// share their first BCP-47 subtag (so <c>en</c> matches <c>en-US</c> and vice versa);
    /// the special preference <c>auto</c> matches any non-empty language.
    /// </summary>
    public static bool LanguageMatches(string? inferred, string preference)
    {
        if (string.IsNullOrWhiteSpace(preference))
        {
            return false;
        }

        if (preference.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(inferred);
        }

        if (string.IsNullOrWhiteSpace(inferred))
        {
            return false;
        }

        return PrimarySubtag(inferred).Equals(PrimarySubtag(preference), StringComparison.OrdinalIgnoreCase);
    }

    private static string PrimarySubtag(string code)
    {
        var hyphen = code.IndexOf('-');
        return hyphen >= 0 ? code[..hyphen] : code;
    }

    private static bool IsPlausibleLanguageCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 16)
        {
            return false;
        }
        // Permissive: allow letters, digits, hyphens, underscores. Real BCP-47 codes are
        // narrower but caption URLs sometimes use exotic forms (zh-Hans-CN, etc).
        return value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    private static string NormalizeLanguageCode(string value)
    {
        return value.Replace('_', '-').Trim();
    }

    // Microsoft Medius URL family: ".../video-NNNN/Caption_en-US.vtt..." — case-insensitive.
    [GeneratedRegex(@"/Caption_([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\.(?:vtt|srt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MediusCaptionRegex();

    // Generic file-name patterns: "<sep>xx[-XX].vtt" where xx is exactly 2 letters and the
    // optional secondary subtag is 2-4 chars. Restricted on purpose to avoid matching arbitrary
    // 3-letter file-name tokens like "foo.bar.baz.vtt" → "baz". 2-letter primary subtags cover
    // ~99 % of caption URLs in the wild; ISO 639-3 codes (3-letter) are extremely rare here.
    [GeneratedRegex(@"(?:^|[/_.\-])([A-Za-z]{2}(?:-[A-Za-z0-9]{2,4})?)\.(?:vtt|srt)(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LanguageInFileNameRegex();

    // Path segment patterns: "/captions/en/...", "/lang/fr/...", "/subs/zh-Hans/...".
    [GeneratedRegex(@"/(?:captions?|subs?(?:titles?)?|lang(?:uage)?)/([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)/", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LanguageInPathRegex();
}

/// <summary>
/// A caption file the browser fetched while playing the page. Records the discovery URL, the
/// on-disk relative path (under the run's <c>captions/</c> directory), the inferred BCP-47
/// language code if any, the heuristic that produced it, and the byte count + SHA-256 so
/// orchestrators can dedupe across runs.
/// </summary>
public sealed record BrowserCapturedCaption(
    int Ordinal,
    string Url,
    string RelativePath,
    string? InferredLanguage,
    string? LanguageSource,
    long ByteCount,
    string ContentSha256,
    string? ContentType);

/// <summary>
/// Inventory written to <c>captions/discovered.json</c> when the browser-network interceptor
/// captured at least one caption file during playback.
/// </summary>
public sealed record BrowserCapturedCaptionsManifest(
    string SchemaVersion,
    DateTimeOffset DiscoveredAt,
    string? OriginalLanguage,
    IReadOnlyList<BrowserCapturedCaption> Captions);
