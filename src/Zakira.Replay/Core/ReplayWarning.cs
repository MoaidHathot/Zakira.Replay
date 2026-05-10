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

    public const string FramesNoMedia = "FRAMES_NO_MEDIA";

    public const string FramesRemoteFallback = "FRAMES_REMOTE_FALLBACK";

    public const string FramesDownloadFailed = "FRAMES_DOWNLOAD_FAILED";

    public const string FramesSceneCapReached = "FRAMES_SCENE_CAP_REACHED";

    public const string FramesLikelyUndersampled = "FRAMES_LIKELY_UNDERSAMPLED";

    public const string OcrNoLlmProvider = "OCR_NO_LLM_PROVIDER";

    public const string OcrParseFallback = "OCR_PARSE_FALLBACK";

    public const string VisionNoLlmProvider = "VISION_NO_LLM_PROVIDER";

    public const string VisionParseFallback = "VISION_PARSE_FALLBACK";

    public const string PerceptualHashFailed = "PERCEPTUAL_HASH_FAILED";

    public const string ClipMediaUrlUnresolved = "CLIP_MEDIA_URL_UNRESOLVED";
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
