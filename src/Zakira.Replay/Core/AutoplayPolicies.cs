namespace Zakira.Replay.Core;

/// <summary>
/// Browser autoplay-policy values + resolver. String-based (not bool) so adding new policies
/// later — e.g. <c>"user-gesture-required"</c>, <c>"document-user-activation-required"</c>,
/// or a future Chromium policy that doesn't exist yet — is a one-line change to the constants
/// and the <see cref="ToChromiumArg"/> switch, with no breaking schema bumps.
/// </summary>
/// <remarks>
/// <para><b>Why a policy at all?</b> Modern browsers refuse to autoplay a video with audio
/// unless one of (a) user has interacted with the page, (b) the page is muted, (c) the source
/// is on an allow-list. MSE-based JS players (Shaka in Microsoft Medius / Build, Bitmovin,
/// Theo, JW, …) commonly rely on autoplay being permitted to drive their boot sequence; when
/// it's blocked they silently fail to attach the MediaSource, <c>video.duration</c> stays
/// <c>NaN</c>, and our duration probe times out.</para>
///
/// <para><b>Three-layer resolution</b> (highest priority wins):</para>
/// <list type="number">
///   <item>CLI / per-request override (<c>--autoplay-policy</c>).</item>
///   <item>Per-host map in config (<c>capture.browser.autoplayPolicyByHost</c>) — e.g.
///         <c>"*.event.microsoft.com": "no-user-gesture-required"</c>. Suffix-match on the
///         source URL's host; bare hostnames also match exactly.</item>
///   <item>Global default in config (<c>capture.browser.autoplayPolicy</c>). Defaults to
///         <c>"default"</c> (let Chromium decide).</item>
/// </list>
///
/// <para><b>Unknown values are treated as <see cref="Default"/></b> so a typo / future-version
/// string never wedges the launch.</para>
/// </remarks>
public static class AutoplayPolicies
{
    /// <summary>Browser-native behaviour. No <c>--autoplay-policy</c> flag is passed.</summary>
    public const string Default = "default";

    /// <summary>
    /// Allow autoplay even without a user gesture. Passed to Chromium as
    /// <c>--autoplay-policy=no-user-gesture-required</c>. The headless workaround for
    /// MSE/Shaka players whose boot sequence stalls when autoplay is blocked.
    /// </summary>
    public const string NoUserGestureRequired = "no-user-gesture-required";

    /// <summary>
    /// Normalise free-form input to a canonical constant. Empty/whitespace/unknown values
    /// collapse to <see cref="Default"/> so a misconfigured value never produces an invalid
    /// Chromium command line.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        return value.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "default" or "browser-default" => Default,
            "no-user-gesture-required" or "no-gesture" or "no-user-gesture" or "allow" => NoUserGestureRequired,
            _ => Default,
        };
    }

    /// <summary>
    /// Resolve the effective autoplay policy for a given source URL, applying the precedence:
    /// CLI override → per-host map → global default. <paramref name="byHost"/> is treated as
    /// a case-insensitive suffix-match dictionary, with a leading <c>*.</c> meaning "match
    /// this suffix on the host name". Plain bare hostnames also match exactly.
    /// </summary>
    /// <param name="cliOverride">The <c>--autoplay-policy</c> argument from the caller, or
    /// null when not specified. Wins when non-null/non-empty.</param>
    /// <param name="byHost">Optional per-host map (typically from
    /// <c>capture.browser.autoplayPolicyByHost</c>). Null/empty when no host overrides exist.</param>
    /// <param name="globalDefault">Global default from <c>capture.browser.autoplayPolicy</c>.</param>
    /// <param name="sourceUrl">The URL being captured; used to look up <paramref name="byHost"/>.
    /// When unparseable, the host map is skipped silently.</param>
    public static string Resolve(string? cliOverride, IReadOnlyDictionary<string, string>? byHost, string? globalDefault, string? sourceUrl)
    {
        // (1) CLI / per-request always wins.
        if (!string.IsNullOrWhiteSpace(cliOverride))
        {
            return Normalize(cliOverride);
        }

        // (2) Host map. Skip silently when the URL is unparseable or the map is empty.
        if (byHost is { Count: > 0 } && !string.IsNullOrWhiteSpace(sourceUrl)
            && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            // Pass 1: exact host match.
            foreach (var (key, value) in byHost)
            {
                var k = key.Trim().ToLowerInvariant();
                if (k.StartsWith("*.", StringComparison.Ordinal)) continue;
                if (string.Equals(k, host, StringComparison.Ordinal))
                {
                    return Normalize(value);
                }
            }
            // Pass 2: suffix wildcard match. Longest matching suffix wins so a more-specific
            // entry can override a broader one.
            var bestMatchLen = -1;
            string? bestMatchValue = null;
            foreach (var (key, value) in byHost)
            {
                var k = key.Trim().ToLowerInvariant();
                if (!k.StartsWith("*.", StringComparison.Ordinal)) continue;
                var suffix = k.AsSpan(1); // includes the leading '.'
                if (host.AsSpan().EndsWith(suffix, StringComparison.Ordinal) && suffix.Length > bestMatchLen)
                {
                    bestMatchLen = suffix.Length;
                    bestMatchValue = value;
                }
            }
            if (bestMatchValue is not null)
            {
                return Normalize(bestMatchValue);
            }
        }

        // (3) Global default.
        return Normalize(globalDefault);
    }

    /// <summary>
    /// Convert a normalised policy to the matching Chromium command-line argument, or null
    /// when the policy is <see cref="Default"/> (in which case no flag is passed). New
    /// policies extend the switch; the rest of the system is policy-agnostic.
    /// </summary>
    public static string? ToChromiumArg(string normalisedPolicy)
        => normalisedPolicy switch
        {
            NoUserGestureRequired => "--autoplay-policy=no-user-gesture-required",
            _ => null,
        };
}
