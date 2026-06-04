using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class ReplayConfig
{
    public DependencyPathConfig Dependencies { get; set; } = new();

    public SearchConfig Search { get; set; } = new();

    public LlmConfig Llm { get; set; } = new();

    public OcrConfig Ocr { get; set; } = new();

    public VisionConfig Vision { get; set; } = new();

    public CaptionsConfig Captions { get; set; } = new();

    public SlidesConfig Slides { get; set; } = new();

    public FramesConfig Frames { get; set; } = new();

    public CropConfig Crop { get; set; } = new();

    public CaptureConfig Capture { get; set; } = new();

    public AuthConfig Auth { get; set; } = new();

    public DiarizationConfig Diarization { get; set; } = new();

    /// <summary>
    /// Output-folder controls for <c>runs/&lt;run-id&gt;/</c> artifacts. Empty by default, in
    /// which case the pipeline falls back to <c>$ZAKIRA_REPLAY_RUNS_DIRECTORY</c> or
    /// <c>&lt;cwd&gt;/runs</c>.
    /// </summary>
    public RunsConfig Runs { get; set; } = new();
}

/// <summary>
/// Runs-output configuration. Lets you pin the root folder where every
/// <c>runs/&lt;run-id&gt;/</c> artifact tree lands instead of inheriting the current working
/// directory. Resolution precedence (highest wins):
/// <list type="number">
///   <item><description>Environment variable <c>ZAKIRA_REPLAY_RUNS_DIRECTORY</c></description></item>
///   <item><description><c>runs.directory</c> in the user config file</description></item>
///   <item><description><c>&lt;cwd&gt;/runs</c> (legacy default)</description></item>
/// </list>
/// Same env-var-literal preservation as <c>dependencies.portableDirectory</c>:
/// <c>%LOCALAPPDATA%\Zakira.Replay\runs</c> is stored verbatim and expanded at read time.
/// </summary>
public sealed class RunsConfig
{
    public string? Directory { get; set; }
}

public sealed class DiarizationConfig
{
    /// <summary>
    /// Preferred provider when the pipeline runs with <c>--diarize</c>. Currently only
    /// <see cref="DiarizationProviders.SherpaOnnx"/> is wired; reserved for future plug-ins
    /// (pyannoteAI cloud, NeMo, etc.).
    /// </summary>
    public string? Provider { get; set; } = DiarizationProviders.SherpaOnnx;

    /// <summary>
    /// Directory containing the two ONNX model files. Resolved against the portable directory
    /// when null. Auto-populated by <c>zakira-replay deps install diarization</c>.
    /// </summary>
    public string? ModelDirectory { get; set; }

    public string? SegmentationModelPath { get; set; }

    public string? EmbeddingModelPath { get; set; }

    /// <summary>
    /// Hard cluster count when the orchestrator knows how many speakers are present. When null,
    /// <see cref="Threshold"/> controls the clustering cutoff and the model auto-detects.
    /// </summary>
    public int? NumSpeakers { get; set; }

    /// <summary>
    /// Agglomerative clustering threshold (cosine distance) used when <see cref="NumSpeakers"/>
    /// is null. Lower values mean stricter clustering (more speakers). Default 0.5 matches the
    /// sherpa-onnx examples.
    /// </summary>
    public float? Threshold { get; set; }

    /// <summary>
    /// Minimum speech segment duration (seconds) emitted by pyannote-segmentation. Sub-threshold
    /// activity is treated as part of an adjacent segment. Defaults to 0.3 s.
    /// </summary>
    public float? MinDurationOnSeconds { get; set; }

    /// <summary>
    /// Minimum silence duration (seconds) between speech segments. Sub-threshold gaps are
    /// merged into a single contiguous segment. Defaults to 0.5 s.
    /// </summary>
    public float? MinDurationOffSeconds { get; set; }

    /// <summary>
    /// Native thread count for sherpa-onnx inference. Defaults to 1.
    /// </summary>
    public int? Threads { get; set; }

    /// <summary>
    /// When true (default), <see cref="SherpaOnnxDiarizationProvider"/> initialisation may
    /// invoke <see cref="PortableDependencyInstaller.InstallAsync"/> to fetch the segmentation
    /// and embedding models on first use. Mirror of <c>ocr.local.autoDownload</c> and
    /// <c>llm.localWhisper.autoDownload</c>; set false to require explicit
    /// <c>zakira-replay deps install diarization</c>.
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

public sealed class AuthConfig
{
    /// <summary>
    /// Directory storing Playwright storage-state JSON snapshots created by
    /// <c>zakira-replay auth login &lt;profile&gt;</c>. Each profile is a single file named
    /// <c>&lt;slug&gt;.json</c>. When null, the directory resolves to <c>auth/</c> next to the
    /// configuration file. Override with <c>ZAKIRA_REPLAY_AUTH_DIRECTORY</c>.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Auth profiles are wall-clock state — SSO sessions, OAuth refresh tokens, and CDN
    /// cookies all expire. When the resolved profile file is older than this many minutes,
    /// the pipeline emits an <c>AUTH_PROFILE_STALE</c> info-level warning so orchestrators
    /// can decide whether to re-run <c>auth login</c>. Defaults to 60 minutes (matches the
    /// 1-2 hour session-expiry observation from the squad-skills SKILL).
    /// </summary>
    public int StaleThresholdMinutes { get; set; } = 60;
}

public sealed class CaptureConfig
{
    /// <summary>
    /// Frame-capture mode. <c>ytdlp</c> (default) uses yt-dlp to resolve a direct stream URL and
    /// ffmpeg to extract frames — works for ~1000 sites yt-dlp supports. <c>browser</c> drives a
    /// Playwright-controlled Chromium (Edge) instance to click play, seek via JavaScript, and
    /// screenshot the &lt;video&gt; element at the chosen timestamps — works for sites yt-dlp can't
    /// reach (custom enterprise portals, Medius/Teams recordings, sites that need a fully-rendered
    /// page to expose the video). <c>auto</c> tries yt-dlp first and falls back to <c>browser</c>
    /// on failure, emitting <c>CAPTURE_BROWSER_FALLBACK</c>.
    /// </summary>
    public string Mode { get; set; } = CaptureModes.YtDlp;

    public BrowserCaptureConfig Browser { get; set; } = new();
}

public sealed class BrowserCaptureConfig
{
    /// <summary>
    /// Optional CSS / Playwright-locator selector for the play button. When null, the capture
    /// client tries the &lt;video&gt; element itself (HTML5 <c>video.play()</c>) and falls back to
    /// the first visible <c>button[aria-label*="play" i]</c>.
    /// </summary>
    public string? PlayButtonSelector { get; set; }

    /// <summary>
    /// CSS selector for the &lt;video&gt; element used for duration probing, seeking, and
    /// screenshotting. Defaults to <c>video</c>.
    /// </summary>
    public string VideoElementSelector { get; set; } = "video";

    /// <summary>
    /// Seconds to wait after <c>video.currentTime = …</c> before screenshotting, so the browser
    /// has time to decode and paint the new frame. The reference SKILL uses 2.5; 1.0 too fast,
    /// 2.0 mostly works, 2.5 reliable, raise to 3.0-4.0 for high-res videos or slower machines.
    /// </summary>
    public double SeekWaitSeconds { get; set; } = 2.5;

    /// <summary>
    /// Max time to wait for <c>video.duration</c> to be a finite number (it returns NaN/Infinity
    /// until the metadata loads). Default 20 seconds matches the reference SKILL.
    /// </summary>
    public double DurationProbeTimeoutSeconds { get; set; } = 20.0;

    /// <summary>
    /// JPEG quality (1-100) for screenshots written to <c>frames/scene-NNNN.jpg</c>. Defaults to
    /// 90 to keep storage modest while preserving slide text legibility.
    /// </summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// When true (default), the browser-capture client attaches a network listener while the
    /// page is loaded and the video is played, captures every <c>.vtt</c> / <c>.srt</c>
    /// response, saves it to <c>captions/browser-NNNN.vtt</c>, and records an inventory at
    /// <c>captions/discovered.json</c>. When the pipeline's transcript step did not find a
    /// transcript, the best-language match is used to populate <c>transcript.md</c> retroactively.
    /// Set false to skip caption interception entirely (for privacy or to reduce per-run noise).
    /// </summary>
    public bool CaptureCaptions { get; set; } = true;

    /// <summary>
    /// Maximum byte count for any single browser-captured caption file. Files larger than this
    /// are skipped with a <c>CAPTIONS_BROWSER_NETWORK_DOWNLOAD_FAILED</c> warning so a runaway
    /// stream URL (e.g. an HLS playlist mistakenly served with a <c>.vtt</c> path) cannot fill
    /// the disk. Defaults to 5 MB.
    /// </summary>
    public int MaxCaptionBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Optional dedicated Edge user-data-dir for persistent-context capture. When the resolved
    /// directory contains a Cookies file under the named profile sub-folder, the browser-capture
    /// client launches Playwright with <c>LaunchPersistentContextAsync</c> against this dir,
    /// reusing cookies stored by Edge in their DPAPI-encrypted SQLite format (per-user,
    /// per-machine on Windows). This is materially more secure than the plaintext
    /// <c>StorageState</c> JSON produced by <c>auth login</c>: a leaked StorageState file works
    /// on any machine; a leaked Edge profile is unusable on a different user / machine.
    /// </summary>
    /// <remarks>
    /// Stored verbatim (including environment-variable references like <c>%LOCALAPPDATA%</c>)
    /// so the config travels across machines. Expansion happens at read time via
    /// <see cref="ResolveEdgeUserDataDir"/>. When the value is null/empty, the default is
    /// <c>%LOCALAPPDATA%\Zakira.Replay\edge-profile</c> on Windows (and the platform-equivalent
    /// LocalApplicationData folder on other OSes).
    /// </remarks>
    public string? EdgeUserDataDir { get; set; }

    /// <summary>
    /// Name of the profile sub-folder inside <see cref="EdgeUserDataDir"/>. Maps to Chromium's
    /// <c>--profile-directory</c> switch. Defaults to <c>"Default"</c>; only change this if
    /// you've manually created multiple profiles inside the same user-data-dir.
    /// </summary>
    public string? EdgeProfileDirectory { get; set; }

    /// <summary>
    /// When true, the browser-capture client additionally writes a diagnostic dump under
    /// <c>runs/&lt;id&gt;/debug/</c>: <c>network.log</c> (JSONL of every response), JSON / XML
    /// response bodies under <see cref="DebugMaxBodyBytes"/>, a <c>texttracks-state.json</c>
    /// snapshot, and a Playwright HAR file at <c>network.har</c>. Cheap-but-not-free: each
    /// response is hashed and (when eligible) persisted, so very large pages may produce
    /// hundreds of MB of debug output. Default false; toggle via <c>--capture-debug</c>.
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Maximum body size (bytes) for any single response that <see cref="Debug"/> dumps to
    /// disk. Bodies larger than this are recorded in <c>network.log</c> with their headers
    /// but not persisted to <c>debug/metadata-responses/</c>. Defaults to 1 MB.
    /// </summary>
    public long DebugMaxBodyBytes { get; set; } = 1L * 1024 * 1024;

    /// <summary>
    /// Global autoplay-policy default for the headless browser. One of the constants in
    /// <see cref="AutoplayPolicies"/> (currently <c>"default"</c> or
    /// <c>"no-user-gesture-required"</c>; the schema is extensible). When the value is
    /// <c>"default"</c> (the default), Chromium's normal autoplay policy applies.
    /// </summary>
    /// <remarks>
    /// Per-run <c>--autoplay-policy</c> and the per-host map below both override this. The
    /// global default is the right knob to flip on machines that exclusively analyse MSE-heavy
    /// sources (e.g. a dedicated conference-recording box).
    /// </remarks>
    public string AutoplayPolicy { get; set; } = AutoplayPolicies.Default;

    /// <summary>
    /// Per-host overrides for the autoplay policy. Keys are hostnames; values are
    /// <see cref="AutoplayPolicies"/> constants. A leading <c>*.</c> marks a suffix-wildcard
    /// match (so <c>"*.event.microsoft.com"</c> matches <c>mediusprod.event.microsoft.com</c>);
    /// bare hostnames match exactly. Exact matches beat wildcards; among wildcards, the
    /// longest matching suffix wins. Per-host entries override <see cref="AutoplayPolicy"/>
    /// but are overridden by a per-run <c>--autoplay-policy</c> flag.
    /// </summary>
    public Dictionary<string, string>? AutoplayPolicyByHost { get; set; }

    /// <summary>
    /// Resolves <see cref="EdgeUserDataDir"/> to an absolute filesystem path, expanding any
    /// environment-variable references (e.g. <c>%LOCALAPPDATA%</c>) against the current
    /// machine's environment. When the configured value is null or whitespace, returns the
    /// per-machine default <c>{LocalApplicationData}/Zakira.Replay/edge-profile</c>.
    /// </summary>
    /// <remarks>
    /// <c>ZAKIRA_REPLAY_EDGE_USER_DATA_DIR</c> takes priority over the config value when set,
    /// mirroring the override pattern used by <c>ZAKIRA_REPLAY_AUTH_DIRECTORY</c>. Useful for
    /// pinning the path in CI / tests without touching the config file.
    /// </remarks>
    public string ResolveEdgeUserDataDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_USER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(fromEnv.Trim().Trim('"')));
        }

        if (!string.IsNullOrWhiteSpace(EdgeUserDataDir))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(EdgeUserDataDir.Trim().Trim('"')));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // Final fallback for environments where LocalApplicationData is empty (rare on CI).
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
        return Path.Combine(localAppData, "Zakira.Replay", "edge-profile");
    }

    /// <summary>
    /// Resolves <see cref="EdgeProfileDirectory"/> to a non-empty sub-folder name. Defaults to
    /// <c>"Default"</c> when null/whitespace.
    /// </summary>
    public string ResolveEdgeProfileDirectory()
    {
        return string.IsNullOrWhiteSpace(EdgeProfileDirectory) ? "Default" : EdgeProfileDirectory.Trim();
    }

    /// <summary>
    /// Reports whether the resolved Edge profile directory exists and contains a usable
    /// <c>Cookies</c> file inside the configured sub-folder. Used by both the capture client
    /// (to decide whether to take the persistent-context path) and by <c>doctor</c>.
    /// </summary>
    public bool IsEdgeProfileInitialized()
    {
        try
        {
            var profileDir = Path.Combine(ResolveEdgeUserDataDir(), ResolveEdgeProfileDirectory());
            if (!System.IO.Directory.Exists(profileDir))
            {
                return false;
            }

            // Chromium stores cookies at either Default/Cookies (older layout) or
            // Default/Network/Cookies (newer layout). Treat either as initialized.
            var legacyCookies = Path.Combine(profileDir, "Cookies");
            var modernCookies = Path.Combine(profileDir, "Network", "Cookies");
            return (File.Exists(legacyCookies) && new FileInfo(legacyCookies).Length > 0)
                || (File.Exists(modernCookies) && new FileInfo(modernCookies).Length > 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the path to the <c>SingletonLock</c> file Chromium creates inside a profile
    /// sub-folder while a process holds the user-data-dir. Existence of this file at launch
    /// time indicates Edge (or another Chromium process) is already using the profile.
    /// </summary>
    public string GetEdgeProfileSingletonLockPath()
    {
        return Path.Combine(ResolveEdgeUserDataDir(), ResolveEdgeProfileDirectory(), "SingletonLock");
    }
}

public sealed class CropConfig
{
    /// <summary>
    /// When true, run smart-crop preprocessing on every extracted frame before perceptual
    /// hashing, OCR, and vision. Removes meeting-platform UI chrome (Teams/Zoom/WebEx controls
    /// bars, participant galleries, black letterbox bars) so downstream stages see only the
    /// shared slide/screen area. Defaults to false; opt-in per-run with <c>--smart-crop</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Crop profile name. <c>auto</c> uses generic brightness-based heuristics tuned for the
    /// SKILL's <c>smart_crop()</c> reference. Reserved values: <c>teams</c>, <c>zoom</c>,
    /// <c>webex</c>, <c>generic</c>, <c>off</c>. Future releases may add platform-specific
    /// tunings; today <c>auto</c>/<c>generic</c>/<c>teams</c>/<c>zoom</c>/<c>webex</c> share
    /// the same algorithm.
    /// </summary>
    public string Profile { get; set; } = SmartCropProfiles.Auto;
}

public sealed class OcrConfig
{
    /// <summary>
    /// Preferred OCR provider when the pipeline runs with <c>--ocr</c>. Use one of the
    /// <see cref="OcrProviders"/> constants. Default is <see cref="OcrProviders.Local"/>
    /// (offline RapidOCR via ONNX); flip to <see cref="OcrProviders.Copilot"/> in config or
    /// per request to route through an LLM vision model instead.
    /// </summary>
    public string? Provider { get; set; } = OcrProviders.Local;

    public LocalOcrConfig Local { get; set; } = new();
}

public sealed class LocalOcrConfig
{
    /// <summary>
    /// Directory containing the four RapidOCR (PP-OCRv5) model files (det, cls, rec, dict).
    /// Resolved against the portable directory when null.
    /// </summary>
    public string? ModelDirectory { get; set; }

    public string? DetectionModelPath { get; set; }

    public string? ClassificationModelPath { get; set; }

    public string? RecognitionModelPath { get; set; }

    public string? DictionaryPath { get; set; }

    /// <summary>
    /// RapidOCR PP-OCRv5 language pack used by the local OCR provider. The detection and
    /// classification models are shared across packs; only the recognition model + character
    /// dictionary swap per pack. See <see cref="OcrLanguagePacks.All"/> for the supported list
    /// (latin, chinese, english, korean, cyrillic, arabic, devanagari, greek, telugu, tamil).
    /// Defaults to <see cref="OcrLanguagePacks.Latin"/> for backwards compatibility with 0.5.x
    /// and earlier installs. Override via <c>--language</c> on <c>deps install ocr</c>,
    /// <c>ZAKIRA_REPLAY_OCR_LANGUAGE_PACK</c>, or this config key.
    /// </summary>
    public string? LanguagePack { get; set; } = OcrLanguagePacks.Latin;

    /// <summary>
    /// When true (default), <see cref="LocalOnnxOcrProvider"/> initialisation may invoke
    /// <see cref="PortableDependencyInstaller.InstallAsync"/> to fetch the RapidOCR
    /// models on first use. Install ahead-of-time with <c>deps install ocr</c> to skip the
    /// network round-trip; set false to disable on-demand downloads entirely.
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

public sealed class VisionConfig
{
    /// <summary>
    /// Preferred vision provider when the pipeline runs with <c>--vision</c>. Use one of the
    /// <see cref="VisionProviders"/> constants. Default is <see cref="VisionProviders.Copilot"/>
    /// (LLM-backed via the configured provider) for backward compatibility; flip to
    /// <see cref="VisionProviders.Local"/> in config or per request to use the fully-local
    /// classical-CV path that never invokes an LLM.
    /// </summary>
    public string? Provider { get; set; } = VisionProviders.Copilot;

    public LocalVisionConfig Local { get; set; } = new();
}

public sealed class LocalVisionConfig
{
    /// <summary>
    /// Sub-mode for the local (no-LLM) vision provider. One of <c>heuristic</c>, <c>clip</c>,
    /// or <c>clip-caption</c> (default). The deprecated alias <c>clip-blip</c> is still
    /// accepted for back-compat with 0.7.x and earlier.
    /// </summary>
    public string? Mode { get; set; } = "clip-caption";

    /// <summary>
    /// Directory containing the CLIP / Florence-2 ONNX model files. Resolved against the
    /// portable directory's <c>vision/</c> subfolder when null. Auto-populated by
    /// <c>zakira-replay deps install vision</c>.
    /// </summary>
    public string? ModelDirectory { get; set; }

    public string? ClipImageEncoderPath { get; set; }
    public string? ClipTextEncoderPath { get; set; }
    public string? ClipKindEmbeddingsPath { get; set; }

    /// <summary>Florence-2 vision-encoder ONNX path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceVisionEncoderPath { get; set; }
    /// <summary>Florence-2 text-encoder ONNX path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceEncoderPath { get; set; }
    /// <summary>Florence-2 decoder ONNX path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceDecoderPath { get; set; }
    /// <summary>Florence-2 embed-tokens ONNX path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceEmbedTokensPath { get; set; }
    /// <summary>Florence-2 tokenizer vocab.json path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceVocabPath { get; set; }
    /// <summary>Florence-2 tokenizer merges.txt path. Required for <c>clip-caption</c> mode.</summary>
    public string? FlorenceMergesPath { get; set; }
    /// <summary>Florence-2 added_tokens.json path. Optional; only used for &lt;loc_*&gt; tokens during decode.</summary>
    public string? FlorenceAddedTokensPath { get; set; }

    /// <summary>
    /// Quantization variant for the Florence ONNX downloads. One of
    /// <see cref="LocalVisionOptions.SupportedQuantizations"/>. Defaults to <c>quantized</c> (int8).
    /// </summary>
    public string? FlorenceQuantization { get; set; }

    /// <summary>Maximum length (in tokens) for the Florence greedy-decoded caption. Defaults to 80.</summary>
    public int? FlorenceMaxTokens { get; set; }

    /// <summary>
    /// When true (default), the provider may invoke
    /// <see cref="PortableDependencyInstaller.InstallAsync"/> to fetch the CLIP / Florence models
    /// on first use. Set false to disable on-demand downloads entirely (e.g. air-gapped runs).
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

public sealed class FramesConfig
{
    /// <summary>
    /// Upper bound on the number of frames the scene-strategy ffmpeg pipeline will return for a
    /// single run, so a pathological video with thousands of scene changes cannot fill the disk.
    /// Slide grouping deduplicates within this cap. Defaults to 5000.
    /// </summary>
    public int SceneSafetyCap { get; set; } = 5000;

    /// <summary>
    /// Default duration-aware sampling rate for the <c>interval</c> frame strategy. When the
    /// request leaves <c>FramesPerMinute</c> null, the pipeline falls back to this value. Set
    /// to <c>0</c> to disable duration-aware scaling and rely on <c>--frames</c> alone. Defaults
    /// to 12 (one frame every five seconds).
    /// </summary>
    public int PerMinute { get; set; } = 12;
}

public sealed class SlidesConfig
{
    /// <summary>
    /// When false, slide grouping is disabled and every frame becomes its own slide. Useful for
    /// animated UI walkthroughs where every frame matters. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum Hamming distance between adjacent frames' 64-bit perceptual hashes that are still
    /// considered the same slide. Lower is stricter. Default: 6.
    /// </summary>
    public int HashDistance { get; set; } = 6;
}

public sealed class CaptionsConfig
{
    /// <summary>
    /// Subtitle/caption language preferences in order of priority. Use <c>"auto"</c> to let
    /// Zakira.Replay union the languages advertised by the source's metadata with sensible
    /// defaults (English plus YouTube live chat). Specific BCP-47-style codes such as
    /// <c>"en"</c>, <c>"fr"</c>, or <c>"es-419"</c> are passed verbatim to yt-dlp.
    /// </summary>
    public List<string> Languages { get; set; } = ["auto"];
}

public sealed class DependencyPathConfig
{
    public bool AutoDownload { get; set; }

    public string? PortableDirectory { get; set; }

    public string? YtDlpPath { get; set; }

    public string? FfmpegPath { get; set; }

    public string? FfprobePath { get; set; }

    public string? EdgePath { get; set; }
}

public sealed class SearchConfig
{
    public OnnxEmbeddingConfig Onnx { get; set; } = new();
}

public sealed class OnnxEmbeddingConfig
{
    public string? ModelPath { get; set; }

    public string? VocabularyPath { get; set; }

    public int? MaxSequenceLength { get; set; }

    public int? EmbeddingDimensions { get; set; }

    public bool AutoDownload { get; set; }

    public string? ModelDirectory { get; set; }

    public string? ModelFile { get; set; }

    /// <summary>
    /// Identifier of the search-embedding model the <c>sqlite-onnx</c> backend uses. One of
    /// the registry entries in <see cref="KnownSearchEmbeddingModels"/>
    /// (<c>bge-small-en-v1.5</c> by default in 0.10.0,
    /// also <c>snowflake-arctic-embed-s</c> and <c>multilingual-e5-small</c>),
    /// or a free-form string for a user-supplied model when paired with explicit
    /// <see cref="ModelPath"/> + <see cref="VocabularyPath"/>.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Embedding scheme override: one of <c>bert</c>, <c>bge</c>, <c>e5</c>. When null the
    /// kind is auto-derived from <see cref="Model"/> (or from the model directory name when
    /// only paths are configured) via
    /// <see cref="OnnxSearchEmbeddingProvider.ResolveKind(string?, string?)"/>.
    /// </summary>
    public string? ModelKind { get; set; }

    /// <summary>
    /// Tokenizer-file path override. Defaults to the model directory's <c>vocab.txt</c>
    /// (BERT WordPiece for bge/arctic/bert) or <c>sentencepiece.bpe.model</c> (XLM-R
    /// SentencePiece for multilingual-e5). Set this when pointing at a non-standard layout.
    /// </summary>
    public string? TokenizerPath { get; set; }
}

public sealed class LlmConfig
{
    public string? Provider { get; set; } = LlmProviders.GitHubCopilot;

    public OpenAiConfig OpenAi { get; set; } = new();

    public AzureOpenAiConfig AzureOpenAi { get; set; } = new();

    /// <summary>
    /// Options for the local Ollama daemon selected via <c>--llm-provider ollama</c>. Chat and
    /// vision routed through Ollama's native <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// implementation; STT remains the caller's responsibility (use <c>local-whisper</c>).
    /// </summary>
    public OllamaConfig Ollama { get; set; } = new();

    /// <summary>
    /// Options for the fully-local Whisper.net STT path selected via
    /// <c>--llm-provider local-whisper</c>. Resolved end-to-end by
    /// <see cref="LocalWhisperOptions.Resolve(ReplayConfig?)"/>.
    /// </summary>
    public LocalWhisperConfig LocalWhisper { get; set; } = new();
}

public sealed class OllamaConfig
{
    public List<string> EndpointEnvironmentVariables { get; set; } = [];

    public List<string> ModelEnvironmentVariables { get; set; } = [];

    public List<string> VisionModelEnvironmentVariables { get; set; } = [];

    /// <summary>HTTP endpoint of the local Ollama daemon. Defaults to <c>http://localhost:11434</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Chat model name (matches the value passed to <c>ollama pull</c>, e.g. <c>qwen2.5:7b</c>,
    /// <c>llama3.1:8b</c>). Required when the provider is selected.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Vision-capable model used when the request includes image attachments (e.g. OCR / vision
    /// frames). Examples: <c>llava</c>, <c>llama3.2-vision</c>, <c>bakllava</c>. When null,
    /// image requests fall back to <see cref="Model"/>.
    /// </summary>
    public string? VisionModel { get; set; }

    /// <summary>
    /// Per-request timeout for chat completions. Defaults to 5 minutes — generous because local
    /// inference can be slow on CPU-only machines and meeting OCR/vision tasks frequently exceed
    /// the cloud-default 60-second budget.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}

public sealed class LocalWhisperConfig
{
    /// <summary>
    /// Absolute path to a ggml model file (<c>ggml-&lt;size&gt;.bin</c>). When null, the provider
    /// derives the path from <see cref="ModelSize"/> against the portable model directory.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Whisper model size used when <see cref="ModelPath"/> is null. One of
    /// <c>tiny</c>/<c>base</c>/<c>small</c>/<c>medium</c>/<c>large-v3</c>/<c>large-v3-turbo</c> or
    /// their <c>.en</c> variants. Defaults to <c>small</c>.
    /// </summary>
    public string? ModelSize { get; set; }

    /// <summary>
    /// Language hint passed to Whisper. Use <c>auto</c> to enable language detection (default),
    /// or a two-letter code like <c>en</c>, <c>fr</c>, <c>es</c>.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Number of native threads whisper.cpp may use. <c>null</c> or <c>0</c> lets whisper.cpp
    /// pick a reasonable default for the current machine.
    /// </summary>
    public int? Threads { get; set; }

    /// <summary>
    /// Reserved for ordering Whisper.net's runtime probes (CUDA/Vulkan/CoreML/CPU). Currently
    /// stored verbatim and not yet consumed by the provider; future releases will pass it through
    /// to <c>RuntimeOptions.RuntimeLibraryOrder</c>.
    /// </summary>
    public List<string> RuntimeOrder { get; set; } = [];

    /// <summary>
    /// When true (default), <see cref="LocalWhisperTranscriptionProvider"/> may auto-fetch the
    /// configured model on first use. Mirrors <c>ocr.local.autoDownload</c>. Set false to require
    /// explicit <c>deps install whisper-model …</c>.
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}

public sealed class OpenAiConfig
{
    public List<string> ApiKeyEnvironmentVariables { get; set; } = [];

    public List<string> BaseUrlEnvironmentVariables { get; set; } = [];

    public List<string> ModelEnvironmentVariables { get; set; } = [];

    public List<string> TranscriptionModelEnvironmentVariables { get; set; } = [];

    public string? BaseUrl { get; set; }

    public string? Model { get; set; }

    public string? TranscriptionModel { get; set; }
}

public sealed class AzureOpenAiConfig
{
    public List<string> EndpointEnvironmentVariables { get; set; } = [];

    public List<string> ApiKeyEnvironmentVariables { get; set; } = [];

    public List<string> DeploymentEnvironmentVariables { get; set; } = [];

    public List<string> ModelEnvironmentVariables { get; set; } = [];

    public List<string> ApiVersionEnvironmentVariables { get; set; } = [];

    public string? Endpoint { get; set; }

    public string? Deployment { get; set; }

    public string? Model { get; set; }

    public string? ApiVersion { get; set; }
}

public sealed class ConfigStore
{
    private const string ConfigDirectoryName = "Zakira.Replay";
    private const string ConfigFileName = "Zakira.Replay.json";

    // Legacy paths from the pre-rename "VideoWatcher" era. On first load, contents are migrated
    // to the new location and the legacy file (and now-empty legacy directory) are removed.
    // This logic should be removed in a future major version.
    private const string LegacyConfigDirectoryName = "VideoWatcher";
    private static readonly string[] LegacyConfigFileNames = ["VideoWatcher.json", "VideoWatcher.config", "config.json"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigStore(string? configPath = null)
    {
        ConfigPath = configPath ?? GetDefaultConfigPath();
    }

    public string ConfigPath { get; }

    public static string GetDefaultConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(xdgConfigHome)), ConfigDirectoryName, ConfigFileName);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ConfigDirectoryName, ConfigFileName);
    }

    public static ReplayConfig CreateDefaultConfig()
    {
        return new ReplayConfig
        {
            Dependencies = new DependencyPathConfig
            {
                AutoDownload = false,
                PortableDirectory = PortableDependencyInstaller.GetDefaultPortableDirectory()
            },
            Search = new SearchConfig
            {
                Onnx = new OnnxEmbeddingConfig
                {
                    AutoDownload = false,
                    // 0.10.0 default; matches KnownSearchEmbeddingModels.DefaultModel. We
                    // intentionally don't set ModelDirectory / ModelFile here so the
                    // installer resolves the directory from the configured model id at
                    // call time — that way switching `search.onnx.model` between known ids
                    // automatically picks up the new layout without a config rewrite.
                    Model = KnownSearchEmbeddingModels.DefaultModel
                }
            },
            Llm = new LlmConfig
            {
                Provider = LlmProviders.GitHubCopilot,
                OpenAi = new OpenAiConfig
                {
                    ApiKeyEnvironmentVariables = ["OPENAI_API_KEY"],
                    BaseUrlEnvironmentVariables = ["OPENAI_BASE_URL"],
                    ModelEnvironmentVariables = ["OPENAI_MODEL"],
                    TranscriptionModelEnvironmentVariables = ["OPENAI_TRANSCRIPTION_MODEL"]
                },
                AzureOpenAi = new AzureOpenAiConfig
                {
                    EndpointEnvironmentVariables = ["AZURE_OPENAI_ENDPOINT"],
                    ApiKeyEnvironmentVariables = ["AZURE_OPENAI_API_KEY"],
                    DeploymentEnvironmentVariables = ["AZURE_OPENAI_DEPLOYMENT"],
                    ModelEnvironmentVariables = ["AZURE_OPENAI_MODEL"],
                    ApiVersionEnvironmentVariables = ["AZURE_OPENAI_API_VERSION"]
                },
                Ollama = new OllamaConfig
                {
                    EndpointEnvironmentVariables = ["ZAKIRA_REPLAY_OLLAMA_ENDPOINT", "OLLAMA_HOST"],
                    ModelEnvironmentVariables = ["ZAKIRA_REPLAY_OLLAMA_MODEL"],
                    VisionModelEnvironmentVariables = ["ZAKIRA_REPLAY_OLLAMA_VISION_MODEL"],
                    Endpoint = OllamaLlmProvider.DefaultEndpoint
                },
                LocalWhisper = new LocalWhisperConfig
                {
                    ModelSize = LocalWhisperOptions.DefaultModelSize,
                    Language = LocalWhisperOptions.DefaultLanguage,
                    AutoDownload = true
                }
            },
            Ocr = new OcrConfig
            {
                Provider = OcrProviders.Local,
                Local = new LocalOcrConfig
                {
                    AutoDownload = true,
                    ModelDirectory = PortableDependencyInstaller.GetDefaultOcrModelDirectory(),
                    LanguagePack = OcrLanguagePacks.Latin
                }
            },
            Captions = new CaptionsConfig
            {
                Languages = ["auto"]
            },
            Slides = new SlidesConfig
            {
                Enabled = true,
                HashDistance = 6
            },
            Frames = new FramesConfig
            {
                SceneSafetyCap = 5000,
                PerMinute = 12
            },
            Crop = new CropConfig
            {
                Enabled = false,
                Profile = SmartCropProfiles.Auto
            },
            Capture = new CaptureConfig
            {
                Mode = CaptureModes.YtDlp,
                Browser = new BrowserCaptureConfig
                {
                    VideoElementSelector = "video",
                    SeekWaitSeconds = 2.5,
                    DurationProbeTimeoutSeconds = 20.0,
                    JpegQuality = 90,
                    CaptureCaptions = true,
                    MaxCaptionBytes = 5 * 1024 * 1024
                }
            },
            Auth = new AuthConfig
            {
                StaleThresholdMinutes = 60
            },
            Diarization = new DiarizationConfig
            {
                Provider = DiarizationProviders.SherpaOnnx,
                ModelDirectory = PortableDependencyInstaller.GetDefaultDiarizationModelDirectory(),
                MinDurationOnSeconds = 0.3f,
                MinDurationOffSeconds = 0.5f,
                Threads = 1,
                AutoDownload = true
            }
        };
    }

    public async Task<ReplayConfig> EnsureExistsAsync(CancellationToken cancellationToken)
    {
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(ConfigPath))
        {
            await SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }

        return config;
    }

    public async Task<ReplayConfig> LoadAsync(CancellationToken cancellationToken)
    {
        TryMigrateLegacyConfig();
        if (!File.Exists(ConfigPath))
        {
            return CreateDefaultConfig();
        }

        await using var stream = File.OpenRead(ConfigPath);
        return await JsonSerializer.DeserializeAsync<ReplayConfig>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new ReplayConfig();
    }

    public ReplayConfig Load()
    {
        TryMigrateLegacyConfig();
        if (!File.Exists(ConfigPath))
        {
            return CreateDefaultConfig();
        }

        return JsonSerializer.Deserialize<ReplayConfig>(File.ReadAllText(ConfigPath), JsonOptions)
            ?? new ReplayConfig();
    }

    public async Task SaveAsync(ReplayConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private void TryMigrateLegacyConfig()
    {
        var legacyPath = FindLegacyConfigPath();
        if (legacyPath is null)
        {
            return;
        }

        if (!File.Exists(ConfigPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.Copy(legacyPath, ConfigPath, overwrite: false);
            }
            catch
            {
                // Migration is best-effort. If it fails, leave the legacy file in place
                // so the user can resolve it manually.
                return;
            }
        }

        TryDeleteLegacy(legacyPath);
    }

    private string? FindLegacyConfigPath()
    {
        var newDir = Path.GetDirectoryName(ConfigPath);
        if (string.IsNullOrEmpty(newDir))
        {
            return null;
        }

        var parentOfNewDir = Path.GetDirectoryName(newDir);
        if (string.IsNullOrEmpty(parentOfNewDir))
        {
            return null;
        }

        var legacyDir = Path.Combine(parentOfNewDir, LegacyConfigDirectoryName);
        if (!Directory.Exists(legacyDir))
        {
            return null;
        }

        foreach (var fileName in LegacyConfigFileNames)
        {
            var candidate = Path.Combine(legacyDir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void TryDeleteLegacy(string legacyPath)
    {
        try
        {
            File.Delete(legacyPath);
            var legacyDir = Path.GetDirectoryName(legacyPath);
            if (!string.IsNullOrEmpty(legacyDir)
                && Directory.Exists(legacyDir)
                && !Directory.EnumerateFileSystemEntries(legacyDir).Any())
            {
                Directory.Delete(legacyDir);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore errors.
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        switch (NormalizeKey(key))
        {
            case "yt-dlp.path":
                config.Dependencies.YtDlpPath = NormalizeExecutablePath(value, "yt-dlp.exe");
                break;
            case "dependencies.autodownload":
            case "dependencies.auto-download":
                config.Dependencies.AutoDownload = ParseBool(value, key);
                break;
            case "dependencies.portabledirectory":
            case "dependencies.portable-directory":
            case "dependencies.directory":
                config.Dependencies.PortableDirectory = NormalizeDirectoryPath(value);
                break;
            case "runs.directory":
            case "runs.dir":
            case "runs.outputdirectory":
            case "runs.output-directory":
                // Preserve env-var literals so the config is portable across machines whose
                // %LOCALAPPDATA% / $HOME expand differently. Resolution happens at read time
                // via ArtifactStore.ResolveRootDirectory(config).
                config.Runs.Directory = NormalizePathPreservingEnvVars(value, key);
                break;
            case "ffmpeg.path":
                config.Dependencies.FfmpegPath = NormalizeExecutablePath(value, "ffmpeg.exe");
                break;
            case "ffprobe.path":
                config.Dependencies.FfprobePath = NormalizeExecutablePath(value, "ffprobe.exe");
                break;
            case "edge.path":
                config.Dependencies.EdgePath = NormalizeExecutablePath(value, "msedge.exe");
                break;
            case "search.onnx.modelpath":
            case "search.onnx.model-path":
                config.Search.Onnx.ModelPath = NormalizeFilePath(value);
                break;
            case "search.onnx.vocabularypath":
            case "search.onnx.vocabulary-path":
            case "search.onnx.vocabpath":
            case "search.onnx.vocab-path":
                config.Search.Onnx.VocabularyPath = NormalizeFilePath(value);
                break;
            case "search.onnx.maxsequencelength":
            case "search.onnx.max-sequence-length":
                config.Search.Onnx.MaxSequenceLength = ParsePositiveInt(value, key);
                break;
            case "search.onnx.embeddingdimensions":
            case "search.onnx.embedding-dimensions":
                config.Search.Onnx.EmbeddingDimensions = ParsePositiveInt(value, key);
                break;
            case "search.onnx.autodownload":
            case "search.onnx.auto-download":
                config.Search.Onnx.AutoDownload = ParseBool(value, key);
                break;
            case "search.onnx.modeldirectory":
            case "search.onnx.model-directory":
                config.Search.Onnx.ModelDirectory = NormalizeDirectoryPath(value);
                break;
            case "search.onnx.modelfile":
            case "search.onnx.model-file":
                config.Search.Onnx.ModelFile = NormalizeNonEmpty(value, key);
                break;
            case "search.onnx.model":
            case "search.onnx.modelname":
            case "search.onnx.model-name":
                // Stored as-is so users can swap between known ids and custom strings without
                // path normalization mangling them.
                config.Search.Onnx.Model = NormalizeNonEmpty(value, key);
                break;
            case "search.onnx.modelkind":
            case "search.onnx.model-kind":
            case "search.onnx.kind":
                // Validate up-front so a typo doesn't survive into a silent fallback at
                // embed time. ParseKind throws ReplayException on unknown values.
                _ = OnnxSearchEmbeddingProvider.ParseKind(value);
                config.Search.Onnx.ModelKind = value.Trim().ToLowerInvariant();
                break;
            case "search.onnx.tokenizerpath":
            case "search.onnx.tokenizer-path":
            case "search.onnx.tokenizer":
                config.Search.Onnx.TokenizerPath = NormalizeFilePath(value);
                break;
            case "llm.provider":
                config.Llm.Provider = LlmProviderFactory.Normalize(value);
                break;
            case "llm.openai.baseurl":
            case "llm.openai.base-url":
                config.Llm.OpenAi.BaseUrl = NormalizeUrl(value, key);
                break;
            case "llm.openai.apikeyenvvars":
            case "llm.openai.api-key-env-vars":
            case "llm.openai.apikeyenvironmentvariables":
            case "llm.openai.api-key-environment-variables":
                config.Llm.OpenAi.ApiKeyEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.openai.baseurlenvvars":
            case "llm.openai.base-url-env-vars":
            case "llm.openai.baseurlenvironmentvariables":
            case "llm.openai.base-url-environment-variables":
                config.Llm.OpenAi.BaseUrlEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.openai.modelenvvars":
            case "llm.openai.model-env-vars":
            case "llm.openai.modelenvironmentvariables":
            case "llm.openai.model-environment-variables":
                config.Llm.OpenAi.ModelEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.openai.transcriptionmodelenvvars":
            case "llm.openai.transcription-model-env-vars":
            case "llm.openai.transcriptionmodelenvironmentvariables":
            case "llm.openai.transcription-model-environment-variables":
                config.Llm.OpenAi.TranscriptionModelEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.openai.model":
                config.Llm.OpenAi.Model = NormalizeNonEmpty(value, key);
                break;
            case "llm.openai.transcriptionmodel":
            case "llm.openai.transcription-model":
                config.Llm.OpenAi.TranscriptionModel = NormalizeNonEmpty(value, key);
                break;
            case "llm.azureopenai.endpoint":
            case "llm.azure-openai.endpoint":
                config.Llm.AzureOpenAi.Endpoint = NormalizeUrl(value, key).TrimEnd('/');
                break;
            case "llm.azureopenai.endpointenvvars":
            case "llm.azure-openai.endpoint-env-vars":
            case "llm.azureopenai.endpointenvironmentvariables":
            case "llm.azure-openai.endpoint-environment-variables":
                config.Llm.AzureOpenAi.EndpointEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.azureopenai.apikeyenvvars":
            case "llm.azure-openai.api-key-env-vars":
            case "llm.azureopenai.apikeyenvironmentvariables":
            case "llm.azure-openai.api-key-environment-variables":
                config.Llm.AzureOpenAi.ApiKeyEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.azureopenai.deploymentenvvars":
            case "llm.azure-openai.deployment-env-vars":
            case "llm.azureopenai.deploymentenvironmentvariables":
            case "llm.azure-openai.deployment-environment-variables":
                config.Llm.AzureOpenAi.DeploymentEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.azureopenai.modelenvvars":
            case "llm.azure-openai.model-env-vars":
            case "llm.azureopenai.modelenvironmentvariables":
            case "llm.azure-openai.model-environment-variables":
                config.Llm.AzureOpenAi.ModelEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.azureopenai.apiversionenvvars":
            case "llm.azure-openai.api-version-env-vars":
            case "llm.azureopenai.apiversionenvironmentvariables":
            case "llm.azure-openai.api-version-environment-variables":
                config.Llm.AzureOpenAi.ApiVersionEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.azureopenai.deployment":
            case "llm.azure-openai.deployment":
                config.Llm.AzureOpenAi.Deployment = NormalizeNonEmpty(value, key);
                break;
            case "llm.azureopenai.model":
            case "llm.azure-openai.model":
                config.Llm.AzureOpenAi.Model = NormalizeNonEmpty(value, key);
                break;
            case "llm.azureopenai.apiversion":
            case "llm.azure-openai.api-version":
                config.Llm.AzureOpenAi.ApiVersion = NormalizeNonEmpty(value, key);
                break;
            case "llm.ollama.endpoint":
                config.Llm.Ollama.Endpoint = NormalizeUrl(value, key).TrimEnd('/');
                break;
            case "llm.ollama.endpointenvvars":
            case "llm.ollama.endpoint-env-vars":
            case "llm.ollama.endpointenvironmentvariables":
            case "llm.ollama.endpoint-environment-variables":
                config.Llm.Ollama.EndpointEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.ollama.model":
                config.Llm.Ollama.Model = NormalizeNonEmpty(value, key);
                break;
            case "llm.ollama.modelenvvars":
            case "llm.ollama.model-env-vars":
            case "llm.ollama.modelenvironmentvariables":
            case "llm.ollama.model-environment-variables":
                config.Llm.Ollama.ModelEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.ollama.visionmodel":
            case "llm.ollama.vision-model":
                config.Llm.Ollama.VisionModel = NormalizeNonEmpty(value, key);
                break;
            case "llm.ollama.visionmodelenvvars":
            case "llm.ollama.vision-model-env-vars":
            case "llm.ollama.visionmodelenvironmentvariables":
            case "llm.ollama.vision-model-environment-variables":
                config.Llm.Ollama.VisionModelEnvironmentVariables = ParseEnvironmentVariableNames(value, key);
                break;
            case "llm.ollama.timeoutseconds":
            case "llm.ollama.timeout-seconds":
                config.Llm.Ollama.TimeoutSeconds = ParsePositiveInt(value, key);
                break;
            case "llm.localwhisper.modelpath":
            case "llm.local-whisper.model-path":
            case "llm.localwhisper.path":
            case "llm.local-whisper.path":
                config.Llm.LocalWhisper.ModelPath = NormalizeFilePath(value);
                break;
            case "llm.localwhisper.modelsize":
            case "llm.local-whisper.model-size":
            case "llm.localwhisper.size":
            case "llm.local-whisper.size":
                config.Llm.LocalWhisper.ModelSize = LocalWhisperOptions.NormalizeModelSize(NormalizeNonEmpty(value, key));
                break;
            case "llm.localwhisper.language":
            case "llm.local-whisper.language":
                config.Llm.LocalWhisper.Language = NormalizeNonEmpty(value, key).ToLowerInvariant();
                break;
            case "llm.localwhisper.threads":
            case "llm.local-whisper.threads":
                config.Llm.LocalWhisper.Threads = ParsePositiveInt(value, key);
                break;
            case "llm.localwhisper.runtimeorder":
            case "llm.local-whisper.runtime-order":
                config.Llm.LocalWhisper.RuntimeOrder = ParseRuntimeOrder(value, key);
                break;
            case "llm.localwhisper.autodownload":
            case "llm.local-whisper.auto-download":
                config.Llm.LocalWhisper.AutoDownload = ParseBool(value, key);
                break;
            case "captions.languages":
                config.Captions.Languages = ParseCaptionLanguages(value, key);
                break;
            case "slides.enabled":
                config.Slides.Enabled = ParseBool(value, key);
                break;
            case "slides.hashdistance":
            case "slides.hash-distance":
                config.Slides.HashDistance = ParseHashDistance(value, key);
                break;
            case "frames.scenesafetycap":
            case "frames.scene-safety-cap":
                config.Frames.SceneSafetyCap = ParseSceneSafetyCap(value, key);
                break;
            case "frames.perminute":
            case "frames.per-minute":
                config.Frames.PerMinute = ParseFramesPerMinute(value, key);
                break;
            case "ocr.provider":
                config.Ocr.Provider = OcrProviderFactory.Normalize(value);
                break;
            case "ocr.local.modeldirectory":
            case "ocr.local.model-directory":
                config.Ocr.Local.ModelDirectory = NormalizeDirectoryPath(value);
                break;
            case "ocr.local.detectionmodelpath":
            case "ocr.local.detection-model-path":
            case "ocr.local.detmodelpath":
            case "ocr.local.det-model-path":
                config.Ocr.Local.DetectionModelPath = NormalizeFilePath(value);
                break;
            case "ocr.local.classificationmodelpath":
            case "ocr.local.classification-model-path":
            case "ocr.local.clsmodelpath":
            case "ocr.local.cls-model-path":
                config.Ocr.Local.ClassificationModelPath = NormalizeFilePath(value);
                break;
            case "ocr.local.recognitionmodelpath":
            case "ocr.local.recognition-model-path":
            case "ocr.local.recmodelpath":
            case "ocr.local.rec-model-path":
                config.Ocr.Local.RecognitionModelPath = NormalizeFilePath(value);
                break;
            case "ocr.local.dictionarypath":
            case "ocr.local.dictionary-path":
            case "ocr.local.keyspath":
            case "ocr.local.keys-path":
                config.Ocr.Local.DictionaryPath = NormalizeFilePath(value);
                break;
            case "ocr.local.autodownload":
            case "ocr.local.auto-download":
                config.Ocr.Local.AutoDownload = ParseBool(value, key);
                break;
            case "ocr.local.languagepack":
            case "ocr.local.language-pack":
            case "ocr.local.language":
            case "ocr.local.lang":
            case "ocr.local.pack":
                if (!OcrLanguagePacks.TryGet(value, out var resolvedPack))
                {
                    throw new ReplayException(
                        $"Unknown OCR language pack: '{value}'. Known packs: {string.Join(", ", OcrLanguagePacks.All.Select(p => p.Name))}.");
                }
                config.Ocr.Local.LanguagePack = resolvedPack.Name;
                break;
            case "vision.provider":
                config.Vision.Provider = VisionProviderFactory.Normalize(value);
                break;
            case "vision.local.mode":
                config.Vision.Local.Mode = VisionProviderFactory.FormatMode(VisionProviderFactory.NormalizeMode(value));
                break;
            case "vision.local.modeldirectory":
            case "vision.local.model-directory":
                config.Vision.Local.ModelDirectory = NormalizeDirectoryPath(value);
                break;
            case "vision.local.clipimageencoderpath":
            case "vision.local.clip-image-encoder-path":
            case "vision.local.clip.imageencoderpath":
            case "vision.local.clip.image-encoder-path":
                config.Vision.Local.ClipImageEncoderPath = NormalizeFilePath(value);
                break;
            case "vision.local.cliptextencoderpath":
            case "vision.local.clip-text-encoder-path":
            case "vision.local.clip.textencoderpath":
            case "vision.local.clip.text-encoder-path":
                config.Vision.Local.ClipTextEncoderPath = NormalizeFilePath(value);
                break;
            case "vision.local.clipkindembeddingspath":
            case "vision.local.clip-kind-embeddings-path":
            case "vision.local.clip.kindembeddingspath":
            case "vision.local.clip.kind-embeddings-path":
                config.Vision.Local.ClipKindEmbeddingsPath = NormalizeFilePath(value);
                break;
            case "vision.local.florencevisionencoderpath":
            case "vision.local.florence-vision-encoder-path":
            case "vision.local.florence.visionencoderpath":
                config.Vision.Local.FlorenceVisionEncoderPath = NormalizeFilePath(value);
                break;
            case "vision.local.florenceencoderpath":
            case "vision.local.florence-encoder-path":
            case "vision.local.florence.encoderpath":
                config.Vision.Local.FlorenceEncoderPath = NormalizeFilePath(value);
                break;
            case "vision.local.florencedecoderpath":
            case "vision.local.florence-decoder-path":
            case "vision.local.florence.decoderpath":
                config.Vision.Local.FlorenceDecoderPath = NormalizeFilePath(value);
                break;
            case "vision.local.florenceembedtokenspath":
            case "vision.local.florence-embed-tokens-path":
            case "vision.local.florence.embedtokenspath":
                config.Vision.Local.FlorenceEmbedTokensPath = NormalizeFilePath(value);
                break;
            case "vision.local.florencevocabpath":
            case "vision.local.florence-vocab-path":
            case "vision.local.florence.vocabpath":
                config.Vision.Local.FlorenceVocabPath = NormalizeFilePath(value);
                break;
            case "vision.local.florencemergespath":
            case "vision.local.florence-merges-path":
            case "vision.local.florence.mergespath":
                config.Vision.Local.FlorenceMergesPath = NormalizeFilePath(value);
                break;
            case "vision.local.florenceaddedtokenspath":
            case "vision.local.florence-added-tokens-path":
            case "vision.local.florence.addedtokenspath":
                config.Vision.Local.FlorenceAddedTokensPath = NormalizeFilePath(value);
                break;
            case "vision.local.florencemaxtokens":
            case "vision.local.florence-max-tokens":
                config.Vision.Local.FlorenceMaxTokens = ParsePositiveInt(value, key);
                break;
            case "vision.local.florencequantization":
            case "vision.local.florence-quantization":
                config.Vision.Local.FlorenceQuantization = LocalVisionOptions.NormalizeQuantization(value);
                break;
            case "vision.local.autodownload":
            case "vision.local.auto-download":
                config.Vision.Local.AutoDownload = ParseBool(value, key);
                break;
            case "crop.enabled":
            case "smartcrop.enabled":
            case "smart-crop.enabled":
                config.Crop.Enabled = ParseBool(value, key);
                break;
            case "crop.profile":
            case "smartcrop.profile":
            case "smart-crop.profile":
                config.Crop.Profile = SmartCropProfiles.Normalize(value);
                break;
            case "capture.mode":
                config.Capture.Mode = CaptureModes.Normalize(value);
                break;
            case "capture.browser.playbuttonselector":
            case "capture.browser.play-button-selector":
                config.Capture.Browser.PlayButtonSelector = NormalizeNonEmpty(value, key);
                break;
            case "capture.browser.videoelementselector":
            case "capture.browser.video-element-selector":
                config.Capture.Browser.VideoElementSelector = NormalizeNonEmpty(value, key);
                break;
            case "capture.browser.seekwaitseconds":
            case "capture.browser.seek-wait-seconds":
                config.Capture.Browser.SeekWaitSeconds = ParsePositiveDouble(value, key);
                break;
            case "capture.browser.durationprobetimeoutseconds":
            case "capture.browser.duration-probe-timeout-seconds":
                config.Capture.Browser.DurationProbeTimeoutSeconds = ParsePositiveDouble(value, key);
                break;
            case "capture.browser.jpegquality":
            case "capture.browser.jpeg-quality":
                config.Capture.Browser.JpegQuality = ParseJpegQuality(value, key);
                break;
            case "capture.browser.capturecaptions":
            case "capture.browser.capture-captions":
                config.Capture.Browser.CaptureCaptions = ParseBool(value, key);
                break;
            case "capture.browser.maxcaptionbytes":
            case "capture.browser.max-caption-bytes":
                config.Capture.Browser.MaxCaptionBytes = ParsePositiveInt(value, key);
                break;
            case "capture.browser.edgeuserdatadir":
            case "capture.browser.edge-user-data-dir":
                config.Capture.Browser.EdgeUserDataDir = NormalizePathPreservingEnvVars(value, key);
                break;
            case "capture.browser.edgeprofiledirectory":
            case "capture.browser.edge-profile-directory":
                config.Capture.Browser.EdgeProfileDirectory = NormalizeNonEmpty(value, key);
                break;
            case "capture.browser.debug":
                config.Capture.Browser.Debug = ParseBool(value, key);
                break;
            case "capture.browser.debugmaxbodybytes":
            case "capture.browser.debug-max-body-bytes":
                config.Capture.Browser.DebugMaxBodyBytes = ParsePositiveInt(value, key);
                break;
            case "auth.directory":
                config.Auth.Directory = NormalizeDirectoryPath(value);
                break;
            case "auth.stalethresholdminutes":
            case "auth.stale-threshold-minutes":
                config.Auth.StaleThresholdMinutes = ParsePositiveInt(value, key);
                break;
            case "diarization.provider":
                config.Diarization.Provider = DiarizationProviderFactory.Normalize(value);
                break;
            case "diarization.modeldirectory":
            case "diarization.model-directory":
                config.Diarization.ModelDirectory = NormalizeDirectoryPath(value);
                break;
            case "diarization.segmentationmodelpath":
            case "diarization.segmentation-model-path":
                config.Diarization.SegmentationModelPath = NormalizeFilePath(value);
                break;
            case "diarization.embeddingmodelpath":
            case "diarization.embedding-model-path":
                config.Diarization.EmbeddingModelPath = NormalizeFilePath(value);
                break;
            case "diarization.numspeakers":
            case "diarization.num-speakers":
                config.Diarization.NumSpeakers = ParsePositiveInt(value, key);
                break;
            case "diarization.threshold":
                config.Diarization.Threshold = (float)ParsePositiveDouble(value, key);
                break;
            case "diarization.mindurationonseconds":
            case "diarization.min-duration-on-seconds":
                config.Diarization.MinDurationOnSeconds = (float)ParsePositiveDouble(value, key);
                break;
            case "diarization.mindurationoffseconds":
            case "diarization.min-duration-off-seconds":
                config.Diarization.MinDurationOffSeconds = (float)ParsePositiveDouble(value, key);
                break;
            case "diarization.threads":
                config.Diarization.Threads = ParsePositiveInt(value, key);
                break;
            case "diarization.autodownload":
            case "diarization.auto-download":
                config.Diarization.AutoDownload = ParseBool(value, key);
                break;
            default:
                throw new ReplayException($"Unknown config key: {key}");
        }

        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return NormalizeKey(key) switch
        {
            "yt-dlp.path" => config.Dependencies.YtDlpPath,
            "dependencies.autodownload" or "dependencies.auto-download" => config.Dependencies.AutoDownload.ToString(),
            "dependencies.portabledirectory" or "dependencies.portable-directory" or "dependencies.directory" => config.Dependencies.PortableDirectory,
            "runs.directory" or "runs.dir" or "runs.outputdirectory" or "runs.output-directory" => config.Runs.Directory,
            "ffmpeg.path" => config.Dependencies.FfmpegPath,
            "ffprobe.path" => config.Dependencies.FfprobePath,
            "edge.path" => config.Dependencies.EdgePath,
            "search.onnx.modelpath" or "search.onnx.model-path" => config.Search.Onnx.ModelPath,
            "search.onnx.vocabularypath" or "search.onnx.vocabulary-path" or "search.onnx.vocabpath" or "search.onnx.vocab-path" => config.Search.Onnx.VocabularyPath,
            "search.onnx.maxsequencelength" or "search.onnx.max-sequence-length" => config.Search.Onnx.MaxSequenceLength?.ToString(),
            "search.onnx.embeddingdimensions" or "search.onnx.embedding-dimensions" => config.Search.Onnx.EmbeddingDimensions?.ToString(),
            "search.onnx.autodownload" or "search.onnx.auto-download" => config.Search.Onnx.AutoDownload.ToString(),
            "search.onnx.modeldirectory" or "search.onnx.model-directory" => config.Search.Onnx.ModelDirectory,
            "search.onnx.modelfile" or "search.onnx.model-file" => config.Search.Onnx.ModelFile,
            "search.onnx.model" or "search.onnx.modelname" or "search.onnx.model-name" => config.Search.Onnx.Model,
            "search.onnx.modelkind" or "search.onnx.model-kind" or "search.onnx.kind" => config.Search.Onnx.ModelKind,
            "search.onnx.tokenizerpath" or "search.onnx.tokenizer-path" or "search.onnx.tokenizer" => config.Search.Onnx.TokenizerPath,
            "llm.provider" => config.Llm.Provider,
            "llm.openai.baseurl" or "llm.openai.base-url" => config.Llm.OpenAi.BaseUrl,
            "llm.openai.apikeyenvvars" or "llm.openai.api-key-env-vars" or "llm.openai.apikeyenvironmentvariables" or "llm.openai.api-key-environment-variables" => FormatEnvironmentVariableNames(config.Llm.OpenAi.ApiKeyEnvironmentVariables),
            "llm.openai.baseurlenvvars" or "llm.openai.base-url-env-vars" or "llm.openai.baseurlenvironmentvariables" or "llm.openai.base-url-environment-variables" => FormatEnvironmentVariableNames(config.Llm.OpenAi.BaseUrlEnvironmentVariables),
            "llm.openai.modelenvvars" or "llm.openai.model-env-vars" or "llm.openai.modelenvironmentvariables" or "llm.openai.model-environment-variables" => FormatEnvironmentVariableNames(config.Llm.OpenAi.ModelEnvironmentVariables),
            "llm.openai.transcriptionmodelenvvars" or "llm.openai.transcription-model-env-vars" or "llm.openai.transcriptionmodelenvironmentvariables" or "llm.openai.transcription-model-environment-variables" => FormatEnvironmentVariableNames(config.Llm.OpenAi.TranscriptionModelEnvironmentVariables),
            "llm.openai.model" => config.Llm.OpenAi.Model,
            "llm.openai.transcriptionmodel" or "llm.openai.transcription-model" => config.Llm.OpenAi.TranscriptionModel,
            "llm.azureopenai.endpoint" or "llm.azure-openai.endpoint" => config.Llm.AzureOpenAi.Endpoint,
            "llm.azureopenai.endpointenvvars" or "llm.azure-openai.endpoint-env-vars" or "llm.azureopenai.endpointenvironmentvariables" or "llm.azure-openai.endpoint-environment-variables" => FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.EndpointEnvironmentVariables),
            "llm.azureopenai.apikeyenvvars" or "llm.azure-openai.api-key-env-vars" or "llm.azureopenai.apikeyenvironmentvariables" or "llm.azure-openai.api-key-environment-variables" => FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ApiKeyEnvironmentVariables),
            "llm.azureopenai.deploymentenvvars" or "llm.azure-openai.deployment-env-vars" or "llm.azureopenai.deploymentenvironmentvariables" or "llm.azure-openai.deployment-environment-variables" => FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.DeploymentEnvironmentVariables),
            "llm.azureopenai.modelenvvars" or "llm.azure-openai.model-env-vars" or "llm.azureopenai.modelenvironmentvariables" or "llm.azure-openai.model-environment-variables" => FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ModelEnvironmentVariables),
            "llm.azureopenai.apiversionenvvars" or "llm.azure-openai.api-version-env-vars" or "llm.azureopenai.apiversionenvironmentvariables" or "llm.azure-openai.api-version-environment-variables" => FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ApiVersionEnvironmentVariables),
            "llm.azureopenai.deployment" or "llm.azure-openai.deployment" => config.Llm.AzureOpenAi.Deployment,
            "llm.azureopenai.model" or "llm.azure-openai.model" => config.Llm.AzureOpenAi.Model,
            "llm.azureopenai.apiversion" or "llm.azure-openai.api-version" => config.Llm.AzureOpenAi.ApiVersion,
            "llm.ollama.endpoint" => config.Llm.Ollama.Endpoint,
            "llm.ollama.endpointenvvars" or "llm.ollama.endpoint-env-vars" or "llm.ollama.endpointenvironmentvariables" or "llm.ollama.endpoint-environment-variables" => FormatEnvironmentVariableNames(config.Llm.Ollama.EndpointEnvironmentVariables),
            "llm.ollama.model" => config.Llm.Ollama.Model,
            "llm.ollama.modelenvvars" or "llm.ollama.model-env-vars" or "llm.ollama.modelenvironmentvariables" or "llm.ollama.model-environment-variables" => FormatEnvironmentVariableNames(config.Llm.Ollama.ModelEnvironmentVariables),
            "llm.ollama.visionmodel" or "llm.ollama.vision-model" => config.Llm.Ollama.VisionModel,
            "llm.ollama.visionmodelenvvars" or "llm.ollama.vision-model-env-vars" or "llm.ollama.visionmodelenvironmentvariables" or "llm.ollama.vision-model-environment-variables" => FormatEnvironmentVariableNames(config.Llm.Ollama.VisionModelEnvironmentVariables),
            "llm.ollama.timeoutseconds" or "llm.ollama.timeout-seconds" => config.Llm.Ollama.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture),
            "llm.localwhisper.modelpath" or "llm.local-whisper.model-path" or "llm.localwhisper.path" or "llm.local-whisper.path" => config.Llm.LocalWhisper.ModelPath,
            "llm.localwhisper.modelsize" or "llm.local-whisper.model-size" or "llm.localwhisper.size" or "llm.local-whisper.size" => config.Llm.LocalWhisper.ModelSize,
            "llm.localwhisper.language" or "llm.local-whisper.language" => config.Llm.LocalWhisper.Language,
            "llm.localwhisper.threads" or "llm.local-whisper.threads" => config.Llm.LocalWhisper.Threads?.ToString(CultureInfo.InvariantCulture),
            "llm.localwhisper.runtimeorder" or "llm.local-whisper.runtime-order" => FormatRuntimeOrder(config.Llm.LocalWhisper.RuntimeOrder),
            "llm.localwhisper.autodownload" or "llm.local-whisper.auto-download" => config.Llm.LocalWhisper.AutoDownload.ToString(),
            "captions.languages" => FormatCaptionLanguages(config.Captions.Languages),
            "slides.enabled" => config.Slides.Enabled.ToString(),
            "slides.hashdistance" or "slides.hash-distance" => config.Slides.HashDistance.ToString(CultureInfo.InvariantCulture),
            "frames.scenesafetycap" or "frames.scene-safety-cap" => config.Frames.SceneSafetyCap.ToString(CultureInfo.InvariantCulture),
            "frames.perminute" or "frames.per-minute" => config.Frames.PerMinute.ToString(CultureInfo.InvariantCulture),
            "ocr.provider" => config.Ocr.Provider,
            "ocr.local.modeldirectory" or "ocr.local.model-directory" => config.Ocr.Local.ModelDirectory,
            "ocr.local.detectionmodelpath" or "ocr.local.detection-model-path" or "ocr.local.detmodelpath" or "ocr.local.det-model-path" => config.Ocr.Local.DetectionModelPath,
            "ocr.local.classificationmodelpath" or "ocr.local.classification-model-path" or "ocr.local.clsmodelpath" or "ocr.local.cls-model-path" => config.Ocr.Local.ClassificationModelPath,
            "ocr.local.recognitionmodelpath" or "ocr.local.recognition-model-path" or "ocr.local.recmodelpath" or "ocr.local.rec-model-path" => config.Ocr.Local.RecognitionModelPath,
            "ocr.local.dictionarypath" or "ocr.local.dictionary-path" or "ocr.local.keyspath" or "ocr.local.keys-path" => config.Ocr.Local.DictionaryPath,
            "ocr.local.autodownload" or "ocr.local.auto-download" => config.Ocr.Local.AutoDownload.ToString(),
            "ocr.local.languagepack" or "ocr.local.language-pack" or "ocr.local.language" or "ocr.local.lang" or "ocr.local.pack" => config.Ocr.Local.LanguagePack,
            "vision.provider" => config.Vision.Provider,
            "vision.local.mode" => config.Vision.Local.Mode,
            "vision.local.modeldirectory" or "vision.local.model-directory" => config.Vision.Local.ModelDirectory,
            "vision.local.clipimageencoderpath" or "vision.local.clip-image-encoder-path" or "vision.local.clip.imageencoderpath" or "vision.local.clip.image-encoder-path" => config.Vision.Local.ClipImageEncoderPath,
            "vision.local.cliptextencoderpath" or "vision.local.clip-text-encoder-path" or "vision.local.clip.textencoderpath" or "vision.local.clip.text-encoder-path" => config.Vision.Local.ClipTextEncoderPath,
            "vision.local.clipkindembeddingspath" or "vision.local.clip-kind-embeddings-path" or "vision.local.clip.kindembeddingspath" or "vision.local.clip.kind-embeddings-path" => config.Vision.Local.ClipKindEmbeddingsPath,
            "vision.local.florencevisionencoderpath" or "vision.local.florence-vision-encoder-path" or "vision.local.florence.visionencoderpath" => config.Vision.Local.FlorenceVisionEncoderPath,
            "vision.local.florenceencoderpath" or "vision.local.florence-encoder-path" or "vision.local.florence.encoderpath" => config.Vision.Local.FlorenceEncoderPath,
            "vision.local.florencedecoderpath" or "vision.local.florence-decoder-path" or "vision.local.florence.decoderpath" => config.Vision.Local.FlorenceDecoderPath,
            "vision.local.florenceembedtokenspath" or "vision.local.florence-embed-tokens-path" or "vision.local.florence.embedtokenspath" => config.Vision.Local.FlorenceEmbedTokensPath,
            "vision.local.florencevocabpath" or "vision.local.florence-vocab-path" or "vision.local.florence.vocabpath" => config.Vision.Local.FlorenceVocabPath,
            "vision.local.florencemergespath" or "vision.local.florence-merges-path" or "vision.local.florence.mergespath" => config.Vision.Local.FlorenceMergesPath,
            "vision.local.florenceaddedtokenspath" or "vision.local.florence-added-tokens-path" or "vision.local.florence.addedtokenspath" => config.Vision.Local.FlorenceAddedTokensPath,
            "vision.local.florencemaxtokens" or "vision.local.florence-max-tokens" => config.Vision.Local.FlorenceMaxTokens?.ToString(CultureInfo.InvariantCulture),
            "vision.local.florencequantization" or "vision.local.florence-quantization" => config.Vision.Local.FlorenceQuantization,
            "vision.local.autodownload" or "vision.local.auto-download" => config.Vision.Local.AutoDownload.ToString(),
            "crop.enabled" or "smartcrop.enabled" or "smart-crop.enabled" => config.Crop.Enabled.ToString(),
            "crop.profile" or "smartcrop.profile" or "smart-crop.profile" => config.Crop.Profile,
            "capture.mode" => config.Capture.Mode,
            "capture.browser.playbuttonselector" or "capture.browser.play-button-selector" => config.Capture.Browser.PlayButtonSelector,
            "capture.browser.videoelementselector" or "capture.browser.video-element-selector" => config.Capture.Browser.VideoElementSelector,
            "capture.browser.seekwaitseconds" or "capture.browser.seek-wait-seconds" => config.Capture.Browser.SeekWaitSeconds.ToString(CultureInfo.InvariantCulture),
            "capture.browser.durationprobetimeoutseconds" or "capture.browser.duration-probe-timeout-seconds" => config.Capture.Browser.DurationProbeTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "capture.browser.jpegquality" or "capture.browser.jpeg-quality" => config.Capture.Browser.JpegQuality.ToString(CultureInfo.InvariantCulture),
            "capture.browser.capturecaptions" or "capture.browser.capture-captions" => config.Capture.Browser.CaptureCaptions.ToString(),
            "capture.browser.maxcaptionbytes" or "capture.browser.max-caption-bytes" => config.Capture.Browser.MaxCaptionBytes.ToString(CultureInfo.InvariantCulture),
            "capture.browser.edgeuserdatadir" or "capture.browser.edge-user-data-dir" => config.Capture.Browser.EdgeUserDataDir,
            "capture.browser.edgeprofiledirectory" or "capture.browser.edge-profile-directory" => config.Capture.Browser.EdgeProfileDirectory,
            "capture.browser.debug" => config.Capture.Browser.Debug.ToString(),
            "capture.browser.debugmaxbodybytes" or "capture.browser.debug-max-body-bytes" => config.Capture.Browser.DebugMaxBodyBytes.ToString(CultureInfo.InvariantCulture),
            "auth.directory" => config.Auth.Directory,
            "auth.stalethresholdminutes" or "auth.stale-threshold-minutes" => config.Auth.StaleThresholdMinutes.ToString(CultureInfo.InvariantCulture),
            "diarization.provider" => config.Diarization.Provider,
            "diarization.modeldirectory" or "diarization.model-directory" => config.Diarization.ModelDirectory,
            "diarization.segmentationmodelpath" or "diarization.segmentation-model-path" => config.Diarization.SegmentationModelPath,
            "diarization.embeddingmodelpath" or "diarization.embedding-model-path" => config.Diarization.EmbeddingModelPath,
            "diarization.numspeakers" or "diarization.num-speakers" => config.Diarization.NumSpeakers?.ToString(CultureInfo.InvariantCulture),
            "diarization.threshold" => config.Diarization.Threshold?.ToString(CultureInfo.InvariantCulture),
            "diarization.mindurationonseconds" or "diarization.min-duration-on-seconds" => config.Diarization.MinDurationOnSeconds?.ToString(CultureInfo.InvariantCulture),
            "diarization.mindurationoffseconds" or "diarization.min-duration-off-seconds" => config.Diarization.MinDurationOffSeconds?.ToString(CultureInfo.InvariantCulture),
            "diarization.threads" => config.Diarization.Threads?.ToString(CultureInfo.InvariantCulture),
            "diarization.autodownload" or "diarization.auto-download" => config.Diarization.AutoDownload.ToString(),
            _ => throw new ReplayException($"Unknown config key: {key}")
        };
    }

    public static IReadOnlyDictionary<string, string?> ToFlatDictionary(ReplayConfig config)
    {
        return new Dictionary<string, string?>
        {
            ["yt-dlp.path"] = config.Dependencies.YtDlpPath,
            ["dependencies.autoDownload"] = config.Dependencies.AutoDownload.ToString(),
            ["dependencies.portableDirectory"] = config.Dependencies.PortableDirectory,
            ["runs.directory"] = config.Runs.Directory,
            ["ffmpeg.path"] = config.Dependencies.FfmpegPath,
            ["ffprobe.path"] = config.Dependencies.FfprobePath,
            ["edge.path"] = config.Dependencies.EdgePath,
            ["search.onnx.modelPath"] = config.Search.Onnx.ModelPath,
            ["search.onnx.vocabularyPath"] = config.Search.Onnx.VocabularyPath,
            ["search.onnx.maxSequenceLength"] = config.Search.Onnx.MaxSequenceLength?.ToString(),
            ["search.onnx.embeddingDimensions"] = config.Search.Onnx.EmbeddingDimensions?.ToString(),
            ["search.onnx.autoDownload"] = config.Search.Onnx.AutoDownload.ToString(),
            ["search.onnx.modelDirectory"] = config.Search.Onnx.ModelDirectory,
            ["search.onnx.modelFile"] = config.Search.Onnx.ModelFile,
            ["search.onnx.model"] = config.Search.Onnx.Model,
            ["search.onnx.modelKind"] = config.Search.Onnx.ModelKind,
            ["search.onnx.tokenizerPath"] = config.Search.Onnx.TokenizerPath,
            ["llm.provider"] = config.Llm.Provider,
            ["llm.openai.apiKeyEnvVars"] = FormatEnvironmentVariableNames(config.Llm.OpenAi.ApiKeyEnvironmentVariables),
            ["llm.openai.baseUrlEnvVars"] = FormatEnvironmentVariableNames(config.Llm.OpenAi.BaseUrlEnvironmentVariables),
            ["llm.openai.modelEnvVars"] = FormatEnvironmentVariableNames(config.Llm.OpenAi.ModelEnvironmentVariables),
            ["llm.openai.transcriptionModelEnvVars"] = FormatEnvironmentVariableNames(config.Llm.OpenAi.TranscriptionModelEnvironmentVariables),
            ["llm.openai.baseUrl"] = config.Llm.OpenAi.BaseUrl,
            ["llm.openai.model"] = config.Llm.OpenAi.Model,
            ["llm.openai.transcriptionModel"] = config.Llm.OpenAi.TranscriptionModel,
            ["llm.azureOpenAi.endpointEnvVars"] = FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.EndpointEnvironmentVariables),
            ["llm.azureOpenAi.apiKeyEnvVars"] = FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ApiKeyEnvironmentVariables),
            ["llm.azureOpenAi.deploymentEnvVars"] = FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.DeploymentEnvironmentVariables),
            ["llm.azureOpenAi.modelEnvVars"] = FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ModelEnvironmentVariables),
            ["llm.azureOpenAi.apiVersionEnvVars"] = FormatEnvironmentVariableNames(config.Llm.AzureOpenAi.ApiVersionEnvironmentVariables),
            ["llm.azureOpenAi.endpoint"] = config.Llm.AzureOpenAi.Endpoint,
            ["llm.azureOpenAi.deployment"] = config.Llm.AzureOpenAi.Deployment,
            ["llm.azureOpenAi.model"] = config.Llm.AzureOpenAi.Model,
            ["llm.azureOpenAi.apiVersion"] = config.Llm.AzureOpenAi.ApiVersion,
            ["llm.ollama.endpoint"] = config.Llm.Ollama.Endpoint,
            ["llm.ollama.endpointEnvVars"] = FormatEnvironmentVariableNames(config.Llm.Ollama.EndpointEnvironmentVariables),
            ["llm.ollama.model"] = config.Llm.Ollama.Model,
            ["llm.ollama.modelEnvVars"] = FormatEnvironmentVariableNames(config.Llm.Ollama.ModelEnvironmentVariables),
            ["llm.ollama.visionModel"] = config.Llm.Ollama.VisionModel,
            ["llm.ollama.visionModelEnvVars"] = FormatEnvironmentVariableNames(config.Llm.Ollama.VisionModelEnvironmentVariables),
            ["llm.ollama.timeoutSeconds"] = config.Llm.Ollama.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture),
            ["llm.localWhisper.modelPath"] = config.Llm.LocalWhisper.ModelPath,
            ["llm.localWhisper.modelSize"] = config.Llm.LocalWhisper.ModelSize,
            ["llm.localWhisper.language"] = config.Llm.LocalWhisper.Language,
            ["llm.localWhisper.threads"] = config.Llm.LocalWhisper.Threads?.ToString(CultureInfo.InvariantCulture),
            ["llm.localWhisper.runtimeOrder"] = FormatRuntimeOrder(config.Llm.LocalWhisper.RuntimeOrder),
            ["llm.localWhisper.autoDownload"] = config.Llm.LocalWhisper.AutoDownload.ToString(),
            ["captions.languages"] = FormatCaptionLanguages(config.Captions.Languages),
            ["slides.enabled"] = config.Slides.Enabled.ToString(),
            ["slides.hashDistance"] = config.Slides.HashDistance.ToString(CultureInfo.InvariantCulture),
            ["frames.sceneSafetyCap"] = config.Frames.SceneSafetyCap.ToString(CultureInfo.InvariantCulture),
            ["frames.perMinute"] = config.Frames.PerMinute.ToString(CultureInfo.InvariantCulture),
            ["ocr.provider"] = config.Ocr.Provider,
            ["ocr.local.modelDirectory"] = config.Ocr.Local.ModelDirectory,
            ["ocr.local.detectionModelPath"] = config.Ocr.Local.DetectionModelPath,
            ["ocr.local.classificationModelPath"] = config.Ocr.Local.ClassificationModelPath,
            ["ocr.local.recognitionModelPath"] = config.Ocr.Local.RecognitionModelPath,
            ["ocr.local.dictionaryPath"] = config.Ocr.Local.DictionaryPath,
            ["ocr.local.autoDownload"] = config.Ocr.Local.AutoDownload.ToString(),
            ["ocr.local.languagePack"] = config.Ocr.Local.LanguagePack,
            ["vision.provider"] = config.Vision.Provider,
            ["vision.local.mode"] = config.Vision.Local.Mode,
            ["vision.local.modelDirectory"] = config.Vision.Local.ModelDirectory,
            ["vision.local.clipImageEncoderPath"] = config.Vision.Local.ClipImageEncoderPath,
            ["vision.local.clipTextEncoderPath"] = config.Vision.Local.ClipTextEncoderPath,
            ["vision.local.clipKindEmbeddingsPath"] = config.Vision.Local.ClipKindEmbeddingsPath,
            ["vision.local.florenceVisionEncoderPath"] = config.Vision.Local.FlorenceVisionEncoderPath,
            ["vision.local.florenceEncoderPath"] = config.Vision.Local.FlorenceEncoderPath,
            ["vision.local.florenceDecoderPath"] = config.Vision.Local.FlorenceDecoderPath,
            ["vision.local.florenceEmbedTokensPath"] = config.Vision.Local.FlorenceEmbedTokensPath,
            ["vision.local.florenceVocabPath"] = config.Vision.Local.FlorenceVocabPath,
            ["vision.local.florenceMergesPath"] = config.Vision.Local.FlorenceMergesPath,
            ["vision.local.florenceAddedTokensPath"] = config.Vision.Local.FlorenceAddedTokensPath,
            ["vision.local.florenceMaxTokens"] = config.Vision.Local.FlorenceMaxTokens?.ToString(CultureInfo.InvariantCulture),
            ["vision.local.florenceQuantization"] = config.Vision.Local.FlorenceQuantization,
            ["vision.local.autoDownload"] = config.Vision.Local.AutoDownload.ToString(),
            ["crop.enabled"] = config.Crop.Enabled.ToString(),
            ["crop.profile"] = config.Crop.Profile,
            ["capture.mode"] = config.Capture.Mode,
            ["capture.browser.playButtonSelector"] = config.Capture.Browser.PlayButtonSelector,
            ["capture.browser.videoElementSelector"] = config.Capture.Browser.VideoElementSelector,
            ["capture.browser.seekWaitSeconds"] = config.Capture.Browser.SeekWaitSeconds.ToString(CultureInfo.InvariantCulture),
            ["capture.browser.durationProbeTimeoutSeconds"] = config.Capture.Browser.DurationProbeTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["capture.browser.jpegQuality"] = config.Capture.Browser.JpegQuality.ToString(CultureInfo.InvariantCulture),
            ["capture.browser.captureCaptions"] = config.Capture.Browser.CaptureCaptions.ToString(),
            ["capture.browser.maxCaptionBytes"] = config.Capture.Browser.MaxCaptionBytes.ToString(CultureInfo.InvariantCulture),
            ["capture.browser.edgeUserDataDir"] = config.Capture.Browser.EdgeUserDataDir,
            ["capture.browser.edgeProfileDirectory"] = config.Capture.Browser.EdgeProfileDirectory,
            ["capture.browser.debug"] = config.Capture.Browser.Debug.ToString(),
            ["capture.browser.debugMaxBodyBytes"] = config.Capture.Browser.DebugMaxBodyBytes.ToString(CultureInfo.InvariantCulture),
            ["auth.directory"] = config.Auth.Directory,
            ["auth.staleThresholdMinutes"] = config.Auth.StaleThresholdMinutes.ToString(CultureInfo.InvariantCulture),
            ["diarization.provider"] = config.Diarization.Provider,
            ["diarization.modelDirectory"] = config.Diarization.ModelDirectory,
            ["diarization.segmentationModelPath"] = config.Diarization.SegmentationModelPath,
            ["diarization.embeddingModelPath"] = config.Diarization.EmbeddingModelPath,
            ["diarization.numSpeakers"] = config.Diarization.NumSpeakers?.ToString(CultureInfo.InvariantCulture),
            ["diarization.threshold"] = config.Diarization.Threshold?.ToString(CultureInfo.InvariantCulture),
            ["diarization.minDurationOnSeconds"] = config.Diarization.MinDurationOnSeconds?.ToString(CultureInfo.InvariantCulture),
            ["diarization.minDurationOffSeconds"] = config.Diarization.MinDurationOffSeconds?.ToString(CultureInfo.InvariantCulture),
            ["diarization.threads"] = config.Diarization.Threads?.ToString(CultureInfo.InvariantCulture),
            ["diarization.autoDownload"] = config.Diarization.AutoDownload.ToString()
        };
    }

    private static string NormalizeKey(string key)
    {
        return key.Trim().ToLowerInvariant().Replace('_', '-');
    }

    private static string NormalizeExecutablePath(string value, string executableName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        if (Directory.Exists(fullPath))
        {
            fullPath = Path.Combine(fullPath, executableName);
        }

        return fullPath;
    }

    private static string NormalizeFilePath(string value)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
    }

    private static string NormalizeDirectoryPath(string value)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
    }

    /// <summary>
    /// Variant of <see cref="NormalizeDirectoryPath"/> that PRESERVES environment-variable
    /// references in the stored config value (e.g. <c>%LOCALAPPDATA%\Zakira.Replay\edge-profile</c>
    /// stays literal). Expansion happens at read time via the consumer's own resolver. This
    /// lets the config travel between machines whose username / drive letter differ; callers
    /// are responsible for expanding when they need the absolute path.
    /// </summary>
    private static string NormalizePathPreservingEnvVars(string value, string key)
    {
        var normalized = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ReplayException($"Config key {key} requires a non-empty value.");
        }
        return normalized;
    }

    private static string NormalizeUrl(string value, string key)
    {
        var normalized = NormalizeNonEmpty(value, key);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString().TrimEnd('/')
            : throw new ReplayException($"Config key {key} requires an absolute http(s) URL.");
    }

    private static string NormalizeNonEmpty(string value, string key)
    {
        var normalized = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ReplayException($"Config key {key} requires a non-empty value.")
            : normalized;
    }

    private static List<string> ParseEnvironmentVariableNames(string value, string key)
    {
        var names = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Select(name => name.Trim().Trim('"'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0 || names.Any(name => name.Contains('=', StringComparison.Ordinal)))
        {
            throw new ReplayException($"Config key {key} requires one or more environment variable names separated by commas or semicolons.");
        }

        return names;
    }

    private static List<string> ParseCaptionLanguages(string value, string key)
    {
        var languages = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(language => language.Trim().Trim('"').ToLowerInvariant())
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (languages.Count == 0)
        {
            throw new ReplayException($"Config key {key} requires one or more language codes (or `auto`) separated by commas or semicolons.");
        }

        return languages;
    }

    private static string? FormatCaptionLanguages(IReadOnlyList<string> languages)
    {
        return languages.Count == 0 ? null : string.Join(',', languages);
    }

    private static List<string> ParseRuntimeOrder(string value, string key)
    {
        var runtimes = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(runtime => runtime.Trim().Trim('"').ToLowerInvariant())
            .Where(runtime => !string.IsNullOrWhiteSpace(runtime))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (runtimes.Count == 0)
        {
            throw new ReplayException($"Config key {key} requires one or more runtime names (e.g. `cuda,vulkan,coreml,cpu`) separated by commas or semicolons.");
        }

        return runtimes;
    }

    private static string? FormatRuntimeOrder(IReadOnlyList<string> runtimes)
    {
        return runtimes.Count == 0 ? null : string.Join(',', runtimes);
    }

    private static string? FormatEnvironmentVariableNames(IReadOnlyList<string> names)
    {
        return names.Count == 0 ? null : string.Join(',', names);
    }

    private static int ParsePositiveInt(string value, string key)
    {
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new ReplayException($"Config key {key} requires a positive integer value.");
    }

    private static int ParseHashDistance(string value, string key)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || parsed > 64)
        {
            throw new ReplayException($"Config key {key} requires an integer between 0 and 64 (Hamming distance over a 64-bit hash).");
        }

        return parsed;
    }

    private static int ParseSceneSafetyCap(string value, string key)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            throw new ReplayException($"Config key {key} requires a positive integer (maximum number of scene frames extracted per run).");
        }

        return parsed;
    }

    private static int ParseFramesPerMinute(string value, string key)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ReplayException($"Config key {key} requires a non-negative integer (use 0 to disable duration-aware frame sampling).");
        }

        return parsed;
    }

    private static double ParsePositiveDouble(string value, string key)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            throw new ReplayException($"Config key {key} requires a positive finite number.");
        }

        return parsed;
    }

    private static int ParseJpegQuality(string value, string key)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > 100)
        {
            throw new ReplayException($"Config key {key} requires an integer between 1 and 100 (JPEG quality).");
        }

        return parsed;
    }

    private static bool ParseBool(string value, string key)
    {
        var normalized = value.Trim();
        if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("0", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ReplayException($"Config key {key} requires a boolean value.");
    }
}
