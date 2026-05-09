using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class ReplayConfig
{
    public DependencyPathConfig Dependencies { get; set; } = new();

    public SearchConfig Search { get; set; } = new();

    public LlmConfig Llm { get; set; } = new();
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
            ["llm.azureOpenAi.apiVersion"] = config.Llm.AzureOpenAi.ApiVersion
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
