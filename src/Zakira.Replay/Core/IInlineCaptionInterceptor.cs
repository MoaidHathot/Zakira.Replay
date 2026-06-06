using Microsoft.Playwright;

namespace Zakira.Replay.Core;

/// <summary>
/// Pluggable hook into the headless-browser response stream that recovers transcripts from
/// streaming-player embed pages whose <c>&lt;video&gt;</c> element never boots headlessly
/// (Medius/Shaka, Bitmovin, Theo, JW, Brightcove, Kaltura, …).
/// </summary>
/// <remarks>
/// <para>Implementations are constructed once per <see cref="BrowserCaptureRequest"/> by
/// <see cref="InlineCaptionInterceptorRegistry.CreateFor"/>, attached to <c>page.Response</c> by
/// <see cref="PlaywrightVideoCaptureClient.CaptureAsync"/>, then invoked once after navigation /
/// duration-probe to download whatever they discovered.</para>
/// <para>Adding a new profile is a two-step exercise: write a class that implements this
/// interface and add a single entry to <see cref="InlineCaptionInterceptorRegistry"/>. No
/// changes to <see cref="PlaywrightVideoCaptureClient"/> are required; the capture loop
/// iterates the registry uniformly.</para>
/// <para><b>Contract:</b> <see cref="OnResponse"/> is a Playwright event handler — it MUST NOT
/// throw. <see cref="DownloadAsync"/> MUST return an empty list (not throw) when nothing was
/// discovered, and MUST persist every returned <see cref="BrowserCapturedCaption"/> to disk
/// before returning so the merge step can resolve their relative paths.</para>
/// </remarks>
internal interface IInlineCaptionInterceptor
{
    /// <summary>
    /// Short stable identifier for logs/warnings, e.g. <c>"medius"</c>. Treat as a slug —
    /// lowercase, ASCII, no spaces.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True once at least one caption candidate has been discovered in the page traffic. Used by
    /// the capture loop to skip the download step for interceptors that observed no relevant
    /// responses.
    /// </summary>
    bool HasDiscoveries { get; }

    /// <summary>
    /// First playable media URL (HLS master playlist, DASH manifest, etc.) discovered by this
    /// interceptor in the page traffic, or <c>null</c> when the source format doesn't expose one
    /// the interceptor recognises. The base contract is opt-in via the default implementation so
    /// existing profiles don't have to advertise it; profiles whose embed pages inline the media
    /// URL (e.g. <see cref="MediusTranscriptInterceptor.DiscoveredMediaUrl"/>) override this to
    /// power ad-hoc spot-frame capture (<c>frames --at</c>) without requiring yt-dlp resolution.
    /// </summary>
    string? DiscoveredMediaUrl => null;

    /// <summary>
    /// Playwright <c>page.Response</c> event handler. Fire-and-forget body reads are encouraged;
    /// this method must never throw — Playwright propagates handler exceptions as test failures.
    /// </summary>
    void OnResponse(object? sender, IResponse response);

    /// <summary>
    /// Download every discovered caption matching <paramref name="languagePreferences"/> via the
    /// interceptor's own auth model (SAS URL, cookies, headers, etc.), persist them under the
    /// run's <c>captions/</c> directory, and return the persisted records. Returns an empty
    /// list when nothing was discovered or nothing matched.
    /// </summary>
    Task<IReadOnlyList<BrowserCapturedCaption>> DownloadAsync(
        IBrowserContext context,
        IReadOnlyList<string> languagePreferences,
        CancellationToken cancellationToken);
}

/// <summary>
/// Single source of truth for the inline-caption interceptor profiles compiled into the
/// browser-capture path. Add a new profile by appending one line here; the rest of the system
/// (subscribe/unsubscribe in <see cref="PlaywrightVideoCaptureClient.CaptureAsync"/>, fallback
/// download passes) iterates this list uniformly.
/// </summary>
/// <remarks>
/// Order matters only for the fallback download passes: when multiple profiles claim the same
/// page, the first one to return a non-empty caption list wins. Put highly specific profiles
/// (e.g. <c>MediusInlineCaptionInterceptor</c>) ahead of generic ones.
/// </remarks>
internal static class InlineCaptionInterceptorRegistry
{
    /// <summary>
    /// Build the per-run interceptor set. Returns an empty list when
    /// <see cref="BrowserCaptureRequest.CaptureCaptions"/> is false so the capture loop can
    /// no-op cleanly.
    /// </summary>
    public static IReadOnlyList<IInlineCaptionInterceptor> CreateFor(
        BrowserCaptureRequest request,
        List<ReplayWarning> warnings)
    {
        if (!request.CaptureCaptions)
        {
            return [];
        }

        return
        [
            new MediusTranscriptInterceptor(request, warnings),
            // Microsoft mediastream.microsoft.com Shaka-player wrapper. Used by Microsoft Build
            // "InstaVOD" sessions (e.g. BRK247) whose onDemandUrl is
            // mediastream.microsoft.com/.../player.html?path=/events/.../Config-<CODE>IVOD.json.
            // Unlike Medius, captions are NOT inlined; the interceptor builds the config URL
            // from the page URL's `path=` query, resolves the HLS master via cdns + manifests,
            // and dedupes the rolling Segment(N).vtt subtitle track.
            new MediastreamTranscriptInterceptor(request, warnings),
            // Future profiles drop in here. See IInlineCaptionInterceptor for the contract.
            // Examples we'd plug in next: GenericJsonLdSubtitleInterceptor, BitmovinPlayerInterceptor,
            // TheoPlayerInterceptor, JwPlayerInterceptor, BrightcoveInterceptor, KalturaInterceptor.
        ];
    }
}
