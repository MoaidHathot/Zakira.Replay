using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// Builds time-anchored URLs ("deep links") so agents and humans can jump straight to the
/// moment in a source video where a chapter, cue, or search hit appears, instead of scrubbing.
/// Site-specific shapes (YouTube <c>?t=</c>, Vimeo <c>#t=</c>, SharePoint Stream <c>?nav=</c>)
/// are honoured when recognised; everything else falls back to the W3C Media Fragments syntax
/// (<c>#t=&lt;seconds&gt;</c>), which most HTML5-based players respect.
/// </summary>
/// <remarks>
/// Pure / static — no I/O. Negative or NaN <paramref name="seconds"/> are treated as 0 (so a
/// well-formed link is always returned when a base URL exists). Returns null only when the
/// base URL is missing or unparseable, because a fragment-less link is no more useful than
/// a missing one for agents.
/// </remarks>
public static class DeepLink
{
    /// <summary>
    /// Compose a deep link for <paramref name="sourceUrl"/> at <paramref name="seconds"/>.
    /// Returns null when <paramref name="sourceUrl"/> is null/empty/unparseable; otherwise
    /// returns the URL augmented with the most player-appropriate time anchor.
    /// </summary>
    public static string? For(string? sourceUrl, double seconds)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)) return null;

        var clamped = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
        // Floor to whole seconds: every site we target accepts integer seconds; sub-second
        // precision is theatre that varies by player. Easier for humans to skim, too.
        var whole = (long)Math.Floor(clamped);

        var host = uri.Host.ToLowerInvariant();
        if (IsYouTube(host)) return ApplyYouTube(uri, whole);
        if (IsVimeo(host)) return ApplyFragment(uri, $"t={whole}s");
        if (IsSharePointStream(host)) return ApplySharePointStream(uri, whole);

        // Default: W3C Media Fragments — '#t=<seconds>'. Replaces any existing 't=' fragment
        // so a re-anchored URL doesn't carry both. This is the only choice that won't
        // accidentally collide with site-specific query semantics on unknown hosts.
        return ApplyFragment(uri, $"t={whole}");
    }

    private static bool IsYouTube(string host)
        => host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)
           || host == "youtu.be";

    private static bool IsVimeo(string host)
        => host == "vimeo.com" || host.EndsWith(".vimeo.com", StringComparison.Ordinal)
           || host == "player.vimeo.com";

    private static bool IsSharePointStream(string host)
        => host.EndsWith(".sharepoint.com", StringComparison.Ordinal)
           || host.EndsWith(".microsoftstream.com", StringComparison.Ordinal);

    private static string ApplyYouTube(Uri uri, long seconds)
    {
        // YouTube uses ?t=<seconds>s (or the legacy &t=<seconds>). Both the long-form
        // (youtube.com/watch?v=…) and short-form (youtu.be/…) pages accept it.
        // youtu.be additionally accepts t= as the first param.
        return ReplaceOrAppendQuery(uri, "t", $"{seconds}s");
    }

    private static string ApplySharePointStream(Uri uri, long seconds)
    {
        // Microsoft Stream / SharePoint Stream's recognised time anchor is
        // ?nav=t=<H>h<M>m<S>s. Whole-second precision is enough for our timestamps.
        var h = seconds / 3600;
        var m = (seconds % 3600) / 60;
        var s = seconds % 60;
        var nav = $"t={h:D2}h{m:D2}m{s:D2}s";
        return ReplaceOrAppendQuery(uri, "nav", nav);
    }

    /// <summary>
    /// Set/replace a single query-string key on <paramref name="uri"/>. We do this by hand
    /// (rather than UriBuilder + HttpUtility) because UriBuilder corrupts SAS-signed URLs by
    /// re-encoding already-encoded characters, and SAS URLs are the dominant deep-link target
    /// for Medius/Stream sources.
    /// </summary>
    private static string ReplaceOrAppendQuery(Uri uri, string key, string value)
    {
        var query = uri.Query.TrimStart('?');
        var pairs = string.IsNullOrEmpty(query)
            ? new List<string>()
            : query.Split('&').Where(p => p.Length > 0).ToList();

        var prefix = key + "=";
        pairs.RemoveAll(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        pairs.Add($"{key}={value}");

        var baseUri = uri.GetLeftPart(UriPartial.Path);
        var newQuery = string.Join("&", pairs);
        var fragment = uri.Fragment; // preserve
        return $"{baseUri}?{newQuery}{fragment}";
    }

    /// <summary>
    /// Set/replace the URL fragment (#…). Used for the W3C Media Fragments fallback and for
    /// Vimeo (which respects #t=Ns).
    /// </summary>
    private static string ApplyFragment(Uri uri, string fragment)
    {
        var baseUri = uri.GetLeftPart(UriPartial.Query);
        return $"{baseUri}#{fragment}";
    }

    /// <summary>
    /// Parse a string like <c>"01:23:45.500"</c> or <c>"83.5"</c> into seconds. Returns null
    /// on unparseable input. Convenience for places that have a formatted timestamp but no
    /// underlying numeric seconds.
    /// </summary>
    public static double? TryParseSeconds(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;
        var s = timestamp.Trim();

        // h:m:s[.ms] or m:s[.ms]
        var parts = s.Split(':');
        if (parts.Length is 2 or 3)
        {
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    return null;
                }
                total = total * 60 + v;
            }
            return total;
        }

        // Plain decimal seconds.
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
