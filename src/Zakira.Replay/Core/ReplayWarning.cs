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
