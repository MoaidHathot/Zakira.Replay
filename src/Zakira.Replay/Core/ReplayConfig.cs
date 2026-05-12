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

    public CaptionsConfig Captions { get; set; } = new();

    public SlidesConfig Slides { get; set; } = new();

    public FramesConfig Frames { get; set; } = new();

    public CropConfig Crop { get; set; } = new();

    public CaptureConfig Capture { get; set; } = new();

    public AuthConfig Auth { get; set; } = new();
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
    /// Directory containing the four RapidOCR (PP-OCRv5 latin) model files:
    /// <c>ch_PP-OCRv5_det_mobile.onnx</c>, <c>ch_ppocr_mobile_v2.0_cls_mobile.onnx</c>,
    /// <c>latin_PP-OCRv5_rec_mobile.onnx</c>, and <c>ppocrv5_latin_dict.txt</c>.
    /// Resolved against the portable directory when null.
    /// </summary>
    public string? ModelDirectory { get; set; }

    public string? DetectionModelPath { get; set; }

    public string? ClassificationModelPath { get; set; }

    public string? RecognitionModelPath { get; set; }

    public string? DictionaryPath { get; set; }

    /// <summary>
    /// When true (default), <see cref="LocalOnnxOcrProvider"/> initialisation may invoke
    /// <see cref="PortableDependencyInstaller.InstallAsync"/> to fetch the RapidOCR
    /// models on first use. Install ahead-of-time with <c>deps install ocr</c> to skip the
    /// network round-trip; set false to disable on-demand downloads entirely.
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
}

public sealed class LlmConfig
{
    public string? Provider { get; set; } = LlmProviders.GitHubCopilot;

    public OpenAiConfig OpenAi { get; set; } = new();

    public AzureOpenAiConfig AzureOpenAi { get; set; } = new();
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
                    ModelDirectory = PortableDependencyInstaller.GetDefaultOnnxModelDirectory(),
                    ModelFile = PortableDependencyInstaller.DefaultOnnxModelFile
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
                }
            },
            Ocr = new OcrConfig
            {
                Provider = OcrProviders.Local,
                Local = new LocalOcrConfig
                {
                    AutoDownload = true,
                    ModelDirectory = PortableDependencyInstaller.GetDefaultOcrModelDirectory()
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
            case "auth.directory":
                config.Auth.Directory = NormalizeDirectoryPath(value);
                break;
            case "auth.stalethresholdminutes":
            case "auth.stale-threshold-minutes":
                config.Auth.StaleThresholdMinutes = ParsePositiveInt(value, key);
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
            "auth.directory" => config.Auth.Directory,
            "auth.stalethresholdminutes" or "auth.stale-threshold-minutes" => config.Auth.StaleThresholdMinutes.ToString(CultureInfo.InvariantCulture),
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
            ["auth.directory"] = config.Auth.Directory,
            ["auth.staleThresholdMinutes"] = config.Auth.StaleThresholdMinutes.ToString(CultureInfo.InvariantCulture)
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
