using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Host-aware tweaks that let <c>analyze &lt;url&gt;</c> Just Work without per-host flags.
/// All checks are conservative (false negatives over false positives) so unknown hosts keep the
/// generic auto behaviour.
///
/// Three independent decisions live here:
/// <list type="bullet">
///   <item><see cref="IsBrowserOnly"/>: known to fail under yt-dlp; <c>capture-mode auto</c>
///     should skip the yt-dlp probe entirely and go straight to browser capture.</item>
///   <item><see cref="ShouldPreferInlineMedia"/>: known to ship an inline HLS / playlist URL in
///     the embed HTML that ffmpeg can seek without booting the JS player. Auto-enables the
///     <c>--prefer-inline-media</c> short-circuit so the Shaka MSE probe never has to fail
///     loudly (eliminating the <c>CAPTURE_DURATION_UNRESOLVED</c> noise).</item>
///   <item><see cref="TryExtractSessionCode"/>: pulls a stable, human-readable identifier out of
///     URL paths/queries for known sources so the run-id slug is short and recognisable
///     (e.g. <c>brk230-1ccc2f93</c> instead of <c>https-build-microsoft-com-en-us-sessions-brk230-source-sessi-1ccc2f93</c>).</item>
/// </list>
/// </summary>
public static class KnownHosts
{
    // Host predicates ---------------------------------------------------------------------

    /// <summary>
    /// Returns true when the URL host is known to be unreachable by yt-dlp (JS-rendered
    /// players, custom enterprise portals, MSE-only streams). With this true, <c>auto</c>
    /// capture mode skips the yt-dlp metadata probe entirely and routes straight to browser
    /// capture, saving ~10 s of guaranteed-fail process spawn time per run.
    /// </summary>
    public static bool IsBrowserOnly(string? source)
    {
        if (!TryGetHost(source, out var host)) return false;
        return IsMediusFamily(host) || IsMediastream(host) || IsBuildMicrosoft(host);
    }

    /// <summary>
    /// Returns true when the URL host wraps an embed page that exposes the underlying media
    /// URL inline (Medius <c>coreConfiguration</c>, Build <c>build.microsoft.com</c> wrapping
    /// a Medius iframe, mediastream Config-IVOD JSON). For these hosts, the inline-media
    /// sidestep is strictly better than the Shaka MSE probe (which won't boot headlessly).
    /// </summary>
    public static bool ShouldPreferInlineMedia(string? source)
    {
        if (!TryGetHost(source, out var host)) return false;
        return IsMediusFamily(host) || IsMediastream(host) || IsBuildMicrosoft(host);
    }

    // Session-code extraction -------------------------------------------------------------

    /// <summary>
    /// Pulls a human-readable session identifier out of a URL for known source patterns.
    /// Returns the identifier (lowercase, slug-safe) on match, <c>null</c> otherwise. The
    /// result is used as the leading slug for <see cref="ArtifactStore.CreateDeterministicRunId"/>;
    /// callers always append a hash suffix so determinism is preserved even when two URLs
    /// happen to share a code.
    /// </summary>
    public static string? TryExtractSessionCode(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        // Microsoft Build: https://build.microsoft.com/en-US/sessions/BRK230
        if (IsBuildMicrosoft(host))
        {
            var m = BuildSessionPath.Match(path);
            if (m.Success) return Sanitize(m.Groups[1].Value);
        }

        // Medius: https://medius.studios.ms/Embed/video-nc/KEY01 or /Embed/video/<id>
        if (IsMediusFamily(host))
        {
            var m = MediusEmbedPath.Match(path);
            if (m.Success) return Sanitize(m.Groups[1].Value);
        }

        // YouTube watch: /watch?v=<id>
        if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host == "youtu.be")
        {
            if (host == "youtu.be")
            {
                var trimmed = path.Trim('/');
                if (trimmed.Length > 0 && !trimmed.Contains('/')) return Sanitize(trimmed);
            }
            else
            {
                var q = uri.Query.TrimStart('?');
                foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq < 0) continue;
                    var key = pair[..eq];
                    if (string.Equals(key, "v", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = pair[(eq + 1)..];
                        if (!string.IsNullOrWhiteSpace(v)) return Sanitize(v);
                    }
                }
            }
        }

        return null;
    }

    // Internals ---------------------------------------------------------------------------

    private static readonly Regex BuildSessionPath = new(
        @"^/[^/]+/sessions/([^/?#]+)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex MediusEmbedPath = new(
        @"^/[Ee]mbed/[^/]+/([^/?#]+)/?$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    private static bool TryGetHost(string? source, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        host = uri.Host.ToLowerInvariant();
        return host.Length > 0;
    }

    private static bool IsMediusFamily(string host)
    {
        return host.EndsWith("medius.microsoft.com", StringComparison.Ordinal)
            || host.EndsWith("medius.studios.ms", StringComparison.Ordinal)
            || (host.StartsWith("medius", StringComparison.Ordinal) && host.Contains(".event.microsoft.com", StringComparison.Ordinal));
    }

    private static bool IsMediastream(string host)
    {
        return host.EndsWith("mediastream.microsoft.com", StringComparison.Ordinal);
    }

    private static bool IsBuildMicrosoft(string host)
    {
        return host == "build.microsoft.com" || host.EndsWith(".build.microsoft.com", StringComparison.Ordinal);
    }

    private static string Sanitize(string raw)
    {
        var slug = Slug.Create(raw, 40);
        return slug;
    }
}
