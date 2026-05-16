using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Zakira.Replay.Core;

/// <summary>
/// LLM provider backed by a local <a href="https://ollama.com">Ollama</a> daemon. Talks to the
/// daemon through OllamaSharp's native <see cref="IChatClient"/> implementation (from
/// <see cref="OllamaSharp.OllamaApiClient"/>), which makes Ollama the reference path for the
/// <see cref="IChatClient"/> surface we expose internally: future providers (Anthropic, Gemini,
/// vLLM, llama-cpp-server, …) can plug in through the same abstraction.
/// </summary>
/// <remarks>
/// Ollama is intentionally chat / vision only — it does not serve audio models, and Zakira.Replay
/// rejects audio attachments with a clear pointer to the local-whisper provider. Vision works
/// out of the box: the provider attaches image bytes as <see cref="DataContent"/> entries on the
/// user message, and the orchestrator can route OCR / vision through Ollama by selecting a
/// vision-capable model (<c>llava</c>, <c>llama3.2-vision</c>, <c>bakllava</c>, …) via
/// <c>llm.ollama.visionModel</c>.
/// </remarks>
public sealed class OllamaLlmProvider : ILlmProvider, IDisposable
{
    /// <summary>Default Ollama daemon endpoint, matching the upstream installer.</summary>
    public const string DefaultEndpoint = "http://localhost:11434";

    /// <summary>Default chat model when none is configured. Small, broadly available, decent for OCR/vision support if the user wants to flip models later.</summary>
    public const string DefaultChatModel = "qwen2.5:7b";

    private readonly Uri endpoint;
    private readonly string chatModel;
    private readonly string? visionModel;
    private readonly Func<HttpClient>? httpClientFactory;
    private readonly TimeSpan defaultTimeout;
    private readonly object initLock = new();

    private HttpClient? ownedHttpClient;
    private OllamaApiClient? client;
    private IChatClient? chatClient;
    private bool disposed;

    public OllamaLlmProvider(
        Uri endpoint,
        string chatModel,
        string? visionModel = null,
        TimeSpan? timeout = null,
        Func<HttpClient>? httpClientFactory = null)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.chatModel = string.IsNullOrWhiteSpace(chatModel)
            ? throw new ReplayException("Ollama provider requires a chat model. Set `llm.ollama.model` (or ZAKIRA_REPLAY_OLLAMA_MODEL).")
            : chatModel;
        this.visionModel = string.IsNullOrWhiteSpace(visionModel) ? null : visionModel;
        this.defaultTimeout = timeout ?? TimeSpan.FromMinutes(5);
        this.httpClientFactory = httpClientFactory;
    }

    public string Name => LlmProviders.Ollama;

    /// <summary>
    /// Return the underlying <see cref="IChatClient"/> implementation. Used by
    /// <see cref="LlmProviderChatClientExtensions.AsChatClient"/> to expose the native Ollama
    /// chat client (with streaming, tool-call support, vision-content blocks) rather than the
    /// generic shim built on top of <see cref="CompleteAsync"/>. Initialises lazily — the first
    /// call constructs the underlying <see cref="OllamaSharp.OllamaApiClient"/>.
    /// </summary>
    public IChatClient GetNativeChatClient() => EnsureChatClient();

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Ollama does not serve audio models. Fail fast so the orchestrator can switch to
        // `local-whisper` or a cloud STT provider instead of silently producing nonsense.
        if (request.AttachmentPaths.Any(OpenAiRequestHelpers.IsAudioAttachment))
        {
            throw new ReplayException("Ollama does not support audio transcription. Use --llm-provider local-whisper for fully-local STT, --llm-provider openai for cloud STT, or omit the flag to fall back to GitHub Copilot.");
        }

        var chat = EnsureChatClient();
        var model = ResolveModel(request);

        var messages = BuildMessages(request);
        var options = new ChatOptions { ModelId = model };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout ?? defaultTimeout);

        try
        {
            var response = await chat.GetResponseAsync(messages, options, cts.Token).ConfigureAwait(false);
            return response.Text?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReplayException($"Ollama request timed out after {(request.Timeout ?? defaultTimeout).TotalSeconds:F0}s. Increase --timeout-seconds, configure a faster Ollama model, or run a larger machine.");
        }
        catch (HttpRequestException ex)
        {
            throw new ReplayException($"Ollama request to {endpoint} failed: {ex.Message}. Is the Ollama daemon running? Start it with `ollama serve`.", ex);
        }
        catch (Exception ex) when (ex is not ReplayException and not OperationCanceledException)
        {
            throw new ReplayException($"Ollama request failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // OllamaApiClient owns the underlying HttpClient only when it created one itself.
        // We dispose what we own (ownedHttpClient when no httpClientFactory was supplied).
        (client as IDisposable)?.Dispose();
        ownedHttpClient?.Dispose();
        client = null;
        chatClient = null;
        ownedHttpClient = null;
    }

    /// <summary>
    /// Translate an <see cref="LlmRequest"/> into an <see cref="IChatClient"/> message list.
    /// Public for unit tests so the conversion contract can be verified without a live daemon.
    /// </summary>
    public static IList<ChatMessage> BuildMessages(LlmRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.SystemMessage!));
        }

        var userContents = new List<AIContent> { new TextContent(request.Prompt) };
        foreach (var attachment in request.AttachmentPaths)
        {
            if (!OpenAiRequestHelpers.IsImageAttachment(attachment))
            {
                throw new ReplayException($"Ollama provider only supports image attachments in this path: {attachment}. Audio attachments must use --llm-provider local-whisper.");
            }

            var bytes = File.ReadAllBytes(attachment);
            userContents.Add(new DataContent(bytes, OpenAiRequestHelpers.GetMimeType(attachment)));
        }

        messages.Add(new ChatMessage(ChatRole.User, userContents));
        return messages;
    }

    /// <summary>
    /// Pick the model to ask Ollama for. Image-bearing requests prefer the configured
    /// <c>visionModel</c> when present; otherwise the caller's <see cref="LlmRequest.Model"/>
    /// (unless it's a sentinel default for some other provider) overrides the configured chat model.
    /// Public for unit-testability.
    /// </summary>
    public string ResolveModel(LlmRequest request)
    {
        var hasImage = request.AttachmentPaths.Any(OpenAiRequestHelpers.IsImageAttachment);
        if (hasImage && !string.IsNullOrWhiteSpace(visionModel))
        {
            return visionModel!;
        }

        if (!string.IsNullOrWhiteSpace(request.Model)
            && request.Model != GitHubCopilotLlmProvider.DefaultModel
            && !LooksLikeWhisperSize(request.Model))
        {
            return request.Model!;
        }

        return chatModel;
    }

    private static bool LooksLikeWhisperSize(string? model)
    {
        // GetDefaultModel for local-whisper returns ggml sizes like "small" — those would
        // accidentally hit the Ollama path if the caller routed `--llm-provider local-whisper`
        // through us by mistake. Defend against that by treating them as "no model specified".
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        return LocalWhisperOptions.SupportedModelSizes.Contains(model, StringComparer.OrdinalIgnoreCase);
    }

    private IChatClient EnsureChatClient()
    {
        if (chatClient is not null)
        {
            return chatClient;
        }

        lock (initLock)
        {
            if (chatClient is not null)
            {
                return chatClient;
            }

            try
            {
                if (httpClientFactory is not null)
                {
                    var http = httpClientFactory();
                    if (http.BaseAddress is null)
                    {
                        http.BaseAddress = endpoint;
                    }

                    client = new OllamaApiClient(http);
                }
                else
                {
                    ownedHttpClient = new HttpClient { BaseAddress = endpoint };
                    // Ollama responses can stream and the inference itself is slow on CPU-only
                    // machines; bump the HttpClient timeout well above the default 100s.
                    ownedHttpClient.Timeout = TimeSpan.FromMinutes(10);
                    client = new OllamaApiClient(ownedHttpClient);
                }

                client.SelectedModel = chatModel;
                chatClient = client; // OllamaApiClient implements IChatClient natively.
                return chatClient;
            }
            catch (Exception ex) when (ex is not ReplayException)
            {
                throw new ReplayException(
                    $"Failed to initialise Ollama client for {endpoint}: {ex.Message}. " +
                    "Verify the daemon is running (`ollama serve`) and the endpoint URL is reachable.",
                    ex);
            }
        }
    }
}
