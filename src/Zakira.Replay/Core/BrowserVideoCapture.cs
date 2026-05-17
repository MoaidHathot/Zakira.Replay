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
    string? EdgeProfileDirectory = null);

public sealed record BrowserCaptureResult(
    IReadOnlyList<FrameArtifact> Frames,
    double? DurationSeconds,
    IReadOnlyList<ReplayWarning> Warnings,
    IReadOnlyList<BrowserCapturedCaption> Captions);

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

                try
                {
                    context = await playwright.Chromium.LaunchPersistentContextAsync(
                        request.EdgeUserDataDir,
                        new BrowserTypeLaunchPersistentContextOptions
                        {
                            ExecutablePath = edge,
                            Headless = true,
                            Args = ["--disable-gpu", $"--profile-directory={profileSubdir}"],
                            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
                        }).ConfigureAwait(false);
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
                    Args = ["--disable-gpu"]
                }).ConfigureAwait(false);

                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    StorageStatePath = string.IsNullOrWhiteSpace(request.AuthStorageStatePath) ? null : request.AuthStorageStatePath
                }).ConfigureAwait(false);
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

            progress?.Report($"Navigating to {request.Url} (browser capture)...");
            await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 120_000
            }).ConfigureAwait(false);

            await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);

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
                return new BrowserCaptureResult([], null, warnings, authFailCaptions);
            }

            await PlayVideoAsync(page, request, warnings, progress).ConfigureAwait(false);

            var duration = await PollDurationAsync(page, request, warnings, progress, cancellationToken).ConfigureAwait(false);
            if (duration is null)
            {
                IReadOnlyList<BrowserCapturedCaption> earlyCaptions = [];
                if (captionCollector is not null)
                {
                    page.Response -= captionCollector.OnResponse;
                    earlyCaptions = await captionCollector.PersistAsync(cancellationToken).ConfigureAwait(false);
                }
                return new BrowserCaptureResult([], null, warnings, earlyCaptions);
            }

            var frames = await CaptureFramesAsync(page, request, duration.Value, warnings, progress, cancellationToken).ConfigureAwait(false);

            // Give the page a beat for any late-arriving caption fetches (some players load
            // alternate-language tracks after the user clicks around).
            if (captionCollector is not null)
            {
                await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);
                page.Response -= captionCollector.OnResponse;
            }

            IReadOnlyList<BrowserCapturedCaption> captions = captionCollector is null
                ? []
                : await captionCollector.PersistAsync(cancellationToken).ConfigureAwait(false);
            return new BrowserCaptureResult(frames, duration, warnings, captions);
        }
        catch (PlaywrightException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CaptureBrowserUnavailable,
                $"Browser capture failed: {ex.Message}",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Error));
            return new BrowserCaptureResult([], null, warnings, []);
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

    private static async Task PlayVideoAsync(IPage page, BrowserCaptureRequest request, List<ReplayWarning> warnings, IProgress<string>? progress)
    {
        progress?.Report("Starting video playback...");

        if (!string.IsNullOrWhiteSpace(request.PlayButtonSelector))
        {
            try
            {
                await page.Locator(request.PlayButtonSelector).First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 }).ConfigureAwait(false);
                return;
            }
            catch (PlaywrightException ex)
            {
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.CapturePlayButtonNotFound,
                    $"Configured play-button selector '{request.PlayButtonSelector}' did not match: {ex.Message}. Falling back to video.play().",
                    Source: "playwright",
                    Severity: ReplayWarningSeverities.Info));
            }
        }

        // Fallback 1: call the HTML5 video element's play() directly.
        try
        {
            var played = await page.EvaluateAsync<bool>($@"
                async (selector) => {{
                    const el = document.querySelector(selector);
                    if (!el) return false;
                    try {{ await el.play(); return true; }} catch {{ return false; }}
                }}", request.VideoElementSelector).ConfigureAwait(false);
            if (played)
            {
                return;
            }
        }
        catch (PlaywrightException)
        {
            // ignored — fall through to the aria-label heuristic
        }

        // Fallback 2: click the first visible play-labelled button.
        try
        {
            await page.Locator("button[aria-label*='play' i]").First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            warnings.Add(new ReplayWarning(
                ReplayWarningCodes.CapturePlayButtonNotFound,
                $"Could not start playback: {ex.Message}. Capture may fail at the duration probe.",
                Source: "playwright",
                Severity: ReplayWarningSeverities.Warning));
        }
    }

    private static async Task<double?> PollDurationAsync(IPage page, BrowserCaptureRequest request, List<ReplayWarning> warnings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Probing video duration...");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(request.DurationProbeTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var duration = await page.EvaluateAsync<double?>($@"
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
        var locator = page.Locator(request.VideoElementSelector).First;
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
                await page.EvaluateAsync(
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
}
