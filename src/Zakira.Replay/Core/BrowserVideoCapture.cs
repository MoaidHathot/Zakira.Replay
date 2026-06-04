using Microsoft.Playwright;

namespace Zakira.Replay.Core;

/// <summary>
/// Extracts video frames by driving a real browser (Playwright + Chromium). This is the
/// alternative to the yt-dlp + ffmpeg path; use it for sites yt-dlp can't reach (auth-gated
/// portals, custom players, sites whose URL only serves a fully-rendered page).
/// Implementations write the same <c>frames/scene-NNNN.jpg</c> files the rest of the pipeline
/// expects, so downstream stages (perceptual hash, slide grouping, smart-crop, OCR, vision)
/// are unchanged.
/// </summary>
public interface IBrowserVideoCaptureClient
{
    Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed record BrowserCaptureRequest(
    string Url,
    VideoRun Run,
    int FrameCount,
    string? PlayButtonSelector,
    string VideoElementSelector,
    double SeekWaitSeconds,
    double DurationProbeTimeoutSeconds,
    int JpegQuality,
    bool CaptureCaptions,
    int MaxCaptionBytes,
    string? AuthStorageStatePath = null,
    string? EdgeUserDataDir = null,
    string? EdgeProfileDirectory = null,
    bool CaptureMediaForStt = false,
    long MaxMediaBytes = 0,
    bool Debug = false,
    long DebugMaxBodyBytes = 1L * 1024 * 1024,
    IReadOnlyList<string>? CaptionLanguagePreferences = null,
    // When true, the capture pass navigates, lets interceptors observe responses + extracts
    // session metadata, then returns WITHOUT engaging the player or polling for duration.
    // Used by FrameCaptureService for ad-hoc spot frames against sources where the only thing
    // we need is the inline media URL the player config exposes (e.g. Medius / Build sessions
    // whose HLS m3u8 ships inline in the embed HTML). Cuts a ~25s probe to ~3-5s.
    bool MetadataOnly = false,
    // Chromium autoplay-policy override for this capture, e.g. AutoplayPolicies.NoUserGestureRequired
    // to bypass the autoplay-with-sound block that wedges MSE/Shaka players (Medius/Build,
    // some Bitmovin/Theo deployments). When null, no flag is passed and Chromium's default
    // applies. Resolved by AutoplayPolicies.Resolve at the pipeline boundary (CLI > host map
    // > global config default) so this field only needs the final, already-normalised value.
    string? AutoplayPolicy = null);

public sealed record BrowserCaptureResult(
    IReadOnlyList<FrameArtifact> Frames,
    double? DurationSeconds,
    IReadOnlyList<ReplayWarning> Warnings,
    IReadOnlyList<BrowserCapturedCaption> Captions,
    string? DownloadedMediaPath = null,
    SessionMetadata? SessionMetadata = null,
    string? InlineMediaUrl = null);

/// <summary>
/// Playwright-backed implementation. Pins Chromium to the user's Edge installation (resolved by
/// <see cref="DependencyResolver.RequireEdge"/>) so we don't need to ship a separate Chromium
/// bundle — the same approach <see cref="DiscoveryService"/> uses.
/// </summary>
public sealed class PlaywrightVideoCaptureClient : IBrowserVideoCaptureClient
{
    private readonly DependencyResolver dependencies;

    public PlaywrightVideoCaptureClient(DependencyResolver dependencies)
    {
        this.dependencies = dependencies;
    }

    public async Task<BrowserCaptureResult> CaptureAsync(BrowserCaptureRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var warnings = new List<ReplayWarning>();
        string edge;
        try
        {
            edge = dependencies.RequireEdge("browser-backed video capture");
        }
        catch (ReplayException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserUnavailable,
                $"Browser capture requested but Edge is not available: {ex.Message}",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Error));
            return new BrowserCaptureResult([], null, warnings, []);
        }

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.EdgeUserDataDir))
            {
                // Persistent-context mode: Playwright launches Edge with --user-data-dir, so
                // Chromium reads/writes cookies in-place using its DPAPI-encrypted SQLite
                // (per-user, per-machine on Windows). No plaintext bearer tokens persisted by
                // us. Pre-flight checks below abort with actionable warnings if the profile
                // isn't usable.
                var profileSubdir = string.IsNullOrWhiteSpace(request.EdgeProfileDirectory) ? "Default" : request.EdgeProfileDirectory;
                if (!Directory.Exists(request.EdgeUserDataDir))
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserProfileDirMissing,
                        $"Configured Edge user-data-dir does not exist: '{request.EdgeUserDataDir}'. " +
                        $"Run `zakira-replay auth init-edge-profile` to create and initialize it, or set " +
                        $"`capture.browser.edgeUserDataDir` to an existing directory.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Error));
                    return new BrowserCaptureResult([], null, warnings, []);
                }

                var singletonLock = Path.Combine(request.EdgeUserDataDir, profileSubdir, "SingletonLock");
                if (File.Exists(singletonLock))
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserProfileLocked,
                        $"Edge profile lock detected at '{singletonLock}'. Close any Edge windows using " +
                        $"user-data-dir '{request.EdgeUserDataDir}' before retrying.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Error));
                    return new BrowserCaptureResult([], null, warnings, []);
                }

                var persistentOptions = new BrowserTypeLaunchPersistentContextOptions
                {
                    ExecutablePath = edge,
                    Headless = true,
                    Args = BuildChromiumArgs(request, $"--profile-directory={profileSubdir}"),
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
                };
                if (request.Debug)
                {
                    var debugDir = request.Run.GetPath("debug");
                    Directory.CreateDirectory(debugDir);
                    persistentOptions.RecordHarPath = Path.Combine(debugDir, "network.har");
                    persistentOptions.RecordHarContent = HarContentPolicy.Embed;
                }

                try
                {
                    context = await playwright.Chromium.LaunchPersistentContextAsync(
                        request.EdgeUserDataDir,
                        persistentOptions).ConfigureAwait(false);
                }
                catch (PlaywrightException ex)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserProfileLaunchFailed,
                        $"Edge persistent-context launch failed for user-data-dir '{request.EdgeUserDataDir}' " +
                        $"(profile '{profileSubdir}'): {ex.Message}. Try re-initialising with " +
                        $"`zakira-replay auth init-edge-profile`.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Error));
                    return new BrowserCaptureResult([], null, warnings, []);
                }
            }
            else
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    ExecutablePath = edge,
                    Headless = true,
                    Args = BuildChromiumArgs(request)
                }).ConfigureAwait(false);

                var contextOptions = new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    StorageStatePath = string.IsNullOrWhiteSpace(request.AuthStorageStatePath) ? null : request.AuthStorageStatePath
                };
                if (request.Debug)
                {
                    var debugDir = request.Run.GetPath("debug");
                    Directory.CreateDirectory(debugDir);
                    contextOptions.RecordHarPath = Path.Combine(debugDir, "network.har");
                    contextOptions.RecordHarContent = HarContentPolicy.Embed;
                }

                context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);
            }

            var page = await context.NewPageAsync().ConfigureAwait(false);

            // Attach network listener BEFORE navigation so we catch caption fetches that fire
            // during page load (Microsoft Medius, for instance, requests Caption_en-US.vtt as
            // part of player initialisation).
            var captionCollector = request.CaptureCaptions
                ? new CaptionResponseCollector(request, warnings)
                : null;
            if (captionCollector is not null)
            {
                page.Response += captionCollector.OnResponse;
            }

            // Optional media-URL collector. Records candidate media responses (Content-Type
            // video/* or audio/* or application/(x-)mpegURL or application/dash+xml) so we can
            // attempt an authenticated re-download as STT fallback when no inline captions are
            // observed. Body is NOT downloaded here \u2014 we just record URLs + sizes from headers.
            var mediaCollector = request.CaptureMediaForStt
                ? new MediaResponseCollector(request)
                : null;
            if (mediaCollector is not null)
            {
                page.Response += mediaCollector.OnResponse;
            }

            // Optional diagnostic recorder. Writes network.log (JSONL) + JSON/XML/text response
            // bodies (under DebugMaxBodyBytes) + index.json. Side-channel \u2014 doesn't affect
            // capture behaviour; just persists everything we see for offline analysis.
            var debugRecorder = request.Debug
                ? new DebugNetworkRecorder(request)
                : null;
            if (debugRecorder is not null)
            {
                page.Response += debugRecorder.OnResponse;
            }

            // SharePoint Stream / OneDrive transcript metadata interceptor. Watches for the
            // `_api/v2.X/drives/.../items/...?...media/transcripts` JSON metadata response
            // that Stream's player issues on load, then follows each transcript's
            // `temporaryDownloadUrl` via cookie-auth and converts the body to WebVTT. This is
            // the only reliable way to get transcripts from SharePoint Stream \u2014 the player
            // doesn't use HTML5 textTracks and doesn't fetch caption files as direct .vtt
            // URLs the standard interceptor can match on.
            var streamInterceptor = request.CaptureCaptions
                ? new SharePointStreamInterceptor(request, warnings)
                : null;
            if (streamInterceptor is not null)
            {
                page.Response += streamInterceptor.OnResponse;
            }

            // Inline-caption interceptors (registry). Each profile in
            // InlineCaptionInterceptorRegistry hooks page.Response, accumulates state, then
            // downloads its discovered caption sidecars after navigation completes. Add a new
            // streaming-player profile by appending it to the registry — no changes required
            // here. Returns an empty list when CaptureCaptions is false, so the loops below are
            // no-ops.
            var inlineCaptionInterceptors = InlineCaptionInterceptorRegistry.CreateFor(request, warnings);
            foreach (var interceptor in inlineCaptionInterceptors)
            {
                page.Response += interceptor.OnResponse;
            }

            progress?.Report($"Navigating to {request.Url} (browser capture)...");
            await page.GotoAsync(request.Url, new PageGotoOptions
            {
                // Use Load (not NetworkIdle): on streaming-video pages the network NEVER goes
                // idle because the player keeps fetching media chunks for the full duration,
                // so NetworkIdle reliably times out at ~120s and fails the run. Load fires
                // when the page's load event is dispatched (DOM ready + initial resources
                // fetched), which is what we actually care about for driving playback.
                WaitUntil = WaitUntilState.Load,
                Timeout = 120_000
            }).ConfigureAwait(false);

            await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);

            // Snapshot the post-navigation HTML once. Deterministic session metadata (title,
            // speakers, abstract, …) lives in <script type="application/ld+json">, OpenGraph
            // meta tags and the <title>; extracting it here gives every exit path access to
            // the metadata via the result record. Best-effort — failure must not abort capture.
            SessionMetadata? sessionMetadata = null;
            try
            {
                var html = await page.ContentAsync().ConfigureAwait(false);
                sessionMetadata = SessionMetadataExtractor.Extract(html, request.Url);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Page may have navigated away or been torn down; leave metadata null.
            }

            // Inspect the post-navigation URL and page content to detect a sign-in redirect
            // BEFORE we attempt to drive playback. Without this check a missing/expired
            // browser session manifests as a misleading CAPTURE_DURATION_UNRESOLVED 20s later;
            // here we abort early with an actionable message.
            if (await DetectAuthFailureAsync(page, request, warnings).ConfigureAwait(false))
            {
                IReadOnlyList<BrowserCapturedCaption> authFailCaptions = [];
                if (captionCollector is not null)
                {
                    page.Response -= captionCollector.OnResponse;
                    authFailCaptions = await captionCollector.PersistAsync(cancellationToken).ConfigureAwait(false);
                }
                if (mediaCollector is not null)
                {
                    page.Response -= mediaCollector.OnResponse;
                }
                if (streamInterceptor is not null)
                {
                    page.Response -= streamInterceptor.OnResponse;
                }
                foreach (var interceptor in inlineCaptionInterceptors)
                {
                    page.Response -= interceptor.OnResponse;
                }
                if (debugRecorder is not null)
                {
                    page.Response -= debugRecorder.OnResponse;
                    await debugRecorder.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                return new BrowserCaptureResult([], null, warnings, authFailCaptions, null, sessionMetadata,
                    InlineMediaUrl: inlineCaptionInterceptors.Select(i => i.DiscoveredMediaUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)));
            }

            // MetadataOnly short-circuit. Skip play + duration probe + frame capture; just
            // collect whatever the interceptors / metadata extractor discovered during
            // navigation. Used by FrameCaptureService spot-frame probes that only need the
            // inline media URL — a Medius/Build probe drops from ~25s (duration timeout) to
            // ~3-5s (navigate + read HTML).
            if (request.MetadataOnly)
            {
                // Give late-arriving response bodies (the Medius embed iframe document is
                // typically last) a beat to land in the interceptor before we unsubscribe.
                // 3s is comfortable for Build's iframe-embed shape; empirical observations on
                // KEY01 show the embed document arriving 1-2s after page Load.
                await page.WaitForTimeoutAsync(3_000).ConfigureAwait(false);
                if (captionCollector is not null) page.Response -= captionCollector.OnResponse;
                if (mediaCollector is not null) page.Response -= mediaCollector.OnResponse;
                if (streamInterceptor is not null) page.Response -= streamInterceptor.OnResponse;
                foreach (var interceptor in inlineCaptionInterceptors) page.Response -= interceptor.OnResponse;
                if (debugRecorder is not null)
                {
                    page.Response -= debugRecorder.OnResponse;
                    await debugRecorder.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Still download the inline captions every interceptor discovered, so callers
                // using MetadataOnly for the fast-frames path also get the transcript. Cheap:
                // each caption is one HTTP GET (~200KB for a typical Build session). Loop the
                // registry and adopt the first non-empty result, mirroring the post-frame
                // fallback in the full path.
                IReadOnlyList<BrowserCapturedCaption> metadataCaptions = [];
                foreach (var interceptor in inlineCaptionInterceptors)
                {
                    if (!interceptor.HasDiscoveries) continue;
                    var harvested = await interceptor.DownloadAsync(
                        context, request.CaptionLanguagePreferences ?? [], cancellationToken).ConfigureAwait(false);
                    if (harvested.Count > 0)
                    {
                        metadataCaptions = harvested;
                        break;
                    }
                }

                return new BrowserCaptureResult(
                    Frames: [],
                    DurationSeconds: null,
                    Warnings: warnings,
                    Captions: metadataCaptions,
                    DownloadedMediaPath: null,
                    SessionMetadata: sessionMetadata,
                    InlineMediaUrl: inlineCaptionInterceptors.Select(i => i.DiscoveredMediaUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)));
            }

            await PlayVideoAsync(page, request, warnings, progress).ConfigureAwait(false);

            // Force caption tracks into mode="showing" so the browser actually fetches their
            // cue sources. Many players (SharePoint Stream, Microsoft Stream, YouTube embedded,
            // most HTML5 players) advertise <track> entries on the <video> element but only
            // load the underlying .vtt/.srt when CC is toggled. Activating here triggers the
            // fetch, which the existing CaptionResponseCollector catches.
            await ActivateCaptionTracksAsync(page, request, warnings, progress).ConfigureAwait(false);

            // Snapshot textTracks state for debug analysis (covers both pre- and post-activation
            // via the activated count; the snapshot here reflects post-activation state).
            if (debugRecorder is not null)
            {
                await debugRecorder.SnapshotTextTracksAsync(page, request, cancellationToken).ConfigureAwait(false);
            }

            var duration = await PollDurationAsync(page, request, warnings, progress, cancellationToken).ConfigureAwait(false);
            if (duration is null)
            {
                IReadOnlyList<BrowserCapturedCaption> earlyCaptions = [];
                if (captionCollector is not null)
                {
                    page.Response -= captionCollector.OnResponse;
                    earlyCaptions = await captionCollector.PersistAsync(cancellationToken).ConfigureAwait(false);
                }
                if (mediaCollector is not null)
                {
                    page.Response -= mediaCollector.OnResponse;
                }
                if (streamInterceptor is not null)
                {
                    page.Response -= streamInterceptor.OnResponse;
                }
                foreach (var interceptor in inlineCaptionInterceptors)
                {
                    page.Response -= interceptor.OnResponse;
                }
                if (debugRecorder is not null)
                {
                    page.Response -= debugRecorder.OnResponse;
                    await debugRecorder.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Medius (and similar MSE players) frequently never expose a finite
                // video.duration headlessly, so we land here with zero frames. The transcript,
                // however, is independent of playback: the embed page's inline caption manifest
                // was already parsed during navigation, so download whatever any registered
                // interceptor discovered even though no frames were captured. This is what makes
                // transcript-only Medius/Build/Ignite capture work.
                if (earlyCaptions.Count == 0)
                {
                    foreach (var interceptor in inlineCaptionInterceptors)
                    {
                        if (!interceptor.HasDiscoveries) continue;
                        var harvested = await interceptor.DownloadAsync(
                            context, request.CaptionLanguagePreferences ?? [], cancellationToken).ConfigureAwait(false);
                        if (harvested.Count > 0)
                        {
                            earlyCaptions = harvested;
                            break;
                        }
                    }
                }

                return new BrowserCaptureResult([], null, warnings, earlyCaptions, null, sessionMetadata,
                    InlineMediaUrl: inlineCaptionInterceptors.Select(i => i.DiscoveredMediaUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)));
            }

            var frames = await CaptureFramesAsync(page, request, duration.Value, warnings, progress, cancellationToken).ConfigureAwait(false);

            // Give the page a beat for any late-arriving caption fetches (some players load
            // alternate-language tracks after the user clicks around).
            if (captionCollector is not null)
            {
                await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);
                page.Response -= captionCollector.OnResponse;
            }
            if (mediaCollector is not null)
            {
                page.Response -= mediaCollector.OnResponse;
            }
            if (streamInterceptor is not null)
            {
                page.Response -= streamInterceptor.OnResponse;
            }
            foreach (var interceptor in inlineCaptionInterceptors)
            {
                page.Response -= interceptor.OnResponse;
            }
            if (debugRecorder is not null)
            {
                page.Response -= debugRecorder.OnResponse;
                await debugRecorder.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<BrowserCapturedCaption> captions = captionCollector is null
                ? []
                : await captionCollector.PersistAsync(cancellationToken).ConfigureAwait(false);

            // If network interception didn't yield any captions, harvest cues directly from
            // the <video> element's textTracks API. Players like SharePoint Stream construct
            // their cues client-side (track.addCue) from the player's metadata response, so
            // no .vtt/.srt is ever fetched \u2014 but the cues are sitting in JS memory after we
            // activated the tracks above. Reading them directly and writing synthetic VTTs is
            // the only reliable way to get a transcript from that family of players.
            if (captions.Count == 0)
            {
                var harvested = await HarvestCaptionCuesAsync(page, request, warnings, progress, cancellationToken).ConfigureAwait(false);
                if (harvested.Count > 0)
                {
                    captions = harvested;
                }
            }

            // SharePoint Stream layer: when no captions came out of layers 1-2, check whether
            // we observed the Stream transcript-metadata endpoint during playback. If yes,
            // download each transcript via cookie-auth and convert to VTT. SharePoint Stream's
            // player does NOT use textTracks AND does NOT fetch .vtt files directly, so this
            // is the only layer that works for that platform.
            if (captions.Count == 0 && streamInterceptor is not null)
            {
                var streamCaptions = await streamInterceptor.DownloadAllAsync(context, cancellationToken).ConfigureAwait(false);
                if (streamCaptions.Count > 0)
                {
                    captions = streamCaptions;
                }
            }

            // Inline-caption-interceptor layer: when no captions came from layers 1-3, iterate
            // every registered interceptor profile (Medius today, more to come) and adopt the
            // first non-empty result. Works for Medius/Build/Ignite sessions whose Shaka player
            // never fetched a .vtt on the wire; the registry approach lets future streaming
            // platforms drop in without touching this loop.
            if (captions.Count == 0)
            {
                foreach (var interceptor in inlineCaptionInterceptors)
                {
                    if (!interceptor.HasDiscoveries) continue;
                    var harvested = await interceptor.DownloadAsync(
                        context, request.CaptionLanguagePreferences ?? [], cancellationToken).ConfigureAwait(false);
                    if (harvested.Count > 0)
                    {
                        captions = harvested;
                        break;
                    }
                }
            }

            // STT fallback: only attempt media download if (a) STT was requested, (b) no
            // captions were intercepted, and (c) a candidate media URL was observed. This is
            // the cheapest path that satisfies "only download if we really need to".
            string? downloadedMediaPath = null;
            if (mediaCollector is not null && captions.Count == 0)
            {
                downloadedMediaPath = await mediaCollector.TryDownloadBestCandidateAsync(
                    context, warnings, progress, cancellationToken).ConfigureAwait(false);
            }

            return new BrowserCaptureResult(frames, duration, warnings, captions, downloadedMediaPath, sessionMetadata,
                InlineMediaUrl: inlineCaptionInterceptors.Select(i => i.DiscoveredMediaUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)));
        }
        catch (PlaywrightException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserUnavailable,
                $"Browser capture failed: {ex.Message}",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Error));
            return new BrowserCaptureResult([], null, warnings, [], null);
        }
        finally
        {
            if (context is not null && browser is null)
            {
                // Persistent-context mode: we own the context; dispose it explicitly. In
                // StorageState mode the context lives on the browser, so disposing the
                // browser cleans it up.
                await context.DisposeAsync().ConfigureAwait(false);
            }
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }
            playwright?.Dispose();
        }
    }

    /// <summary>
    /// Inspect the post-navigation URL and (cheaply) page content to detect whether the page
    /// redirected to a sign-in flow or surfaced an MFA challenge. Emits
    /// <c>CAPTURE_BROWSER_AUTH_REQUIRED</c> or <c>CAPTURE_BROWSER_AUTH_MFA_DETECTED</c> as
    /// appropriate. Returns <c>true</c> when capture should be aborted.
    /// </summary>
    private static async Task<bool> DetectAuthFailureAsync(IPage page, BrowserCaptureRequest request, List<ReplayWarning> warnings)
    {
        var finalUrl = page.Url ?? string.Empty;
        if (LooksLikeLoginPage(finalUrl))
        {
            var hint = string.IsNullOrWhiteSpace(request.EdgeUserDataDir)
                ? "Run `zakira-replay auth init-edge-profile --url <site>` (recommended) " +
                  "or `zakira-replay auth login <profile>` and pass `--auth-profile <profile>` to retry."
                : $"Run `zakira-replay auth init-edge-profile --url <site>` to re-sign in against " +
                  $"user-data-dir '{request.EdgeUserDataDir}' and retry.";
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserAuthRequired,
                $"Page redirected to a sign-in URL ({finalUrl}). The browser context is not " +
                $"signed in to the target site. {hint}",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Error));
            return true;
        }

        // Cheap content check for the canonical Microsoft MFA selectors. These selectors
        // come from public Entra ID / Azure AD sign-in HTML; if you see them, the browser
        // is on an interactive MFA challenge that headless Playwright cannot satisfy.
        try
        {
            var mfa = await page.EvaluateAsync<bool>(@"
                () => {
                    if (document.querySelector('#idDiv_SAOTCC_OTC') !== null) return true;
                    if (document.querySelector('#idDiv_SAOTCS_Proofs') !== null) return true;
                    if (document.querySelector('input[name=""otc""]') !== null) return true;
                    if (document.querySelector('#idRichContext_DisplaySign') !== null) return true;
                    return false;
                }").ConfigureAwait(false);
            if (mfa)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureBrowserAuthMfaDetected,
                    $"Page rendered a Microsoft MFA challenge ({finalUrl}) that headless capture " +
                    $"cannot satisfy. Re-run `zakira-replay auth init-edge-profile --url <site>` and " +
                    $"complete MFA interactively to clear the challenge.",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Error));
                return true;
            }
        }
        catch (PlaywrightException)
        {
            // The evaluate failed (unusual). Don't fail the capture for this; let the
            // duration probe make the call.
        }

        return false;
    }

    /// <summary>
    /// Pattern-match the final URL against canonical Microsoft sign-in / SAML / OAuth domains.
    /// Conservative: misses are fine (we fall through to the standard duration probe), false
    /// positives would needlessly abort otherwise-valid captures.
    /// </summary>
    internal static bool LooksLikeLoginPage(string finalUrl)
    {
        if (string.IsNullOrWhiteSpace(finalUrl))
        {
            return false;
        }

        var u = finalUrl.ToLowerInvariant();
        return u.Contains("login.microsoftonline.com")
            || u.Contains("login.live.com")
            || u.Contains("login.windows.net")
            || u.Contains("login.microsoftonline.us")
            || u.Contains("/account/signin")
            || u.Contains("/account/login")
            || u.Contains("/oauth2/authorize")
            || u.Contains("/saml/login")
            || u.Contains("/_layouts/15/wopi.ashx?ru=")
            || u.Contains("login.partner.microsoftonline.cn")
            || u.Contains("idsrv/account/login");
    }

    /// <summary>
    /// Build the Chromium command-line argument array for a single capture. Always includes
    /// <c>--disable-gpu</c> (software rendering path is the stable one for headless), plus
    /// any per-request <see cref="BrowserCaptureRequest.AutoplayPolicy"/> override the
    /// pipeline resolved from the CLI flag / host map / global config default.
    /// </summary>
    /// <param name="extras">Extra static args the caller wants appended verbatim (e.g.
    /// <c>--profile-directory=Default</c> for the persistent-context launch).</param>
    private static IEnumerable<string> BuildChromiumArgs(BrowserCaptureRequest request, params string[] extras)
    {
        var args = new List<string> { "--disable-gpu" };
        args.AddRange(extras);

        // When the resolved policy is "default", ToChromiumArg returns null and nothing is
        // appended — so this path is a no-op for everyone except sources that explicitly
        // need the override (Medius/Build via host map, or --autoplay-policy on the CLI).
        var autoplayArg = AutoplayPolicies.ToChromiumArg(AutoplayPolicies.Normalize(request.AutoplayPolicy));
        if (autoplayArg is not null)
        {
            args.Add(autoplayArg);
        }

        return args;
    }

    private static async Task PlayVideoAsync(IPage page, BrowserCaptureRequest request, List<ReplayWarning> warnings, IProgress<string>? progress)
    {
        progress?.Report("Starting video playback...");

        // MSE/HLS players (Microsoft Medius, Azure Media Player, Shaka) attach the <video>
        // element late and only begin fetching media + caption sidecars once the player is
        // engaged. Navigation uses WaitUntil=Load (streaming pages never reach NetworkIdle), so
        // the player is typically still booting at this point. Wait for the element to attach so
        // the play heuristics below have something to act on; without this the duration probe
        // races ahead and reports CAPTURE_DURATION_UNRESOLVED because querySelector('video') was
        // still null. 30s mirrors the slack the NetworkIdle-based discovery path relies on.
        try
        {
            await page.WaitForSelectorAsync(
                request.VideoElementSelector,
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Element never attached in the top frame (e.g. nested-iframe player). The play
            // heuristics still run; the duration probe will surface the failure if nothing starts.
        }

        // Resolve the frame that owns the <video> so the el.play() and forced-surface-click
        // fallbacks below target the right document (the player may live inside an iframe).
        // Falls back to the main frame, preserving the original single-frame behaviour.
        var videoFrame = await ResolveVideoFrameAsync(page, request.VideoElementSelector).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.PlayButtonSelector))
        {
            try
            {
                await page.Locator(request.PlayButtonSelector).First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 }).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Playwright 1.59 throws System.TimeoutException (not a PlaywrightException) when a
                // locator action times out, so both must be caught here or the run crashes outright.
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CapturePlayButtonNotFound,
                    $"Configured play-button selector '{request.PlayButtonSelector}' did not match: {ex.Message}. Falling back to video.play().",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Info));
            }
        }

        // Fallback 1: call the HTML5 video element's play() directly. el.play() returns a promise
        // that only resolves once playback actually begins, which can hang indefinitely on a long
        // or still-buffering video. EvaluateAsync has no default timeout, so awaiting it directly
        // would block the whole pipeline forever. Race the play() promise against a short in-page
        // timeout: we just need to kick playback, not wait for it to settle.
        try
        {
            var played = await videoFrame.EvaluateAsync<bool>($@"
                async (selector) => {{
                    const el = document.querySelector(selector);
                    if (!el) return false;
                    try {{
                        const playPromise = Promise.resolve(el.play()).catch(() => {{}});
                        const timeout = new Promise((resolve) => setTimeout(resolve, 3000));
                        await Promise.race([playPromise, timeout]);
                        // Only report success when playback actually engaged. Autoplay-with-sound
                        // is blocked by default, in which case el.play() rejects silently and the
                        // element stays paused — fall through to the click heuristics below.
                        return !el.paused;
                    }} catch {{ return false; }}
                }}", request.VideoElementSelector).ConfigureAwait(false);
            if (played)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // ignored — fall through to the aria-label heuristic
        }

        // Fallback 2: click the first visible play-labelled button.
        try
        {
            await page.Locator("button[aria-label*='play' i]").First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CapturePlayButtonNotFound,
                $"Could not start playback: {ex.Message}. Capture may fail at the duration probe.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Warning));
        }

        // Fallback 3: click the player surface itself. MSE players (Medius / Azure Media Player)
        // often bind click-to-play on the <video> element or a poster overlay rather than a
        // button with an aria-label, so a direct (forced) click on the video surface starts
        // playback where the heuristics above don't. Best-effort: failures are non-fatal.
        try
        {
            await videoFrame.Locator(request.VideoElementSelector).First.ClickAsync(
                new LocatorClickOptions { Timeout = 5_000, Force = true }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored — the duration probe is the final arbiter of whether playback started.
        }
    }

    /// <summary>
    /// Walk the <c>&lt;video&gt;</c> element's <c>textTracks</c> list and set every captions /
    /// subtitles track to <c>mode = "showing"</c>. This is what the browser does when the user
    /// clicks CC; without it, players like SharePoint Stream advertise tracks but never fetch
    /// their cue sources. Activating here lets <see cref="CaptionResponseCollector"/> see the
    /// resulting <c>.vtt</c> / <c>.srt</c> network responses. Also tries clicking a
    /// CC-labelled button as a backup for players that render captions outside the standard
    /// HTML5 textTracks contract (Medius / Stream older Web App may need this).
    /// </summary>
    /// <remarks>
    /// Best effort: failures are non-fatal. If neither path activates captions, the existing
    /// <c>CAPTIONS_BROWSER_NETWORK_NONE</c> warning will still fire at the end of capture so
    /// the orchestrator can see no captions were observed.
    /// </remarks>
    private static async Task ActivateCaptionTracksAsync(
        IPage page,
        BrowserCaptureRequest request,
        List<ReplayWarning> warnings,
        IProgress<string>? progress)
    {
        progress?.Report("Activating caption tracks...");
        int activated = 0;
        try
        {
            activated = await page.EvaluateAsync<int>($@"
                (selector) => {{
                    const el = document.querySelector(selector);
                    if (!el || !el.textTracks) return 0;
                    let count = 0;
                    for (const t of el.textTracks) {{
                        try {{
                            // 'captions' and 'subtitles' are the standard kinds; 'descriptions'
                            // (audio descriptions) and 'chapters' / 'metadata' are not useful
                            // for STT-substitute transcripts. Be permissive on kind for older
                            // players that mislabel \u2014 if it's not 'descriptions', try it.
                            const k = (t.kind || '').toLowerCase();
                            if (k === 'descriptions' || k === 'chapters' || k === 'metadata') continue;
                            if (t.mode !== 'showing') {{
                                t.mode = 'showing';
                                count++;
                            }}
                        }} catch (_) {{
                            // ignore per-track failures
                        }}
                    }}
                    return count;
                }}", request.VideoElementSelector).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Page may have navigated away or the video element is unavailable. The fallback
            // below will still try the CC-button heuristic.
        }

        if (activated == 0)
        {
            // Fallback: click a CC-labelled toggle button. Common selectors across players.
            try
            {
                await page.EvaluateAsync(@"
                    () => {
                        const candidates = [
                            'button[aria-label*=""subtitle"" i]',
                            'button[aria-label*=""caption"" i]',
                            'button[aria-label*=""cc"" i]',
                            'button[data-track-kind=""captions""]',
                            '[role=""button""][aria-label*=""caption"" i]',
                            '[role=""button""][aria-label*=""subtitle"" i]'
                        ];
                        for (const sel of candidates) {
                            const b = document.querySelector(sel);
                            if (b) { try { b.click(); return; } catch (_) {} }
                        }
                    }
                ").ConfigureAwait(false);
                // Give the click a beat to register and the player to load the track.
                await page.WaitForTimeoutAsync(750).ConfigureAwait(false);

                // Re-poll textTracks in case the click loaded them post-hoc.
                activated = await page.EvaluateAsync<int>($@"
                    (selector) => {{
                        const el = document.querySelector(selector);
                        if (!el || !el.textTracks) return 0;
                        let count = 0;
                        for (const t of el.textTracks) {{
                            if (t.mode === 'showing') count++;
                        }}
                        return count;
                    }}", request.VideoElementSelector).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // ignore
            }
        }

        if (activated > 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserCaptionsActivated,
                $"Activated {activated} caption track(s) on the <video> element so the player would fetch cue sources.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Info));
        }
    }

    /// <summary>
    /// Read caption cues directly out of the <c>&lt;video&gt;</c> element's <c>textTracks</c>
    /// API and serialise them to VTT files. This is the fallback for players that build cues
    /// in JavaScript (<c>track.addCue()</c>) rather than fetching a <c>.vtt</c> over the wire
    /// \u2014 the network interceptor sees nothing in that case, but the cues are still in JS memory.
    /// </summary>
    /// <remarks>
    /// SharePoint Stream specifically constructs its caption cues client-side from the
    /// player's metadata response (no separate <c>.vtt</c> network fetch). Reading directly
    /// from <c>track.cues</c> is the only reliable way to harvest them. Cues may load
    /// asynchronously after <c>track.mode = "showing"</c>; we poll briefly to give the load
    /// a chance to complete.
    /// </remarks>
    private static async Task<IReadOnlyList<BrowserCapturedCaption>> HarvestCaptionCuesAsync(
        IPage page,
        BrowserCaptureRequest request,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var captionsDir = request.Run.GetPath("captions");

        // Poll for up to ~5 seconds; some players load cues asynchronously after activation.
        // Each poll snapshots cue counts so we know when loading has stabilised.
        HarvestedTrack[] tracks = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var lastTotalCues = -1;
        var stableCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                tracks = await page.EvaluateAsync<HarvestedTrack[]>($@"
                    (selector) => {{
                        const el = document.querySelector(selector);
                        if (!el || !el.textTracks) return [];
                        const out = [];
                        for (let i = 0; i < el.textTracks.length; i++) {{
                            const t = el.textTracks[i];
                            const k = (t.kind || '').toLowerCase();
                            if (k === 'descriptions' || k === 'chapters' || k === 'metadata') continue;
                            const cues = [];
                            if (t.cues) {{
                                for (let j = 0; j < t.cues.length; j++) {{
                                    const c = t.cues[j];
                                    if (typeof c.startTime !== 'number' || typeof c.endTime !== 'number') continue;
                                    const text = (c.text || '').toString();
                                    if (!text) continue;
                                    cues.push({{
                                        startTime: c.startTime,
                                        endTime: c.endTime,
                                        text: text
                                    }});
                                }}
                            }}
                            out.push({{
                                index: i,
                                kind: t.kind || null,
                                label: t.label || null,
                                language: t.language || null,
                                mode: t.mode || null,
                                cueCount: cues.length,
                                cues: cues
                            }});
                        }}
                        return out;
                    }}", request.VideoElementSelector).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                break;
            }

            var totalCues = tracks.Sum(t => t.CueCount);
            if (totalCues == 0)
            {
                await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
                continue;
            }
            if (totalCues == lastTotalCues)
            {
                stableCount++;
                if (stableCount >= 2)
                {
                    break;
                }
            }
            else
            {
                stableCount = 0;
                lastTotalCues = totalCues;
            }
            await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
        }

        if (tracks.Length == 0)
        {
            return [];
        }

        Directory.CreateDirectory(captionsDir);
        var captions = new List<BrowserCapturedCaption>();
        var totalHarvested = 0;
        var ordinalBase = 1000; // reserve ordinals so we don't collide with network-intercepted ones
        for (var i = 0; i < tracks.Length; i++)
        {
            var track = tracks[i];
            if (track.Cues is null || track.Cues.Length == 0)
            {
                continue;
            }

            var vtt = SerializeTrackToVtt(track);
            var bytes = System.Text.Encoding.UTF8.GetBytes(vtt);
            var languagePart = string.IsNullOrWhiteSpace(track.Language) ? "track" : track.Language.Trim();
            var fileName = $"texttrack-{(i + 1):0000}-{languagePart}.vtt";
            var relativePath = $"captions/{fileName}";
            var fullPath = Path.Combine(captionsDir, fileName);
            try
            {
                await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptionsBrowserNetworkDownloadFailed,
                    $"Failed to persist harvested caption track {i + 1} (lang '{track.Language}') to {fullPath}: {ex.Message}",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Warning));
                continue;
            }

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            captions.Add(new BrowserCapturedCaption(
                Ordinal: ordinalBase + i + 1,
                Url: $"texttrack://video.textTracks[{track.Index}]",
                RelativePath: relativePath,
                InferredLanguage: string.IsNullOrWhiteSpace(track.Language) ? null : track.Language,
                LanguageSource: string.IsNullOrWhiteSpace(track.Language) ? null : "html5-texttrack-language-attr",
                ByteCount: bytes.LongLength,
                ContentSha256: hash,
                ContentType: "text/vtt"));
            totalHarvested += track.CueCount;
        }

        if (captions.Count > 0)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserCaptionsHarvestedFromDom,
                $"Harvested {totalHarvested} cue(s) across {captions.Count} caption track(s) directly from the <video> textTracks API.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Info));
            progress?.Report($"Harvested {totalHarvested} caption cues from {captions.Count} track(s).");
        }

        return captions;
    }

    private static string SerializeTrackToVtt(HarvestedTrack track)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        for (var i = 0; i < track.Cues!.Length; i++)
        {
            var cue = track.Cues[i];
            sb.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.Append(FormatVttTimestamp(cue.StartTime));
            sb.Append(" --> ");
            sb.Append(FormatVttTimestamp(cue.EndTime));
            sb.AppendLine();
            // Normalise newlines in the cue text so the resulting file stays valid.
            sb.AppendLine(cue.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim());
            sb.AppendLine();
        }
        return sb.ToString();
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

    private sealed record HarvestedTrack(
        int Index,
        string? Kind,
        string? Label,
        string? Language,
        string? Mode,
        int CueCount,
        HarvestedCue[]? Cues);

    private sealed record HarvestedCue(double StartTime, double EndTime, string Text);

    private static async Task<double?> PollDurationAsync(IPage page, BrowserCaptureRequest request, List<ReplayWarning> warnings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Probing video duration...");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(request.DurationProbeTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Re-resolve each iteration: MSE players (and the wrapper-page → iframe case)
                // attach the <video> late and possibly in a child frame, so the frame that owns
                // the element can change between polls.
                var frame = await ResolveVideoFrameAsync(page, request.VideoElementSelector).ConfigureAwait(false);
                var duration = await frame.EvaluateAsync<double?>($@"
                    (selector) => {{
                        const el = document.querySelector(selector);
                        if (!el) return null;
                        const d = el.duration;
                        return Number.isFinite(d) && d > 0 ? d : null;
                    }}", request.VideoElementSelector).ConfigureAwait(false);
                if (duration is { } d && d > 0)
                {
                    return d;
                }
            }
            catch (PlaywrightException)
            {
                // ignore transient evaluation errors; retry until deadline
            }

            await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);
        }

        warnings.Add(new ReplayWarning(
            ReplayWarningCodes.CaptureDurationUnresolved,
            $"video.duration did not become a finite number within {request.DurationProbeTimeoutSeconds:F1}s. The video may not have started playing or the page does not expose a media element matching '{request.VideoElementSelector}'.",
            Source: "playwright",
            Severity: ReplayWarningSeverities.Error));
        return null;
    }

    /// <summary>
    /// Find the frame whose DOM contains an element matching <paramref name="selector"/>. Players
    /// embedded via an iframe (e.g. a Microsoft Medius player inside a build.microsoft.com session
    /// page) expose the <c>&lt;video&gt;</c> inside a child frame, which <see cref="IPage.Locator"/>
    /// and <see cref="IPage.EvaluateAsync"/> (both main-frame-scoped) never see. Playwright can
    /// drive cross-origin child frames, so we iterate them. Falls back to the main frame when no
    /// frame matches, preserving the single-frame behaviour exactly.
    /// </summary>
    private static async Task<IFrame> ResolveVideoFrameAsync(IPage page, string selector)
    {
        // Prefer the main frame: cheapest, and correct for the overwhelmingly common case where
        // the player isn't iframed.
        try
        {
            var inMain = await page.MainFrame.EvaluateAsync<bool>(
                "(s) => document.querySelector(s) !== null", selector).ConfigureAwait(false);
            if (inMain)
            {
                return page.MainFrame;
            }
        }
        catch (PlaywrightException)
        {
            // fall through to scan child frames
        }

        foreach (var frame in page.Frames)
        {
            if (ReferenceEquals(frame, page.MainFrame))
            {
                continue;
            }
            try
            {
                var found = await frame.EvaluateAsync<bool>(
                    "(s) => document.querySelector(s) !== null", selector).ConfigureAwait(false);
                if (found)
                {
                    return frame;
                }
            }
            catch (PlaywrightException)
            {
                // Frame may be detached / navigating / not yet evaluable; skip it.
            }
        }

        return page.MainFrame;
    }

    private static async Task<IReadOnlyList<FrameArtifact>> CaptureFramesAsync(
        IPage page,
        BrowserCaptureRequest request,
        double durationSeconds,
        List<ReplayWarning> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var frameCount = Math.Max(1, request.FrameCount);
        var timestamps = ComputeTimestamps(durationSeconds, frameCount);
        var framesDirectory = request.Run.GetPath("frames");
        Directory.CreateDirectory(framesDirectory);

        var seekWaitMs = (int)Math.Round(Math.Max(0.0, request.SeekWaitSeconds) * 1000);
        // Resolve the frame that actually owns the <video> once up front; seek + screenshot must
        // target that frame (the video may live inside an iframe). Falls back to the main frame.
        var videoFrame = await ResolveVideoFrameAsync(page, request.VideoElementSelector).ConfigureAwait(false);
        var locator = videoFrame.Locator(request.VideoElementSelector).First;
        var frames = new List<FrameArtifact>(timestamps.Count);
        var seekFailureSeen = false;
        for (var i = 0; i < timestamps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seconds = timestamps[i];
            var label = Timestamp.Format(seconds);
            var id = $"scene-{i + 1:0000}";
            var relativePath = $"frames/{id}.jpg";
            var fullPath = request.Run.GetPath(relativePath);

            try
            {
                await videoFrame.EvaluateAsync(
                    $@"
                    (args) => {{
                        const el = document.querySelector(args.selector);
                        if (!el) throw new Error('video element not found');
                        el.currentTime = args.seconds;
                    }}",
                    new { selector = request.VideoElementSelector, seconds }).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                if (!seekFailureSeen)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureSeekFailed,
                        $"Seek to {seconds:F2}s failed: {ex.Message}",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                    seekFailureSeen = true;
                }
                continue;
            }

            await page.WaitForTimeoutAsync(seekWaitMs).ConfigureAwait(false);

            progress?.Report($"Capturing browser frame {id} at {label}...");
            try
            {
                await locator.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = fullPath,
                    Type = ScreenshotType.Jpeg,
                    Quality = request.JpegQuality
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureScreenshotFailed,
                    $"Screenshot for {id} at {label} failed: {ex.Message}",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Warning));
                continue;
            }

            frames.Add(new FrameArtifact(id, relativePath, seconds, label));
        }

        return frames;
    }

    internal static IReadOnlyList<double> ComputeTimestamps(double durationSeconds, int frameCount)
    {
        // Match the reference SKILL: evenly spaced timestamps strictly inside the (0, duration)
        // interval. For N frames: duration / (N + 1) * {1..N}.
        if (durationSeconds <= 0 || frameCount <= 0)
        {
            return [];
        }

        var interval = durationSeconds / (frameCount + 1);
        var timestamps = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            timestamps[i] = Math.Round(interval * (i + 1), 3, MidpointRounding.AwayFromZero);
        }
        return timestamps;
    }

    /// <summary>
    /// Network-event listener that collects every caption-shaped response, awaits its body off
    /// the event-loop thread, dedupes by SHA-256, and persists the surviving files plus a JSON
    /// manifest under the run's <c>captions/</c> directory.
    /// </summary>
    private sealed class CaptionResponseCollector
    {
        private readonly BrowserCaptureRequest request;
        private readonly List<ReplayWarning> warnings;
        private readonly List<Task<CapturedCaptionPayload?>> pending = [];
        private readonly object pendingLock = new();

        public CaptionResponseCollector(BrowserCaptureRequest request, List<ReplayWarning> warnings)
        {
            this.request = request;
            this.warnings = warnings;
        }

        public void OnResponse(object? sender, IResponse response)
        {
            // Response handlers run on the Playwright event-loop thread; do as little here as
            // possible. Everything async (body fetch, file IO) happens later in PersistAsync.
            if (!BrowserCaptionInterceptor.IsCaptionUrl(response.Url))
            {
                return;
            }

            if (response.Status >= 400)
            {
                return;
            }

            var task = LoadBodyAsync(response);
            lock (pendingLock)
            {
                pending.Add(task);
            }
        }

        public async Task<IReadOnlyList<BrowserCapturedCaption>> PersistAsync(CancellationToken cancellationToken)
        {
            Task<CapturedCaptionPayload?>[] snapshot;
            lock (pendingLock)
            {
                snapshot = pending.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return [];
            }

            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                // Per-response failures are surfaced via the payload's Warning; we never want
                // a single bad response to abort the whole capture.
            }

            var payloads = snapshot
                .Select(task => task.IsCompletedSuccessfully ? task.Result : null)
                .OfType<CapturedCaptionPayload>()
                .ToArray();

            // Surface per-response warnings (download failure, oversize body) collected during
            // body loading.
            foreach (var payload in payloads)
            {
                if (payload.Warning is not null)
                {
                    warnings.Add(payload.Warning);
                }
            }

            var captionPayloads = payloads.Where(payload => payload.Bytes is not null).ToArray();
            if (captionPayloads.Length == 0)
            {
                return [];
            }

            var captionsDirectory = request.Run.GetPath("captions");
            Directory.CreateDirectory(captionsDirectory);

            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captions = new List<BrowserCapturedCaption>();
            var ordinal = 0;
            foreach (var payload in captionPayloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenHashes.Add(payload.ContentSha256!))
                {
                    continue;
                }

                ordinal++;
                var extension = ExtensionFor(payload.Url);
                var fileName = $"browser-{ordinal:0000}{extension}";
                var relativePath = $"captions/{fileName}";
                var fullPath = Path.Combine(captionsDirectory, fileName);
                try
                {
                    await File.WriteAllBytesAsync(fullPath, payload.Bytes!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptionsBrowserNetworkDownloadFailed,
                        $"Failed to persist browser-captured caption from {payload.Url}: {ex.Message}",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                    continue;
                }

                var (language, languageSource) = BrowserCaptionInterceptor.InferLanguageFromUrl(payload.Url);
                captions.Add(new BrowserCapturedCaption(
                    Ordinal: ordinal,
                    Url: payload.Url,
                    RelativePath: relativePath,
                    InferredLanguage: language,
                    LanguageSource: languageSource,
                    ByteCount: payload.Bytes!.LongLength,
                    ContentSha256: payload.ContentSha256!,
                    ContentType: payload.ContentType));
            }

            return captions;
        }

        private async Task<CapturedCaptionPayload?> LoadBodyAsync(IResponse response)
        {
            var url = response.Url;
            string? contentType = null;
            try
            {
                var headers = await response.AllHeadersAsync().ConfigureAwait(false);
                if (headers.TryGetValue("content-type", out var headerValue))
                {
                    contentType = headerValue;
                }

                byte[] body;
                try
                {
                    body = await response.BodyAsync().ConfigureAwait(false);
                }
                catch (PlaywrightException ex)
                {
                    return new CapturedCaptionPayload(url, null, null, contentType, new ReplayWarning(
                        ReplayWarningCodes.CaptionsBrowserNetworkDownloadFailed,
                        $"Could not read body for caption response {url}: {ex.Message}",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                }

                if (body.LongLength > request.MaxCaptionBytes)
                {
                    return new CapturedCaptionPayload(url, null, null, contentType, new ReplayWarning(
                        ReplayWarningCodes.CaptionsBrowserNetworkDownloadFailed,
                        $"Caption response from {url} exceeded maxCaptionBytes ({body.LongLength} > {request.MaxCaptionBytes}); skipped.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                }

                if (body.LongLength == 0)
                {
                    return null;
                }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
                return new CapturedCaptionPayload(url, body, hash, contentType, null);
            }
            catch (Exception ex)
            {
                return new CapturedCaptionPayload(url, null, null, contentType, new ReplayWarning(
                    ReplayWarningCodes.CaptionsBrowserNetworkDownloadFailed,
                    $"Unexpected failure capturing caption response from {url}: {ex.Message}",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Warning));
            }
        }

        private static string ExtensionFor(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                {
                    return ".srt";
                }
                return ".vtt";
            }
            catch
            {
                return ".vtt";
            }
        }

        private sealed record CapturedCaptionPayload(string Url, byte[]? Bytes, string? ContentSha256, string? ContentType, ReplayWarning? Warning);
    }

    /// <summary>
    /// Lightweight network observer that records candidate media responses (video/*, audio/*,
    /// HLS / DASH manifests) seen during playback so we can attempt an authenticated re-download
    /// as STT fallback when no inline captions were intercepted. We deliberately do NOT
    /// download bodies inline: streaming videos can be hundreds of megabytes and would blow up
    /// memory if we held every response body. Instead we record URL + content-type + size from
    /// response headers, then issue ONE targeted download after capture finishes.
    /// </summary>
    /// <remarks>
    /// Best effort: works when the server hands back a single addressable media URL (typical
    /// SharePoint Stream pattern when the recording was uploaded as a single MP4). Won't help
    /// for HLS / DASH chunked streams where audio is split across hundreds of <c>.m4s</c>
    /// fragments \u2014 those would need manifest parsing + segment reassembly, which is left for a
    /// future change. When no candidate is found we emit <c>CAPTURE_BROWSER_MEDIA_NO_CANDIDATE</c>
    /// so the orchestrator can branch.
    /// </remarks>
    private sealed class MediaResponseCollector
    {
        private readonly BrowserCaptureRequest request;
        private readonly List<MediaCandidate> candidates = [];
        private readonly object lockObj = new();

        public MediaResponseCollector(BrowserCaptureRequest request)
        {
            this.request = request;
        }

        public void OnResponse(object? sender, IResponse response)
        {
            // Stay off the body during the event handler \u2014 just sniff URL + headers.
            try
            {
                if (response.Status >= 400)
                {
                    return;
                }

                var url = response.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                // Skip responses we know aren't media.
                if (LooksLikeKnownNonMedia(url))
                {
                    return;
                }

                var headers = response.Headers;
                string? contentType = null;
                long contentLength = 0;
                if (headers is not null)
                {
                    if (headers.TryGetValue("content-type", out var ct))
                    {
                        contentType = ct;
                    }
                    if (headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var parsedLen))
                    {
                        contentLength = parsedLen;
                    }
                }

                if (!LooksLikeMedia(url, contentType))
                {
                    return;
                }

                lock (lockObj)
                {
                    candidates.Add(new MediaCandidate(url, contentType, contentLength));
                }
            }
            catch
            {
                // Never let a sniff failure surface; this is a side-channel observer.
            }
        }

        public async Task<string?> TryDownloadBestCandidateAsync(
            IBrowserContext context,
            List<ReplayWarning> warnings,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            MediaCandidate[] snapshot;
            lock (lockObj)
            {
                snapshot = candidates.ToArray();
            }

            if (snapshot.Length == 0)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureBrowserMediaNoCandidate,
                    "No suitable single-file media URL was observed during browser capture. " +
                    "Common cause: the player streams audio/video as DASH or HLS fragments " +
                    "(SharePoint Stream uses fragmented MP4 / `part=mediasegment` URLs which " +
                    "decode to nothing without a companion init segment). STT will be skipped. " +
                    "If you need STT for this source, supply the audio via `--audio` or " +
                    "configure a yt-dlp cookie path that can download the original.",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Info));
                return null;
            }

            // Group by URL-without-query so byte-range responses targeting the same resource
            // collapse to one candidate. Pick the one with the highest content-length advertised
            // (since byte-range responses report partial sizes, the full-file response will dwarf
            // them). This is a heuristic; chunked streams will still produce many tiny candidates.
            var grouped = snapshot
                .GroupBy(c => TrimQueryAndFragment(c.Url))
                .Select(g => new
                {
                    BaseUrl = g.Key,
                    BestUrl = g.OrderByDescending(c => c.ContentLength).First().Url,
                    MaxContentLength = g.Max(c => c.ContentLength),
                    Count = g.Count(),
                    ContentType = g.Select(c => c.ContentType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                })
                .OrderByDescending(g => g.MaxContentLength)
                .ToArray();

            var top = grouped[0];
            if (top.MaxContentLength <= 0)
            {
                // No size info, but try the largest-by-count anyway.
                top = grouped.OrderByDescending(g => g.Count).First();
            }

            var mediaDir = request.Run.GetPath("media");
            Directory.CreateDirectory(mediaDir);
            // Use a stable filename so re-runs against the same source overwrite cleanly.
            var extension = InferExtension(top.BestUrl, top.ContentType);
            var outputPath = Path.Combine(mediaDir, $"browser-fetched{extension}");

            progress?.Report($"Downloading media for STT from authenticated context: {top.BestUrl}");

            try
            {
                var apiResponse = await context.APIRequest.GetAsync(top.BestUrl, new APIRequestContextOptions
                {
                    Timeout = 300_000 // 5 minutes for the full file
                }).ConfigureAwait(false);

                if (!apiResponse.Ok)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserMediaDownloadFailed,
                        $"Authenticated media download returned HTTP {apiResponse.Status} for {top.BestUrl}. STT will be skipped.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                    return null;
                }

                var body = await apiResponse.BodyAsync().ConfigureAwait(false);
                if (body.Length == 0)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserMediaDownloadFailed,
                        $"Authenticated media download returned an empty body for {top.BestUrl}. STT will be skipped.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                    return null;
                }

                if (request.MaxMediaBytes > 0 && body.Length > request.MaxMediaBytes)
                {
                    warnings.Add(new ReplayWarning(
                        ReplayWarningCodes.CaptureBrowserMediaDownloadFailed,
                        $"Media response from {top.BestUrl} exceeded maxMediaBytes ({body.Length} > {request.MaxMediaBytes}); discarded. STT will be skipped.",
                        Source: "playwright",
                        Severity: ReplayWarningSeverities.Warning));
                    return null;
                }

                await File.WriteAllBytesAsync(outputPath, body, cancellationToken).ConfigureAwait(false);

                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureBrowserMediaDownloaded,
                    $"Downloaded media for STT fallback ({body.Length:N0} bytes) from {top.BestUrl} \u2192 {outputPath}.",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Info));
                return outputPath;
            }
            catch (Exception ex)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CaptureBrowserMediaDownloadFailed,
                    $"Authenticated media download threw for {top.BestUrl}: {ex.Message}. STT will be skipped.",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Warning));
                return null;
            }
        }

        private static bool LooksLikeMedia(string url, string? contentType)
        {
            // Filter out URLs that clearly identify themselves as DASH / fMP4 fragments. These
            // are individual segments (moof+mdat boxes) that decode to nothing without the
            // companion init segment. SharePoint Stream serves video this way, so this is the
            // single biggest source of false positives.
            if (LooksLikeStreamFragment(url))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var ct = contentType.ToLowerInvariant();
                if (ct.StartsWith("video/", StringComparison.Ordinal)
                    || ct.StartsWith("audio/", StringComparison.Ordinal)
                    || ct.Contains("mpegurl", StringComparison.Ordinal)        // HLS .m3u8
                    || ct.Contains("dash+xml", StringComparison.Ordinal)        // DASH .mpd
                    || ct.Equals("application/mp4", StringComparison.Ordinal)
                    || ct.Equals("application/x-mp4", StringComparison.Ordinal))
                {
                    return true;
                }

                // application/octet-stream is too noisy to whitelist by content-type alone;
                // fall through to URL inspection below.
                if (ct != "application/octet-stream")
                {
                    return false;
                }
            }

            try
            {
                var path = new Uri(url).AbsolutePath.ToLowerInvariant();
                return path.EndsWith(".mp4", StringComparison.Ordinal)
                    || path.EndsWith(".m4a", StringComparison.Ordinal)
                    || path.EndsWith(".m4s", StringComparison.Ordinal)
                    || path.EndsWith(".webm", StringComparison.Ordinal)
                    || path.EndsWith(".mov", StringComparison.Ordinal)
                    || path.EndsWith(".mp3", StringComparison.Ordinal)
                    || path.EndsWith(".aac", StringComparison.Ordinal)
                    || path.EndsWith(".ogg", StringComparison.Ordinal)
                    || path.EndsWith(".opus", StringComparison.Ordinal)
                    || path.EndsWith(".m3u8", StringComparison.Ordinal)
                    || path.EndsWith(".mpd", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true when the URL is recognisably a DASH / fMP4 fragment URL: SharePoint
        /// Stream's <c>oneDrive.transcode?part=mediasegment&amp;segmentTime=...</c> pattern, an
        /// HLS <c>.m4s</c> chunk, or anything else that obviously identifies as a single
        /// streamed segment rather than a self-contained media file. Skipping these avoids
        /// wasting an authenticated download on a file ffmpeg will reject with "moov atom not
        /// found".
        /// </summary>
        private static bool LooksLikeStreamFragment(string url)
        {
            try
            {
                var u = new Uri(url);
                var path = u.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith(".m4s", StringComparison.Ordinal))
                {
                    return true; // canonical DASH segment extension
                }

                var query = u.Query.ToLowerInvariant();
                // SharePoint Stream / oneDrive.transcode fragmented-MP4 pattern.
                if (query.Contains("part=mediasegment", StringComparison.Ordinal)
                    || query.Contains("segmenttime=", StringComparison.Ordinal)
                    || query.Contains("part=mediainit", StringComparison.Ordinal))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeKnownNonMedia(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath.ToLowerInvariant();
                // Static asset extensions worth fast-filtering before doing header parsing.
                return path.EndsWith(".js", StringComparison.Ordinal)
                    || path.EndsWith(".css", StringComparison.Ordinal)
                    || path.EndsWith(".woff", StringComparison.Ordinal)
                    || path.EndsWith(".woff2", StringComparison.Ordinal)
                    || path.EndsWith(".ttf", StringComparison.Ordinal)
                    || path.EndsWith(".svg", StringComparison.Ordinal)
                    || path.EndsWith(".png", StringComparison.Ordinal)
                    || path.EndsWith(".jpg", StringComparison.Ordinal)
                    || path.EndsWith(".jpeg", StringComparison.Ordinal)
                    || path.EndsWith(".gif", StringComparison.Ordinal)
                    || path.EndsWith(".ico", StringComparison.Ordinal)
                    || path.EndsWith(".webp", StringComparison.Ordinal)
                    || path.EndsWith(".html", StringComparison.Ordinal)
                    || path.EndsWith(".htm", StringComparison.Ordinal)
                    || path.EndsWith(".vtt", StringComparison.Ordinal)
                    || path.EndsWith(".srt", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string TrimQueryAndFragment(string url)
        {
            try
            {
                var u = new Uri(url);
                return $"{u.Scheme}://{u.Authority}{u.AbsolutePath}";
            }
            catch
            {
                return url;
            }
        }

        private static string InferExtension(string url, string? contentType)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                var ext = Path.GetExtension(path);
                if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 6)
                {
                    return ext.ToLowerInvariant();
                }
            }
            catch
            {
                // ignore
            }

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var ct = contentType.ToLowerInvariant();
                if (ct.Contains("mp4", StringComparison.Ordinal)) return ".mp4";
                if (ct.Contains("webm", StringComparison.Ordinal)) return ".webm";
                if (ct.Contains("mpeg", StringComparison.Ordinal)) return ".mp3";
                if (ct.Contains("aac", StringComparison.Ordinal)) return ".aac";
                if (ct.Contains("ogg", StringComparison.Ordinal)) return ".ogg";
            }

            return ".bin";
        }

        private sealed record MediaCandidate(string Url, string? ContentType, long ContentLength);
    }

    /// <summary>
    /// Diagnostic recorder: writes one row per response to <c>debug/network.log</c> (JSONL),
    /// dumps eligible JSON/XML/text bodies to <c>debug/metadata-responses/</c>, and (after
    /// activation) snapshots <c>&lt;video&gt;.textTracks</c> state. Used by Phase 1 of the
    /// SharePoint Stream investigation; also useful for reverse-engineering any new vendor.
    /// </summary>
    /// <remarks>
    /// Strictly side-channel: never throws, never blocks the capture pipeline. Body downloads
    /// happen asynchronously and are awaited only in <see cref="FlushAsync"/>. Bodies larger
    /// than <see cref="BrowserCaptureRequest.DebugMaxBodyBytes"/> are recorded in the log but
    /// not persisted to <c>metadata-responses/</c>.
    /// </remarks>
    private sealed class DebugNetworkRecorder
    {
        private readonly BrowserCaptureRequest request;
        private readonly string debugDirectory;
        private readonly string responsesDirectory;
        private readonly List<Task<RecordedResponse?>> pending = [];
        private readonly object lockObj = new();
        private int seqCounter;

        public DebugNetworkRecorder(BrowserCaptureRequest request)
        {
            this.request = request;
            this.debugDirectory = request.Run.GetPath("debug");
            this.responsesDirectory = Path.Combine(this.debugDirectory, "metadata-responses");
            Directory.CreateDirectory(this.responsesDirectory);
        }

        public void OnResponse(object? sender, IResponse response)
        {
            try
            {
                var seq = System.Threading.Interlocked.Increment(ref seqCounter);
                var task = CaptureResponseAsync(seq, response);
                lock (lockObj)
                {
                    pending.Add(task);
                }
            }
            catch
            {
                // never throw from the event handler
            }
        }

        private async Task<RecordedResponse?> CaptureResponseAsync(int seq, IResponse response)
        {
            try
            {
                var url = response.Url;
                var method = string.Empty;
                try { method = response.Request.Method; } catch { /* ignore */ }
                var status = response.Status;

                Dictionary<string, string>? headers = null;
                try
                {
                    headers = await response.AllHeadersAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Some Playwright responses can't surface headers after the body completes.
                }

                string? contentType = null;
                long contentLength = 0;
                if (headers is not null)
                {
                    if (headers.TryGetValue("content-type", out var ct))
                    {
                        contentType = ct;
                    }
                    if (headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var parsedLen))
                    {
                        contentLength = parsedLen;
                    }
                }

                // Determine if we should persist the body. We persist text-shaped responses
                // (JSON, XML, text/*) under the configured size cap. Binary bodies (video,
                // audio, octet-stream) are logged but not dumped \u2014 they'd blow up disk usage
                // for very little diagnostic value.
                string? bodyRelativePath = null;
                long bodyBytes = 0;
                string? bodySha256 = null;
                var shouldPersist = ShouldPersistBody(contentType);
                if (shouldPersist)
                {
                    try
                    {
                        var body = await response.BodyAsync().ConfigureAwait(false);
                        bodyBytes = body.LongLength;
                        if (body.LongLength > 0 && body.LongLength <= request.DebugMaxBodyBytes)
                        {
                            bodySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
                            var ext = InferExtension(contentType);
                            var fileName = $"{seq:0000}-{bodySha256[..8]}{ext}";
                            var fullPath = Path.Combine(responsesDirectory, fileName);
                            await File.WriteAllBytesAsync(fullPath, body).ConfigureAwait(false);
                            bodyRelativePath = $"debug/metadata-responses/{fileName}";
                        }
                    }
                    catch
                    {
                        // Body unavailable (response not finished, navigation, etc.). Just log
                        // the headers without a body pointer.
                    }
                }

                return new RecordedResponse(
                    Seq: seq,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Url: url,
                    Method: method,
                    Status: status,
                    ContentType: contentType,
                    ContentLength: contentLength,
                    Headers: headers,
                    BodyRelativePath: bodyRelativePath,
                    BodyBytes: bodyBytes,
                    BodySha256: bodySha256);
            }
            catch
            {
                return null;
            }
        }

        public async Task SnapshotTextTracksAsync(IPage page, BrowserCaptureRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await page.EvaluateAsync<object>($@"
                    (selector) => {{
                        const el = document.querySelector(selector);
                        if (!el || !el.textTracks) return {{ found: false }};
                        const tracks = [];
                        for (let i = 0; i < el.textTracks.length; i++) {{
                            const t = el.textTracks[i];
                            tracks.push({{
                                index: i,
                                kind: t.kind || null,
                                label: t.label || null,
                                language: t.language || null,
                                mode: t.mode || null,
                                cueCount: (t.cues && t.cues.length) || 0,
                                inBandMetadataTrackDispatchType: t.inBandMetadataTrackDispatchType || null
                            }});
                        }}
                        return {{ found: true, count: el.textTracks.length, tracks: tracks }};
                    }}", request.VideoElementSelector).ConfigureAwait(false);

                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(debugDirectory, "texttracks-state.json"), json, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            Task<RecordedResponse?>[] snapshot;
            lock (lockObj)
            {
                snapshot = pending.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                // ignore; per-response errors are dropped silently
            }

            var rows = snapshot
                .Select(t => t.IsCompletedSuccessfully ? t.Result : null)
                .OfType<RecordedResponse>()
                .OrderBy(r => r.Seq)
                .ToArray();

            var logPath = Path.Combine(debugDirectory, "network.log");
            var indexPath = Path.Combine(debugDirectory, "metadata-responses", "index.json");

            await using (var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            await using (var writer = new StreamWriter(stream))
            {
                foreach (var row in rows)
                {
                    var line = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        seq = row.Seq,
                        timestampUtc = row.TimestampUtc.ToString("O"),
                        method = row.Method,
                        status = row.Status,
                        url = row.Url,
                        contentType = row.ContentType,
                        contentLength = row.ContentLength,
                        bodyBytes = row.BodyBytes,
                        bodyPath = row.BodyRelativePath,
                        bodySha256 = row.BodySha256,
                        headers = row.Headers
                    });
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }

            var indexRows = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.BodyRelativePath))
                .Select(r => new
                {
                    seq = r.Seq,
                    url = r.Url,
                    contentType = r.ContentType,
                    sha256 = r.BodySha256,
                    bytes = r.BodyBytes,
                    path = r.BodyRelativePath
                })
                .ToArray();
            var indexJson = System.Text.Json.JsonSerializer.Serialize(indexRows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(indexPath, indexJson, cancellationToken).ConfigureAwait(false);
        }

        private static bool ShouldPersistBody(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            var ct = contentType.ToLowerInvariant();
            // JSON family
            if (ct.Contains("json", StringComparison.Ordinal)) return true;
            // XML family (DASH .mpd, TTML, SAMI, etc.)
            if (ct.Contains("xml", StringComparison.Ordinal)) return true;
            // Plain text (VTT, SRT, M3U8, JS / HTML source — useful for player config)
            if (ct.StartsWith("text/", StringComparison.Ordinal)) return true;
            // JavaScript files often contain player config inline
            if (ct.Contains("javascript", StringComparison.Ordinal)) return true;
            return false;
        }

        private static string InferExtension(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return ".bin";
            var ct = contentType.ToLowerInvariant();
            if (ct.Contains("json", StringComparison.Ordinal)) return ".json";
            if (ct.Contains("dash+xml", StringComparison.Ordinal)) return ".mpd";
            if (ct.Contains("ttml", StringComparison.Ordinal)) return ".ttml";
            if (ct.Contains("xml", StringComparison.Ordinal)) return ".xml";
            if (ct.Contains("vtt", StringComparison.Ordinal)) return ".vtt";
            if (ct.Contains("srt", StringComparison.Ordinal)) return ".srt";
            if (ct.Contains("mpegurl", StringComparison.Ordinal)) return ".m3u8";
            if (ct.Contains("javascript", StringComparison.Ordinal)) return ".js";
            if (ct.Contains("html", StringComparison.Ordinal)) return ".html";
            if (ct.StartsWith("text/", StringComparison.Ordinal)) return ".txt";
            return ".bin";
        }

        private sealed record RecordedResponse(
            int Seq,
            DateTimeOffset TimestampUtc,
            string Url,
            string Method,
            int Status,
            string? ContentType,
            long ContentLength,
            Dictionary<string, string>? Headers,
            string? BodyRelativePath,
            long BodyBytes,
            string? BodySha256);
    }
}
