using Microsoft.Extensions.AI;

namespace Zakira.Replay.Core;

/// <summary>
/// Forward-looking bridge from Zakira.Replay's <see cref="ILlmProvider"/> abstraction to
/// <see cref="IChatClient"/> from <c>Microsoft.Extensions.AI.Abstractions</c>. Lets agent code
/// and external orchestrators consume any configured provider through the .NET-standard chat
/// abstraction without depending on Zakira.Replay's internal interfaces.
/// </summary>
/// <remarks>
/// <para>
/// Native <see cref="IChatClient"/> implementations (currently only <see cref="OllamaLlmProvider"/>,
/// which is backed by <c>OllamaSharp.OllamaApiClient</c>) are exposed verbatim — they implement
/// streaming, tool calls, and the full <see cref="ChatOptions"/> surface natively. All other
/// providers are wrapped in a thin shim that translates <see cref="ChatMessage"/> sequences into
/// <see cref="LlmRequest"/> calls; image attachments are persisted to temp files for the wrapped
/// provider, and audio content blocks are rejected with a clear error.
/// </para>
/// <para>
/// The shim is best-effort: it does not implement true token streaming for non-streaming
/// providers (it emits a single <see cref="ChatResponseUpdate"/> with the full response when
/// <see cref="IChatClient.GetStreamingResponseAsync"/> is called). This matches the current
/// <see cref="ILlmProvider.CompleteAsync"/> non-streaming contract and is sufficient for the
/// pipelines that currently consume <see cref="ILlmProvider"/>.
/// </para>
/// </remarks>
public static class LlmProviderChatClientExtensions
{
    /// <summary>
    /// Return an <see cref="IChatClient"/> view of any <see cref="ILlmProvider"/>. The returned
    /// client may be the provider itself (when it implements <see cref="IChatClient"/> natively)
    /// or a thin shim. Callers must dispose the returned client.
    /// </summary>
    public static IChatClient AsChatClient(this ILlmProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider switch
        {
            OllamaLlmProvider ollama => ollama.GetNativeChatClient(),
            _ => new LlmProviderChatClientAdapter(provider)
        };
    }
}

/// <summary>
/// Adapter that exposes any <see cref="ILlmProvider"/> as an <see cref="IChatClient"/>. Used
/// for providers that don't implement <see cref="IChatClient"/> natively (currently
/// GitHub Copilot, OpenAI, Azure OpenAI). Conversion is best-effort and stateless.
/// </summary>
internal sealed class LlmProviderChatClientAdapter : IChatClient
{
    private static readonly ChatClientMetadata SharedMetadata = new(providerName: "zakira-replay-llm-provider-adapter");

    private readonly ILlmProvider provider;
    private readonly List<string> tempFiles = [];
    private bool disposed;

    public LlmProviderChatClientAdapter(ILlmProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        var request = await BuildRequestAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var text = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        var assistantMessage = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(assistantMessage)
        {
            ModelId = options?.ModelId ?? request.Model,
            FinishReason = ChatFinishReason.Stop
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The wrapped ILlmProvider is non-streaming; surface the full response as a single
        // ChatResponseUpdate. Real streaming should use providers that implement IChatClient
        // natively (today: OllamaLlmProvider).
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
        {
            ModelId = response.ModelId,
            FinishReason = response.FinishReason
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return SharedMetadata;
            }
            if (serviceType == typeof(ILlmProvider))
            {
                return provider;
            }
            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var path in tempFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup; ignore.
            }
        }

        tempFiles.Clear();
    }

    private async Task<LlmRequest> BuildRequestAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var systemBuilder = new System.Text.StringBuilder();
        var promptBuilder = new System.Text.StringBuilder();
        var attachments = new List<string>();

        foreach (var message in messages)
        {
            if (message is null)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        if (message.Role == ChatRole.System)
                        {
                            AppendWithSeparator(systemBuilder, text.Text);
                        }
                        else
                        {
                            AppendWithSeparator(promptBuilder, FormatRolePrefix(message.Role, text.Text));
                        }
                        break;

                    case DataContent data:
                        if (string.IsNullOrWhiteSpace(data.MediaType) || data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ReplayException("Audio content blocks are not supported by the LLM provider adapter. Use --llm-provider local-whisper for STT, or attach audio directly to an ILlmProvider that supports transcription (openai).");
                        }

                        var tempPath = await PersistAttachmentAsync(data, cancellationToken).ConfigureAwait(false);
                        attachments.Add(tempPath);
                        break;

                    case UriContent uri:
                        // URI references aren't natively supported by ILlmProvider.CompleteAsync;
                        // surface the URL in the prompt so the model still sees the context.
                        AppendWithSeparator(promptBuilder, $"[image: {uri.Uri}]");
                        break;
                }
            }
        }

        if (promptBuilder.Length == 0)
        {
            throw new ReplayException("ChatMessage sequence contained no user-visible text content.");
        }

        return new LlmRequest(
            Prompt: promptBuilder.ToString().TrimEnd(),
            AttachmentPaths: attachments,
            Model: options?.ModelId,
            SystemMessage: systemBuilder.Length == 0 ? null : systemBuilder.ToString().TrimEnd());
    }

    private async Task<string> PersistAttachmentAsync(DataContent data, CancellationToken cancellationToken)
    {
        var extension = GuessExtension(data.MediaType);
        var path = Path.Combine(Path.GetTempPath(), $"zakira-replay-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, data.Data.ToArray(), cancellationToken).ConfigureAwait(false);
        tempFiles.Add(path);
        return path;
    }

    private static string GuessExtension(string mediaType)
    {
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".bin"
        };
    }

    private static string FormatRolePrefix(ChatRole role, string text)
    {
        if (role == ChatRole.User || role == ChatRole.Assistant)
        {
            return text;
        }

        // Tool / other roles get a small prefix so the wrapped provider sees the context
        // without losing the role distinction.
        return $"[{role}] {text}";
    }

    private static void AppendWithSeparator(System.Text.StringBuilder builder, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(text);
    }
}
