namespace Zakira.Replay.Core;

/// <summary>
/// Structured warning emitted by Zakira.Replay during fact extraction.
/// Warnings are facts about pipeline reliability, not synthesis: an external orchestrator can branch on <see cref="Code"/>.
/// </summary>
public sealed record ReplayWarning(
    string Code,
    string Message,
    string? Source = null,
    string Severity = ReplayWarningSeverities.Warning);

/// <summary>
/// Stable warning code constants. Codes are part of the public artifact contract; rename only with a schema bump.
/// </summary>
public static class ReplayWarningCodes
{
    public const string TranscriptNotFound = "TRANSCRIPT_NOT_FOUND";

    public const string TranscriptNotFoundNoStt = "TRANSCRIPT_NOT_FOUND_NO_STT";

    public const string MediaUrlUnresolved = "MEDIA_URL_UNRESOLVED";

    public const string AudioRemoteFallback = "AUDIO_REMOTE_FALLBACK";

    public const string AudioDownloadFailed = "AUDIO_DOWNLOAD_FAILED";

    public const string SttNoAudio = "STT_NO_AUDIO";

    public const string SttNoLlmProvider = "STT_NO_LLM_PROVIDER";

    public const string SttChunkFailed = "STT_CHUNK_FAILED";

    public const string SttLocalModelMissing = "STT_LOCAL_MODEL_MISSING";

    public const string SttLocalInitFailed = "STT_LOCAL_INIT_FAILED";

    public const string SttLocalInferenceFailed = "STT_LOCAL_INFERENCE_FAILED";

    public const string FramesNoMedia = "FRAMES_NO_MEDIA";

    public const string FramesRemoteFallback = "FRAMES_REMOTE_FALLBACK";

    public const string FramesDownloadFailed = "FRAMES_DOWNLOAD_FAILED";

    /// <summary>
    /// The pipeline reached a point where it would have downloaded the source media to disk
    /// (yt-dlp <c>DownloadMediaForProcessing</c> or the browser STT media-collector
    /// <c>TryDownloadBestCandidate</c>) but <c>AllowMediaDownload</c> was not set on the
    /// request. The path that asked is named in the message so an agent can decide whether to
    /// retry with <c>--allow-media-download</c>.
    /// </summary>
    public const string MediaDownloadDeclined = "MEDIA_DOWNLOAD_DECLINED";

    public const string FramesSceneCapReached = "FRAMES_SCENE_CAP_REACHED";

    public const string FramesLikelyUndersampled = "FRAMES_LIKELY_UNDERSAMPLED";

    public const string OcrNoLlmProvider = "OCR_NO_LLM_PROVIDER";

    public const string OcrParseFallback = "OCR_PARSE_FALLBACK";

    public const string OcrLocalModelsMissing = "OCR_LOCAL_MODELS_MISSING";

    public const string OcrLocalInitFailed = "OCR_LOCAL_INIT_FAILED";

    public const string OcrLocalInferenceFailed = "OCR_LOCAL_INFERENCE_FAILED";

    public const string OcrUnknownProvider = "OCR_UNKNOWN_PROVIDER";

    public const string VisionNoLlmProvider = "VISION_NO_LLM_PROVIDER";

    public const string VisionParseFallback = "VISION_PARSE_FALLBACK";

    public const string VisionLocalModelsMissing = "VISION_LOCAL_MODELS_MISSING";

    public const string VisionLocalInitFailed = "VISION_LOCAL_INIT_FAILED";

    public const string VisionLocalInferenceFailed = "VISION_LOCAL_INFERENCE_FAILED";

    public const string VisionUnknownProvider = "VISION_UNKNOWN_PROVIDER";

    public const string VisionLocalOcrRequired = "VISION_LOCAL_OCR_REQUIRED";

    public const string VisionLocalModeDegraded = "VISION_LOCAL_MODE_DEGRADED";

    public const string PerceptualHashFailed = "PERCEPTUAL_HASH_FAILED";

    public const string CropImageDecodeFailed = "CROP_IMAGE_DECODE_FAILED";

    public const string CropBailOut = "CROP_BAIL_OUT";

    public const string CropProfileUnknown = "CROP_PROFILE_UNKNOWN";

    public const string CropOutputFailed = "CROP_OUTPUT_FAILED";

    public const string CaptureBrowserUnavailable = "CAPTURE_BROWSER_UNAVAILABLE";

    public const string CaptureBrowserFallback = "CAPTURE_BROWSER_FALLBACK";

    public const string CapturePlayButtonNotFound = "CAPTURE_PLAY_BUTTON_NOT_FOUND";

    public const string CaptureDurationUnresolved = "CAPTURE_DURATION_UNRESOLVED";

    public const string CaptureSeekFailed = "CAPTURE_SEEK_FAILED";

    public const string CaptureScreenshotFailed = "CAPTURE_SCREENSHOT_FAILED";

    public const string CaptureUnknownMode = "CAPTURE_UNKNOWN_MODE";

    public const string CaptionsBrowserNetworkNone = "CAPTIONS_BROWSER_NETWORK_NONE";

    public const string CaptionsBrowserNetworkDownloadFailed = "CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED";

    public const string CaptionsBrowserNetworkParseFailed = "CAPTIONS_BROWSER_NETWORK_PARSE_FAILED";

    /// <summary>
    /// Browser capture programmatically enabled one or more caption tracks on the
    /// <c>&lt;video&gt;</c> element (set <c>track.mode = "showing"</c>) so the player would
    /// fetch caption cue sources. Players like SharePoint Stream advertise tracks in
    /// metadata but only fetch their bodies when CC is toggled; activation lets the existing
    /// network interceptor catch them. Severity is <c>info</c>; reports how many tracks
    /// were activated.
    /// </summary>
    public const string CaptureBrowserCaptionsActivated = "CAPTURE_BROWSER_CAPTIONS_ACTIVATED";

    /// <summary>
    /// Browser capture read caption cues directly out of the <c>&lt;video&gt;</c> element's
    /// <c>textTracks</c> API (in-browser JavaScript) and serialised them to VTT files in the
    /// run's <c>captions/</c> directory. Used when the player constructs cues programmatically
    /// (<c>track.addCue()</c>) rather than fetching a <c>.vtt</c> over the wire \u2014 the network
    /// interceptor sees nothing in that case, but the cues are still in JS memory after
    /// activation. Severity is <c>info</c>; reports the number of tracks and total cues
    /// harvested.
    /// </summary>
    public const string CaptureBrowserCaptionsHarvestedFromDom = "CAPTURE_BROWSER_CAPTIONS_HARVESTED_FROM_DOM";

    /// <summary>
    /// Browser capture observed the SharePoint Stream / OneDrive transcripts metadata
    /// endpoint (<c>_api/v2.X/drives/{drive-id}/items/{item-id}?...media/transcripts</c>) and
    /// parsed at least one transcript entry out of the response body. Severity is <c>info</c>;
    /// the orchestrator can use this to confirm Stream-specific extraction kicked in.
    /// </summary>
    public const string CaptureStreamTranscriptDiscovered = "CAPTURE_STREAM_TRANSCRIPT_DISCOVERED";

    /// <summary>
    /// SharePoint Stream transcript fetch via the authenticated Playwright context succeeded
    /// and the resulting file landed under <c>captions/</c>. Severity is <c>info</c>; reports
    /// the language, byte count, and output path.
    /// </summary>
    public const string CaptureStreamTranscriptDownloaded = "CAPTURE_STREAM_TRANSCRIPT_DOWNLOADED";

    /// <summary>
    /// SharePoint Stream transcript metadata response could not be parsed as JSON, or the
    /// JSON did not contain a recognisable <c>media.transcripts[]</c> array. Severity is
    /// <c>warning</c>; the raw body is still persisted in <c>debug/metadata-responses/</c>
    /// when <c>--capture-debug</c> is enabled for inspection.
    /// </summary>
    public const string CaptureStreamMetadataParseFailed = "CAPTURE_STREAM_METADATA_PARSE_FAILED";

    /// <summary>
    /// Successfully downloaded a SharePoint Stream transcript file but could not convert its
    /// body into standard WebVTT \u2014 the format wasn't recognised (neither VTT nor any of the
    /// known Teams JSON shapes). Raw body is still persisted under <c>captions/</c> for the
    /// orchestrator to inspect. Severity is <c>warning</c>.
    /// </summary>
    public const string CaptureStreamTranscriptParseFailed = "CAPTURE_STREAM_TRANSCRIPT_PARSE_FAILED";

    /// <summary>
    /// Browser capture parsed a Microsoft Medius embed page and found the inline
    /// <c>captionsConfiguration.languageList</c> block listing one or more SAS-signed caption
    /// (<c>Caption_&lt;lang&gt;.vtt</c>) URLs. Severity is <c>info</c>; reports how many
    /// languages were advertised. Unlike playback-driven interception, this works even when the
    /// MSE/Shaka player never boots, because the URLs are embedded in the initial HTML document.
    /// </summary>
    public const string CaptureMediusTranscriptDiscovered = "CAPTURE_MEDIUS_TRANSCRIPT_DISCOVERED";

    /// <summary>
    /// A Medius caption file referenced by the embed page's <c>captionsConfiguration</c> was
    /// downloaded via its self-authorising SAS URL and landed under <c>captions/</c>. Severity
    /// is <c>info</c>; reports the language, byte count, and output path.
    /// </summary>
    public const string CaptureMediusTranscriptDownloaded = "CAPTURE_MEDIUS_TRANSCRIPT_DOWNLOADED";

    /// <summary>
    /// A Medius caption URL from <c>captionsConfiguration</c> failed to download (network error
    /// or non-200 response), or its body wasn't recognisable WebVTT. Severity is <c>warning</c>;
    /// other advertised languages are still attempted.
    /// </summary>
    public const string CaptureMediusTranscriptFailed = "CAPTURE_MEDIUS_TRANSCRIPT_FAILED";

    /// <summary>
    /// Browser capture recognised a Microsoft <c>mediastream.microsoft.com</c> Shaka-player
    /// embed (used by Microsoft Build "InstaVOD" sessions whose <c>onDemandUrl</c> looks like
    /// <c>.../player.html?path=/events/.../Config-&lt;CODE&gt;IVOD.json</c>) and parsed the
    /// <c>coreConfig.manifests.main[].manifest</c> + <c>cdns[origin][].hostName</c> blocks to
    /// derive the HLS master playlist URL. Severity is <c>info</c>; reports the discovered
    /// playlist URL. The actual subtitle download happens in a follow-up step.
    /// </summary>
    public const string CaptureMediastreamTranscriptDiscovered = "CAPTURE_MEDIASTREAM_TRANSCRIPT_DISCOVERED";

    /// <summary>
    /// A <c>mediastream.microsoft.com</c> session's subtitle track was successfully extracted
    /// from its HLS subtitle playlist: every <c>Segment(NNN).vtt</c> was fetched in parallel,
    /// the per-segment rolling-cue progression was deduped (CEA-608/708-style word-by-word
    /// caption growth collapses to one stable cue per phrase), and the result landed under
    /// <c>captions/mediastream-NNNN-&lt;lang&gt;.vtt</c>. Severity is <c>info</c>; reports the
    /// segment count, dedup ratio, language, output path, and elapsed seconds.
    /// </summary>
    public const string CaptureMediastreamTranscriptDownloaded = "CAPTURE_MEDIASTREAM_TRANSCRIPT_DOWNLOADED";

    /// <summary>
    /// A step in the <c>mediastream.microsoft.com</c> caption extraction pipeline failed:
    /// the player config JSON couldn't be fetched / parsed, the HLS master playlist had no
    /// <c>#EXT-X-MEDIA:TYPE=SUBTITLES</c> entry, the subtitle playlist returned no segments,
    /// every segment fetch failed, or the merged VTT was empty. Severity is <c>warning</c>;
    /// other capture paths (player iframe, frame extraction from the HLS URL) still proceed.
    /// </summary>
    public const string CaptureMediastreamTranscriptFailed = "CAPTURE_MEDIASTREAM_TRANSCRIPT_FAILED";

    public const string AuthProfileNotFound = "AUTH_PROFILE_NOT_FOUND";

    public const string AuthProfileStale = "AUTH_PROFILE_STALE";

    public const string AuthProfileLoadFailed = "AUTH_PROFILE_LOAD_FAILED";

    /// <summary>
    /// The dedicated Edge user-data-dir resolved from <c>capture.browser.edgeUserDataDir</c>
    /// (or its <c>%LOCALAPPDATA%\Zakira.Replay\edge-profile</c> default) has no Cookies file
    /// inside the named profile sub-folder, so persistent-context capture cannot reuse a
    /// prior sign-in. Severity is <c>info</c>: capture falls back to the StorageState path
    /// (anonymous browser context unless <c>--auth-profile</c> is also configured).
    /// Remediation: <c>zakira-replay auth init-edge-profile</c>.
    /// </summary>
    public const string CaptureBrowserProfileNotInitialized = "CAPTURE_BROWSER_PROFILE_NOT_INITIALIZED";

    /// <summary>
    /// The configured <c>capture.browser.edgeUserDataDir</c> resolves to a path whose parent
    /// directory does not exist. Severity is <c>error</c>; capture aborts. Fix the config
    /// or create the directory.
    /// </summary>
    public const string CaptureBrowserProfileDirMissing = "CAPTURE_BROWSER_PROFILE_DIR_MISSING";

    /// <summary>
    /// A <c>SingletonLock</c> file inside the configured profile sub-folder indicates Edge
    /// (or another Chromium-based process) is already using the user-data-dir. Severity is
    /// <c>error</c>; capture aborts. Close the running Edge instance and retry.
    /// </summary>
    public const string CaptureBrowserProfileLocked = "CAPTURE_BROWSER_PROFILE_LOCKED";

    /// <summary>
    /// <see cref="Microsoft.Playwright.IBrowserType.LaunchPersistentContextAsync"/> threw
    /// during persistent-context capture (corrupt profile, DPAPI failure, incompatible Edge
    /// version, etc.). Severity is <c>error</c>; capture aborts. The Playwright exception
    /// message is included.
    /// </summary>
    public const string CaptureBrowserProfileLaunchFailed = "CAPTURE_BROWSER_PROFILE_LAUNCH_FAILED";

    /// <summary>
    /// Post-navigation URL inspection matched a known Microsoft / SAML / OAuth sign-in
    /// domain, indicating the page redirected to a login page rather than serving the
    /// requested content. Severity is <c>error</c>; capture aborts before duration probing
    /// to avoid misleading <c>CAPTURE_DURATION_UNRESOLVED</c> downstream. Remediation:
    /// re-run <c>zakira-replay auth init-edge-profile</c> (persistent-context mode) or
    /// <c>zakira-replay auth login &lt;profile&gt;</c> (StorageState mode) and retry.
    /// </summary>
    public const string CaptureBrowserAuthRequired = "CAPTURE_BROWSER_AUTH_REQUIRED";

    /// <summary>
    /// Post-navigation the page contains a Microsoft MFA challenge selector (e.g. an OTP
    /// input or the "Approve sign-in request" prompt), which headless Playwright cannot
    /// satisfy. Severity is <c>error</c>; capture aborts. Re-init the profile interactively
    /// to clear the MFA challenge.
    /// </summary>
    public const string CaptureBrowserAuthMfaDetected = "CAPTURE_BROWSER_AUTH_MFA_DETECTED";

    /// <summary>
    /// Both <c>--auth-profile</c> and an initialized <c>capture.browser.edgeUserDataDir</c>
    /// are configured. Persistent-context wins; the StorageState profile is ignored.
    /// Severity is <c>info</c> for auditability.
    /// </summary>
    public const string CaptureProfileConflict = "CAPTURE_PROFILE_CONFLICT";

    /// <summary>
    /// Browser capture saw a candidate media response during playback and successfully
    /// downloaded it via the authenticated browser context, falling back to local STT after
    /// no inline captions were observed. Severity is <c>info</c>; recorded so orchestrators
    /// can see the audio-fallback path was used.
    /// </summary>
    public const string CaptureBrowserMediaDownloaded = "CAPTURE_BROWSER_MEDIA_DOWNLOADED";

    /// <summary>
    /// Browser capture saw no candidate media responses suitable for re-download during
    /// playback. Typical cause: the player uses HLS / DASH chunked streaming and serves the
    /// audio as many small fragments rather than a single addressable file. Severity is
    /// <c>info</c>; STT will not run for this source unless an alternate audio path is wired.
    /// </summary>
    public const string CaptureBrowserMediaNoCandidate = "CAPTURE_BROWSER_MEDIA_NO_CANDIDATE";

    /// <summary>
    /// Browser capture identified a media URL but the authenticated re-download attempt
    /// failed (HTTP error, oversize body, transient network failure). Severity is
    /// <c>warning</c>; STT will not run.
    /// </summary>
    public const string CaptureBrowserMediaDownloadFailed = "CAPTURE_BROWSER_MEDIA_DOWNLOAD_FAILED";

    public const string ClipMediaUrlUnresolved = "CLIP_MEDIA_URL_UNRESOLVED";

    public const string FrameCaptureMediaUrlUnresolved = "FRAME_CAPTURE_MEDIA_URL_UNRESOLVED";

    public const string FrameCaptureTimestampOutOfRange = "FRAME_CAPTURE_TIMESTAMP_OUT_OF_RANGE";

    public const string FrameCaptureRangeOutOfBounds = "FRAME_CAPTURE_RANGE_OUT_OF_BOUNDS";

    public const string FrameCaptureTooManyTimestamps = "FRAME_CAPTURE_TOO_MANY_TIMESTAMPS";

    public const string FrameCaptureNoFrames = "FRAME_CAPTURE_NO_FRAMES";

    public const string FrameCaptureSceneCapReached = "FRAME_CAPTURE_SCENE_CAP_REACHED";

    public const string DiarizationNoAudio = "DIARIZATION_NO_AUDIO";

    public const string DiarizationNoTranscript = "DIARIZATION_NO_TRANSCRIPT";

    public const string DiarizationModelsMissing = "DIARIZATION_MODELS_MISSING";

    public const string DiarizationInitFailed = "DIARIZATION_INIT_FAILED";

    public const string DiarizationFailed = "DIARIZATION_FAILED";

    public const string DiarizationUnknownProvider = "DIARIZATION_UNKNOWN_PROVIDER";
}

/// <summary>
/// Severity classifications for <see cref="ReplayWarning.Severity"/>.
/// </summary>
public static class ReplayWarningSeverities
{
    public const string Info = "info";

    public const string Warning = "warning";

    public const string Error = "error";
}
