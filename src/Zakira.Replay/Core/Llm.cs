using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;

namespace Zakira.Replay.Core;

public interface ILlmProvider
{
    string Name { get; }

    Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

public sealed record LlmRequest(
    string Prompt,
    IReadOnlyList<string> AttachmentPaths,
    string? Model = null,
    string? SystemMessage = null,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null);

public sealed class GitHubCopilotLlmProvider : ILlmProvider
{
    public const string DefaultModel = "gpt-5.4";

    public string Name => LlmProviders.GitHubCopilot;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Cwd = request.WorkingDirectory ?? Environment.CurrentDirectory,
            UseLoggedInUser = true
        });

        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        var model = await SelectModelAsync(client, request.Model ?? DefaultModel, cancellationToken).ConfigureAwait(false);
        var response = new StringBuilder();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = string.IsNullOrWhiteSpace(request.SystemMessage)
                ? null
                : new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = request.SystemMessage
                },
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            Streaming = false
        }, cancellationToken).ConfigureAwait(false);

        using var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    if (!string.IsNullOrWhiteSpace(msg.Data?.Content))
                    {
                        response.Clear();
                        response.Append(msg.Data.Content);
                    }
                    break;
                case SessionErrorEvent err:
                    completion.TrySetException(new ReplayException($"Copilot session error: {err.Data?.Message}"));
                    break;
                case SessionIdleEvent:
                    completion.TrySetResult();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = request.Prompt,
            Attachments = request.AttachmentPaths.Select(path => new UserMessageAttachmentFile
            {
                Path = path,
                DisplayName = Path.GetFileName(path)
            }).Cast<UserMessageAttachment>().ToList()
        }, cancellationToken).ConfigureAwait(false);

        await completion.Task.WaitAsync(request.Timeout ?? TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        await client.StopAsync().ConfigureAwait(false);

        return response.ToString().Trim();
    }

    private static async Task<string> SelectModelAsync(CopilotClient client, string requestedModel, CancellationToken cancellationToken)
    {
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Any(model => model.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)))
            {
                return requestedModel;
            }

            var fallback = models.FirstOrDefault(model => model.Id.Contains("gpt-5", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Capabilities?.Supports?.Vision == true)
                ?? models.FirstOrDefault();

            if (fallback is not null)
            {
                return fallback.Id;
            }
        }
        catch
        {
            // If model listing fails, let session creation surface the model error.
        }

        return requestedModel;
    }
}

public sealed class OpenAiLlmProvider : ILlmProvider
{
    private readonly string apiKey;
    private readonly string baseUrl;
    private readonly string chatModel;
    private readonly string transcriptionModel;
    private readonly Func<HttpClient> httpClientFactory;

    public OpenAiLlmProvider(string apiKey, string? baseUrl = null, string? chatModel = null, string? transcriptionModel = null, Func<HttpClient>? httpClientFactory = null)
    {
        this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ReplayException("OpenAI provider requires OPENAI_API_KEY.") : apiKey;
        this.baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        this.chatModel = string.IsNullOrWhiteSpace(chatModel) ? LlmProviderFactory.DefaultOpenAiModel : chatModel;
        this.transcriptionModel = string.IsNullOrWhiteSpace(transcriptionModel) ? "whisper-1" : transcriptionModel;
        this.httpClientFactory = httpClientFactory ?? (() => new HttpClient());
    }

    public string Name => LlmProviders.OpenAi;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (request.AttachmentPaths.Count == 1 && OpenAiRequestHelpers.IsAudioAttachment(request.AttachmentPaths[0]))
        {
            return await TranscribeAudioAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var client = httpClientFactory();
        client.Timeout = request.Timeout ?? TimeSpan.FromMinutes(3);
        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = OpenAiRequestHelpers.JsonContent(new
        {
            model = string.IsNullOrWhiteSpace(request.Model) || request.Model == GitHubCopilotLlmProvider.DefaultModel ? chatModel : request.Model,
            messages = OpenAiRequestHelpers.BuildMessages(request),
            temperature = 0.2
        });

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReplayException($"OpenAI request failed: {(int)response.StatusCode} {body}");
        }

        return OpenAiRequestHelpers.ExtractChatCompletionText(body, "OpenAI");
    }

    private async Task<string> TranscribeAudioAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory();
        client.Timeout = request.Timeout ?? TimeSpan.FromMinutes(5);
        using var form = new MultipartFormDataContent();
        await using var file = File.OpenRead(request.AttachmentPaths[0]);
        using var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(OpenAiRequestHelpers.GetMimeType(request.AttachmentPaths[0]));
        form.Add(fileContent, "file", Path.GetFileName(request.AttachmentPaths[0]));
        form.Add(new StringContent(transcriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");

        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = form;

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReplayException($"OpenAI transcription request failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? string.Empty : body;
    }
}

public sealed class AzureOpenAiLlmProvider : ILlmProvider
{
    private readonly string endpoint;
    private readonly string apiKey;
    private readonly string deployment;
    private readonly string? model;
    private readonly string apiVersion;
    private readonly string? chatCompletionsUrl;
    private readonly Func<HttpClient> httpClientFactory;

    public AzureOpenAiLlmProvider(string endpoint, string apiKey, string deployment, string? model = null, string? apiVersion = null, Func<HttpClient>? httpClientFactory = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ReplayException("Azure OpenAI provider requires AZURE_OPENAI_ENDPOINT.");
        }

        var normalizedEndpoint = endpoint.TrimEnd('/');
        this.endpoint = normalizedEndpoint;
        this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ReplayException("Azure OpenAI provider requires AZURE_OPENAI_API_KEY.") : apiKey;
        chatCompletionsUrl = IsChatCompletionsEndpoint(normalizedEndpoint) ? EnsureApiVersion(normalizedEndpoint, apiVersion) : null;
        this.deployment = string.IsNullOrWhiteSpace(deployment) && chatCompletionsUrl is null
            ? throw new ReplayException("Azure OpenAI provider requires AZURE_OPENAI_DEPLOYMENT unless the endpoint is a full chat completions URL.")
            : deployment;
        this.model = model;
        this.apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? "2024-10-21" : apiVersion;
        this.httpClientFactory = httpClientFactory ?? (() => new HttpClient());
    }

    public string Name => LlmProviders.AzureOpenAi;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (request.AttachmentPaths.Any(OpenAiRequestHelpers.IsAudioAttachment))
        {
            throw new ReplayException("Azure OpenAI chat provider does not support audio transcription through Zakira.Replay yet. Use captions, sidecars, GitHub Copilot STT, or OpenAI transcription.");
        }

        using var client = httpClientFactory();
        client.Timeout = request.Timeout ?? TimeSpan.FromMinutes(3);
        using var message = new HttpRequestMessage(HttpMethod.Post, chatCompletionsUrl ?? $"{endpoint}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(apiVersion)}");
        message.Headers.Add("api-key", apiKey);
        var effectiveModel = string.IsNullOrWhiteSpace(request.Model) || request.Model == deployment
            ? model
            : request.Model;
        object payload = string.IsNullOrWhiteSpace(effectiveModel)
            ? new
            {
                messages = OpenAiRequestHelpers.BuildMessages(request),
                temperature = 0.2
            }
            : new
            {
                model = effectiveModel,
                messages = OpenAiRequestHelpers.BuildMessages(request),
                temperature = 0.2
            };
        message.Content = OpenAiRequestHelpers.JsonContent(payload);

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReplayException($"Azure OpenAI request failed: {(int)response.StatusCode} {body}");
        }

        return OpenAiRequestHelpers.ExtractChatCompletionText(body, "Azure OpenAI");
    }

    private static bool IsChatCompletionsEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureApiVersion(string endpoint, string? apiVersion)
    {
        if (endpoint.Contains("api-version=", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(apiVersion))
        {
            return endpoint;
        }

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return endpoint + separator + "api-version=" + Uri.EscapeDataString(apiVersion);
    }
}

public static class LlmProviders
{
    public const string GitHubCopilot = "github-copilot";
    public const string OpenAi = "openai";
    public const string AzureOpenAi = "azure-openai";

    /// <summary>
    /// Fully-local Whisper.net speech-to-text. STT-only — does not implement chat / vision /
    /// OCR. <see cref="LlmProviderFactory.Create(string?)"/> throws for this value; callers
    /// resolving the chat LLM in the analysis pipeline use
    /// <see cref="LlmProviderFactory.TryCreate(string?, ReplayConfig)"/> instead, which returns
    /// <c>null</c> here so OCR/vision branches degrade gracefully with their existing
    /// <c>*_NO_LLM_PROVIDER</c> warnings.
    /// </summary>
    public const string LocalWhisper = "local-whisper";
}

public static class LlmProviderFactory
{
    public const string DefaultOpenAiModel = "gpt-4o-mini";

    public static ILlmProvider Create(string? provider)
    {
        var config = new ConfigStore().Load();
        return Create(provider, config);
    }

    public static ILlmProvider Create(string? provider, ReplayConfig config)
    {
        return Normalize(provider ?? GetConfiguredProvider(config)) switch
        {
            LlmProviders.GitHubCopilot => new GitHubCopilotLlmProvider(),
            LlmProviders.OpenAi => new OpenAiLlmProvider(
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.OpenAi.ApiKeyEnvironmentVariables, "OPENAI_API_KEY")) ?? throw new ReplayException($"OpenAI provider requires one of these environment variables: {string.Join(", ", WithDefaults(config.Llm.OpenAi.ApiKeyEnvironmentVariables, "OPENAI_API_KEY"))}."),
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.OpenAi.BaseUrlEnvironmentVariables, "OPENAI_BASE_URL")) ?? config.Llm.OpenAi.BaseUrl,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.OpenAi.ModelEnvironmentVariables, "OPENAI_MODEL")) ?? config.Llm.OpenAi.Model,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.OpenAi.TranscriptionModelEnvironmentVariables, "OPENAI_TRANSCRIPTION_MODEL")) ?? config.Llm.OpenAi.TranscriptionModel),
            LlmProviders.AzureOpenAi => new AzureOpenAiLlmProvider(
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.EndpointEnvironmentVariables, "AZURE_OPENAI_ENDPOINT")) ?? config.Llm.AzureOpenAi.Endpoint ?? string.Empty,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.ApiKeyEnvironmentVariables, "AZURE_OPENAI_API_KEY")) ?? string.Empty,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.DeploymentEnvironmentVariables, "AZURE_OPENAI_DEPLOYMENT")) ?? config.Llm.AzureOpenAi.Deployment ?? string.Empty,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.ModelEnvironmentVariables, "AZURE_OPENAI_MODEL")) ?? config.Llm.AzureOpenAi.Model,
                GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.ApiVersionEnvironmentVariables, "AZURE_OPENAI_API_VERSION")) ?? config.Llm.AzureOpenAi.ApiVersion),
            LlmProviders.LocalWhisper => throw new ReplayException("Provider `local-whisper` is speech-to-text only (no chat/vision/OCR). For chat tasks, pass --llm-provider github-copilot|openai|azure-openai or omit the flag."),
            var value => throw new ReplayException($"Unknown LLM provider: {value}")
        };
    }

    /// <summary>
    /// Like <see cref="Create(string?, ReplayConfig)"/> but returns <c>null</c> instead of
    /// throwing when the requested provider is STT-only (currently
    /// <see cref="LlmProviders.LocalWhisper"/>). Used by the analysis pipeline so the chat-LLM
    /// resolution path gracefully reports <c>STT_NO_LLM_PROVIDER</c> / <c>OCR_NO_LLM_PROVIDER</c>
    /// / <c>VISION_NO_LLM_PROVIDER</c> warnings instead of crashing the whole run.
    /// </summary>
    public static ILlmProvider? TryCreate(string? provider, ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        var normalized = Normalize(provider ?? GetConfiguredProvider(config));
        if (normalized == LlmProviders.LocalWhisper)
        {
            return null;
        }

        try
        {
            return Create(normalized, config);
        }
        catch (ReplayException)
        {
            // Unknown / misconfigured providers (e.g. OpenAI without an API key) should also
            // surface as "no chat LLM available" rather than a hard crash. Callers emit a
            // structured *_NO_LLM_PROVIDER warning instead.
            return null;
        }
    }

    public static string GetConfiguredProvider(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return Normalize(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_LLM_PROVIDER") ?? config.Llm.Provider);
    }

    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return LlmProviders.GitHubCopilot;
        }

        return provider.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "copilot" or "github" or "github-copilot" => LlmProviders.GitHubCopilot,
            "openai" => LlmProviders.OpenAi,
            "azure" or "azure-openai" or "azureopenai" => LlmProviders.AzureOpenAi,
            "local-whisper" or "localwhisper" or "whisper" or "local-stt" or "localstt" => LlmProviders.LocalWhisper,
            var value => value
        };
    }

    public static string GetDefaultModel(string? provider, ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return Normalize(provider ?? GetConfiguredProvider(config)) switch
        {
            LlmProviders.OpenAi => GetFirstEnvironmentVariable(WithDefaults(config.Llm.OpenAi.ModelEnvironmentVariables, "OPENAI_MODEL")) ?? config.Llm.OpenAi.Model ?? DefaultOpenAiModel,
            LlmProviders.AzureOpenAi => GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.ModelEnvironmentVariables, "AZURE_OPENAI_MODEL")) ?? config.Llm.AzureOpenAi.Model ?? GetFirstEnvironmentVariable(WithDefaults(config.Llm.AzureOpenAi.DeploymentEnvironmentVariables, "AZURE_OPENAI_DEPLOYMENT")) ?? config.Llm.AzureOpenAi.Deployment ?? string.Empty,
            LlmProviders.LocalWhisper => LocalWhisperOptions.NormalizeModelSize(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_MODEL_SIZE") ?? config.Llm.LocalWhisper.ModelSize),
            _ => GitHubCopilotLlmProvider.DefaultModel
        };
    }

    private static string? GetFirstEnvironmentVariable(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> WithDefaults(IReadOnlyList<string> configuredNames, params string[] defaults)
    {
        return configuredNames.Count == 0
            ? defaults
            : configuredNames.Concat(defaults).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal static class OpenAiRequestHelpers
{
    public static StringContent JsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
    }

    public static object[] BuildMessages(LlmRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            messages.Add(new { role = "system", content = request.SystemMessage });
        }

        var content = new List<object> { new { type = "text", text = request.Prompt } };
        foreach (var attachment in request.AttachmentPaths)
        {
            if (!IsImageAttachment(attachment))
            {
                throw new ReplayException($"OpenAI-compatible chat providers only support image attachments in this path: {attachment}");
            }

            content.Add(new
            {
                type = "image_url",
                image_url = new { url = CreateDataUrl(attachment) }
            });
        }

        messages.Add(new { role = "user", content });
        return messages.ToArray();
    }

    public static string ExtractChatCompletionText(string body, string providerName)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            return content.GetString()?.Trim() ?? string.Empty;
        }

        throw new ReplayException($"{providerName} response did not contain choices[0].message.content.");
    }

    public static bool IsImageAttachment(string path)
    {
        return GetMimeType(path).StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAudioAttachment(string path)
    {
        return GetMimeType(path).StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }

    private static string CreateDataUrl(string path)
    {
        return $"data:{GetMimeType(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }
}
