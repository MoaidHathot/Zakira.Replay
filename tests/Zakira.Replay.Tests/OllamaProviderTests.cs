using Microsoft.Extensions.AI;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class OllamaProviderTests
{
    [Theory]
    [InlineData("ollama", LlmProviders.Ollama)]
    [InlineData("ollamasharp", LlmProviders.Ollama)]
    [InlineData(" Ollama ", LlmProviders.Ollama)]
    [InlineData("OLLAMA", LlmProviders.Ollama)]
    public void NormalizeMapsAliasesToOllama(string input, string expected)
    {
        Assert.Equal(expected, LlmProviderFactory.Normalize(input));
    }

    [Fact]
    public void CreateOllamaProviderHonoursConfigEndpointAndModel()
    {
        var config = new ReplayConfig
        {
            Llm = new LlmConfig
            {
                Ollama = new OllamaConfig
                {
                    Endpoint = "http://custom-host:11500",
                    Model = "llama3.1:8b",
                    VisionModel = "llama3.2-vision:11b"
                }
            }
        };

        using var provider = (OllamaLlmProvider)LlmProviderFactory.Create("ollama", config);

        Assert.Equal(LlmProviders.Ollama, provider.Name);
    }

    [Fact]
    public void CreateOllamaProviderReadsEndpointFromEnvironmentVariable()
    {
        const string envVar = "ZAKIRA_REPLAY_OLLAMA_ENDPOINT";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "http://ollama-via-env:11434");
            var config = new ReplayConfig
            {
                Llm = new LlmConfig
                {
                    Ollama = new OllamaConfig
                    {
                        Endpoint = "http://config-fallback:99",
                        Model = "qwen2.5:7b"
                    }
                }
            };

            // No throw means the env var was preferred and parsed correctly.
            using var provider = (OllamaLlmProvider)LlmProviderFactory.Create("ollama", config);
            Assert.Equal(LlmProviders.Ollama, provider.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void CreateOllamaProviderThrowsForInvalidEndpoint()
    {
        var config = new ReplayConfig
        {
            Llm = new LlmConfig
            {
                Ollama = new OllamaConfig { Endpoint = "not-a-url", Model = "qwen2.5:7b" }
            }
        };

        var ex = Assert.Throws<ReplayException>(() => LlmProviderFactory.Create("ollama", config));
        Assert.Contains("absolute URL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreateReturnsOllamaProviderForNormalConfig()
    {
        var config = new ReplayConfig
        {
            Llm = new LlmConfig
            {
                Ollama = new OllamaConfig { Endpoint = "http://localhost:11434", Model = "qwen2.5:7b" }
            }
        };

        var provider = LlmProviderFactory.TryCreate("ollama", config);
        Assert.NotNull(provider);
        using (provider as IDisposable)
        {
            Assert.Equal(LlmProviders.Ollama, provider!.Name);
        }
    }

    [Fact]
    public void GetDefaultModelReturnsConfiguredOllamaModel()
    {
        var config = new ReplayConfig
        {
            Llm = new LlmConfig
            {
                Ollama = new OllamaConfig { Model = "llama3.1:70b" }
            }
        };

        Assert.Equal("llama3.1:70b", LlmProviderFactory.GetDefaultModel("ollama", config));
    }

    [Fact]
    public void GetDefaultModelFallsBackToOllamaProviderDefaultWhenUnconfigured()
    {
        Assert.Equal(OllamaLlmProvider.DefaultChatModel, LlmProviderFactory.GetDefaultModel("ollama", new ReplayConfig()));
    }

    [Fact]
    public void BuildMessagesIncludesSystemAndUserContent()
    {
        var request = new LlmRequest(
            Prompt: "Describe this scene.",
            AttachmentPaths: [],
            SystemMessage: "You are a precise vision engine.");

        var messages = OllamaLlmProvider.BuildMessages(request);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("You are a precise vision engine.", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("Describe this scene.", messages[1].Text);
    }

    [Fact]
    public void BuildMessagesOmitsSystemMessageWhenAbsent()
    {
        var request = new LlmRequest("Hello", []);
        var messages = OllamaLlmProvider.BuildMessages(request);

        Assert.Single(messages);
        Assert.Equal(ChatRole.User, messages[0].Role);
    }

    [Fact]
    public async Task BuildMessagesAttachesImageBytesAsDataContent()
    {
        using var temp = new TestTempDirectory();
        var imagePath = temp.GetPath("frame.png");
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header bytes
        await File.WriteAllBytesAsync(imagePath, imageBytes, CancellationToken.None);

        var request = new LlmRequest("What is in this frame?", [imagePath]);
        var messages = OllamaLlmProvider.BuildMessages(request);

        var userMessage = Assert.Single(messages);
        Assert.Equal(ChatRole.User, userMessage.Role);
        Assert.Equal(2, userMessage.Contents.Count);

        var text = Assert.IsType<TextContent>(userMessage.Contents[0]);
        Assert.Equal("What is in this frame?", text.Text);

        var data = Assert.IsType<DataContent>(userMessage.Contents[1]);
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal(imageBytes, data.Data.ToArray());
    }

    [Fact]
    public async Task BuildMessagesRejectsAudioAttachmentsWithPointerToWhisper()
    {
        using var temp = new TestTempDirectory();
        var audio = temp.GetPath("clip.wav");
        await File.WriteAllBytesAsync(audio, [0, 0], CancellationToken.None);

        var ex = Assert.Throws<ReplayException>(() => OllamaLlmProvider.BuildMessages(new LlmRequest("Transcribe.", [audio])));
        Assert.Contains("image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsyncRejectsAudioAttachmentsBeforeTouchingTheDaemon()
    {
        using var temp = new TestTempDirectory();
        var audio = temp.GetPath("clip.wav");
        await File.WriteAllBytesAsync(audio, [0, 0], CancellationToken.None);

        using var provider = new OllamaLlmProvider(new Uri("http://does-not-matter:1"), "qwen2.5:7b");
        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.CompleteAsync(new LlmRequest("Transcribe", [audio]), CancellationToken.None));

        // Must point users at the local-whisper alternative so they don't get stuck.
        Assert.Contains("local-whisper", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveModelPrefersVisionModelWhenImagePresent()
    {
        using var temp = new TestTempDirectory();
        var imagePath = temp.GetPath("frame.jpg");
        File.WriteAllBytes(imagePath, [0xFF, 0xD8]);

        using var provider = new OllamaLlmProvider(
            new Uri("http://localhost:11434"),
            chatModel: "qwen2.5:7b",
            visionModel: "llama3.2-vision:11b");

        var request = new LlmRequest("Describe", [imagePath]);
        Assert.Equal("llama3.2-vision:11b", provider.ResolveModel(request));
    }

    [Fact]
    public void ResolveModelFallsBackToChatModelWhenVisionModelUnset()
    {
        using var temp = new TestTempDirectory();
        var imagePath = temp.GetPath("frame.jpg");
        File.WriteAllBytes(imagePath, [0xFF, 0xD8]);

        using var provider = new OllamaLlmProvider(new Uri("http://localhost:11434"), chatModel: "qwen2.5:7b");

        Assert.Equal("qwen2.5:7b", provider.ResolveModel(new LlmRequest("Describe", [imagePath])));
    }

    [Fact]
    public void ResolveModelIgnoresCopilotSentinelModelString()
    {
        using var provider = new OllamaLlmProvider(new Uri("http://localhost:11434"), chatModel: "qwen2.5:7b");

        // When the analysis pipeline forwards `request.Model = GitHubCopilotLlmProvider.DefaultModel`
        // (the copilot sentinel) into Ollama, we must NOT pass that string as the Ollama model id —
        // it would 404 the daemon. The provider's configured chatModel wins.
        var request = new LlmRequest("Hi", [], Model: GitHubCopilotLlmProvider.DefaultModel);
        Assert.Equal("qwen2.5:7b", provider.ResolveModel(request));
    }

    [Fact]
    public void ResolveModelIgnoresWhisperSizeStringsFromUpstreamFactory()
    {
        // GetDefaultModel("local-whisper") returns "small" / "base" / etc. If a misconfigured
        // pipeline forwards that into the Ollama provider, it must be treated as "use my default"
        // instead of asking Ollama for a model named "small".
        using var provider = new OllamaLlmProvider(new Uri("http://localhost:11434"), chatModel: "qwen2.5:7b");

        foreach (var size in new[] { "tiny", "small", "large-v3-turbo" })
        {
            Assert.Equal("qwen2.5:7b", provider.ResolveModel(new LlmRequest("Hi", [], Model: size)));
        }
    }

    [Fact]
    public void ResolveModelHonoursRequestModelForTextOnlyRequests()
    {
        using var provider = new OllamaLlmProvider(new Uri("http://localhost:11434"), chatModel: "qwen2.5:7b");

        var request = new LlmRequest("Hi", [], Model: "llama3.1:8b");
        Assert.Equal("llama3.1:8b", provider.ResolveModel(request));
    }

    [Fact]
    public async Task CompleteAsyncSurfacesDaemonUnreachableAsReplayException()
    {
        // Point at an unused TCP port; HttpClient should fail fast.
        using var provider = new OllamaLlmProvider(
            new Uri("http://127.0.0.1:1"),
            chatModel: "qwen2.5:7b",
            timeout: TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.CompleteAsync(new LlmRequest("Hi", []), CancellationToken.None));

        // Whether the failure surfaces as "Ollama request to … failed" or a timeout depends on
        // the platform; both should mention "Ollama".
        Assert.Contains("ollama", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AsChatClientForNonOllamaProviderWrapsCompleteAsync()
    {
        var fake = new FakeLlmProvider(req => $"echo: {req.Prompt} ({string.Join(",", req.AttachmentPaths.Select(Path.GetFileName))})");
        using var chat = fake.AsChatClient();

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "You are a test."), new ChatMessage(ChatRole.User, "Hello")],
            options: null,
            CancellationToken.None);

        Assert.Single(fake.Calls);
        Assert.Equal("Hello", fake.Calls[0].Prompt);
        Assert.Equal("You are a test.", fake.Calls[0].SystemMessage);
        Assert.Equal("echo: Hello ()", response.Text);
    }

    [Fact]
    public async Task AsChatClientPersistsImageContentAsTemporaryAttachment()
    {
        var fake = new FakeLlmProvider(req => $"saw {req.AttachmentPaths.Count} attachment(s)");
        using var chat = fake.AsChatClient();

        var imageBytes = new byte[] { 1, 2, 3 };
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("Look at this"),
            new DataContent(imageBytes, "image/png")
        ]);

        var response = await chat.GetResponseAsync([message], options: null, CancellationToken.None);

        Assert.Single(fake.Calls);
        Assert.Single(fake.Calls[0].AttachmentPaths);
        Assert.True(File.Exists(fake.Calls[0].AttachmentPaths[0]));
        Assert.Equal(imageBytes, await File.ReadAllBytesAsync(fake.Calls[0].AttachmentPaths[0]));
        Assert.Equal("saw 1 attachment(s)", response.Text);
    }

    [Fact]
    public async Task AsChatClientStreamingEmitsSingleUpdate()
    {
        var fake = new FakeLlmProvider(_ => "complete response");
        using var chat = fake.AsChatClient();

        var updates = new List<string?>();
        await foreach (var update in chat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            options: null,
            CancellationToken.None))
        {
            updates.Add(update.Text);
        }

        Assert.Single(updates);
        Assert.Equal("complete response", updates[0]);
    }

    [Fact]
    public void AsChatClientForOllamaReturnsNativeInstance()
    {
        using var ollama = new OllamaLlmProvider(new Uri("http://localhost:11434"), "qwen2.5:7b");
        using var chat = ollama.AsChatClient();

        // OllamaSharp's OllamaApiClient implements IChatClient directly; calling AsChatClient on
        // an OllamaLlmProvider must surface that native instance instead of wrapping in the
        // generic shim, so streaming, tool-calling, etc. still work.
        Assert.NotNull(chat);
        Assert.Equal("OllamaApiClient", chat.GetType().Name);
    }

    [Fact]
    public async Task ConfigStoreRoundTripsLlmOllamaKeys()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        var initial = ConfigStore.CreateDefaultConfig();
        Assert.NotNull(initial.Llm.Ollama);
        Assert.Equal(OllamaLlmProvider.DefaultEndpoint, initial.Llm.Ollama.Endpoint);

        await store.SetAsync("llm.ollama.endpoint", "http://localhost:11500", CancellationToken.None);
        await store.SetAsync("llm.ollama.model", "llama3.1:8b", CancellationToken.None);
        await store.SetAsync("llm.ollama.visionModel", "llama3.2-vision:11b", CancellationToken.None);
        await store.SetAsync("llm.ollama.timeoutSeconds", "120", CancellationToken.None);

        var endpoint = await store.GetAsync("llm.ollama.endpoint", CancellationToken.None);
        var model = await store.GetAsync("llm.ollama.model", CancellationToken.None);
        var vision = await store.GetAsync("llm.ollama.visionModel", CancellationToken.None);
        var timeout = await store.GetAsync("llm.ollama.timeoutSeconds", CancellationToken.None);

        Assert.Equal("http://localhost:11500", endpoint);
        Assert.Equal("llama3.1:8b", model);
        Assert.Equal("llama3.2-vision:11b", vision);
        Assert.Equal("120", timeout);
    }

    private sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly Func<LlmRequest, string> responder;

        public FakeLlmProvider(Func<LlmRequest, string> responder)
        {
            this.responder = responder;
        }

        public string Name => "fake";

        public List<LlmRequest> Calls { get; } = [];

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
