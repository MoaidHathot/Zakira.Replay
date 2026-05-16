using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class LlmProviderTests
{
    [Fact]
    public async Task OpenAiProviderSendsChatRequestThroughIChatClient()
    {
        // Phase 4: OpenAi/Azure providers go through Microsoft.Extensions.AI.IChatClient
        // internally. We mock at the IChatClient layer rather than at the HTTP wire shape:
        // the OpenAI SDK is responsible for the wire, and the SDK is well-tested upstream.
        using var image = new TestTempDirectory();
        var imagePath = image.GetPath("frame.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3], CancellationToken.None);

        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new OpenAiLlmProvider(fake);

        var response = await provider.CompleteAsync(new LlmRequest(
            Prompt: "Describe this frame.",
            AttachmentPaths: [imagePath],
            Model: null,
            SystemMessage: "system"), CancellationToken.None);

        Assert.Equal("ok", response);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(2, call.Messages.Count);
        Assert.Equal(ChatRole.System, call.Messages[0].Role);
        Assert.Equal("system", call.Messages[0].Text);
        Assert.Equal(ChatRole.User, call.Messages[1].Role);

        // User content is [TextContent("Describe this frame."), DataContent(image-bytes, image/jpeg)].
        Assert.Equal(2, call.Messages[1].Contents.Count);
        var text = Assert.IsType<TextContent>(call.Messages[1].Contents[0]);
        Assert.Equal("Describe this frame.", text.Text);
        var data = Assert.IsType<DataContent>(call.Messages[1].Contents[1]);
        Assert.Equal("image/jpeg", data.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Data.ToArray());
    }

    [Fact]
    public async Task OpenAiProviderForwardsRequestModelToChatOptions()
    {
        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new OpenAiLlmProvider(fake);

        await provider.CompleteAsync(new LlmRequest(
            Prompt: "Hi",
            AttachmentPaths: [],
            Model: "gpt-4o-mini-2024-07-18"), CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("gpt-4o-mini-2024-07-18", call.Options?.ModelId);
        Assert.Equal(0.2f, call.Options?.Temperature);
    }

    [Fact]
    public async Task OpenAiProviderIgnoresCopilotSentinelModelString()
    {
        // When the analysis pipeline forwards GitHubCopilotLlmProvider.DefaultModel as the model
        // (the Copilot sentinel) into the OpenAI provider, we must NOT pass that value as the
        // OpenAI model id — it would 404 the API. The provider's configured chatModel wins.
        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new OpenAiLlmProvider(fake);

        await provider.CompleteAsync(new LlmRequest(
            Prompt: "Hi",
            AttachmentPaths: [],
            Model: GitHubCopilotLlmProvider.DefaultModel), CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(LlmProviderFactory.DefaultOpenAiModel, call.Options?.ModelId);
    }

    [Fact]
    public async Task AzureOpenAiProviderSendsChatRequestThroughIChatClient()
    {
        using var fake = new RecordingChatClient(static _ => "azure-ok");
        using var provider = new AzureOpenAiLlmProvider(fake, deployment: "deployment-one");

        var response = await provider.CompleteAsync(new LlmRequest(
            Prompt: "Summarize.",
            AttachmentPaths: [],
            Model: "deployment-one"), CancellationToken.None);

        Assert.Equal("azure-ok", response);
        var call = Assert.Single(fake.Calls);
        // When request.Model equals the deployment, prefer the configured `model` (null) — fall
        // through to the deployment id since model is null. This preserves the legacy contract.
        Assert.Equal("deployment-one", call.Options?.ModelId);
        Assert.Single(call.Messages);
        Assert.Equal(ChatRole.User, call.Messages[0].Role);
        var text = Assert.IsType<TextContent>(call.Messages[0].Contents[0]);
        Assert.Equal("Summarize.", text.Text);
    }

    [Fact]
    public async Task AzureOpenAiProviderUsesConfiguredModelOverDeploymentSentinel()
    {
        // Azure deployments are typically distinct from openai model ids; the provider lets
        // users pin a specific model (e.g. "gpt-4o-2024-08-06") through the ctor `model` arg.
        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new AzureOpenAiLlmProvider(fake, deployment: "deployment-one", model: "gpt-4o-2024-08-06");

        await provider.CompleteAsync(new LlmRequest(
            Prompt: "Hi",
            AttachmentPaths: [],
            Model: "deployment-one"), CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("gpt-4o-2024-08-06", call.Options?.ModelId);
    }

    [Fact]
    public async Task AzureOpenAiProviderRejectsAudioAttachmentsWithGuidance()
    {
        using var temp = new TestTempDirectory();
        var audio = temp.GetPath("clip.wav");
        await File.WriteAllBytesAsync(audio, [0, 0], CancellationToken.None);

        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new AzureOpenAiLlmProvider(fake, deployment: "deployment-one");

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.CompleteAsync(new LlmRequest("Transcribe", [audio]), CancellationToken.None));

        Assert.Contains("audio transcription", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task OpenAiProviderRejectsNonImageAttachments()
    {
        using var temp = new TestTempDirectory();
        var binary = temp.GetPath("doc.pdf");
        await File.WriteAllBytesAsync(binary, [1, 2, 3], CancellationToken.None);

        using var fake = new RecordingChatClient(static _ => "ok");
        using var provider = new OpenAiLlmProvider(fake);

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.CompleteAsync(new LlmRequest("describe", [binary]), CancellationToken.None));

        Assert.Contains("image attachments", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void LlmProviderFactoryReadsSecretFromConfiguredOpenAiEnvironmentVariableName()
    {
        var previousCustomKey = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_TEST_OPENAI_KEY");
        var previousOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_OPENAI_KEY", "custom-openai-key");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            var provider = LlmProviderFactory.Create("openai", new ReplayConfig
            {
                Llm = new LlmConfig
                {
                    OpenAi = new OpenAiConfig
                    {
                        ApiKeyEnvironmentVariables = ["ZAKIRA_REPLAY_TEST_OPENAI_KEY"]
                    }
                }
            });

            Assert.Equal(LlmProviders.OpenAi, provider.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_OPENAI_KEY", previousCustomKey);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiKey);
        }
    }

    [Fact]
    public void LlmProviderFactoryReadsAzureValuesFromConfiguredEnvironmentVariableNames()
    {
        var previousEndpoint = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_ENDPOINT");
        var previousKey = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_KEY");
        var previousDeployment = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_DEPLOYMENT");
        var previousStandardEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var previousStandardKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var previousStandardDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_ENDPOINT", "https://configured.test");
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_KEY", "configured-key");
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_DEPLOYMENT", "configured-deployment");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);

            var provider = LlmProviderFactory.Create("azure-openai", new ReplayConfig
            {
                Llm = new LlmConfig
                {
                    AzureOpenAi = new AzureOpenAiConfig
                    {
                        EndpointEnvironmentVariables = ["ZAKIRA_REPLAY_TEST_AZURE_ENDPOINT"],
                        ApiKeyEnvironmentVariables = ["ZAKIRA_REPLAY_TEST_AZURE_KEY"],
                        DeploymentEnvironmentVariables = ["ZAKIRA_REPLAY_TEST_AZURE_DEPLOYMENT"]
                    }
                }
            });

            Assert.Equal(LlmProviders.AzureOpenAi, provider.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_ENDPOINT", previousEndpoint);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_KEY", previousKey);
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_TEST_AZURE_DEPLOYMENT", previousDeployment);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", previousStandardEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", previousStandardKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", previousStandardDeployment);
        }
    }

    /// <summary>
    /// Test double for <see cref="IChatClient"/>. Records every call so tests can assert on the
    /// translated <see cref="ChatMessage"/> sequence and the <see cref="ChatOptions"/> the
    /// provider built, and returns a canned response. Streaming is unsupported (the
    /// production providers go through <c>GetResponseAsync</c>, not the streaming variant).
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<RecordedCall, string> responder;

        public RecordingChatClient(Func<RecordedCall, string> responder)
        {
            this.responder = responder;
        }

        public List<RecordedCall> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = new RecordedCall(messages.ToList(), options);
            Calls.Add(call);
            var text = responder(call);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                ModelId = options?.ModelId,
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("RecordingChatClient does not implement streaming.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed record RecordedCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
}
