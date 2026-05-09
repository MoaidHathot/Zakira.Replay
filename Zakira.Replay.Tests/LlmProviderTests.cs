using System.Net;
using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class LlmProviderTests
{
    [Fact]
    public async Task OpenAiProviderSendsChatCompletionPayload()
    {
        using var handler = new RecordingHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        using var image = new TestTempDirectory();
        var imagePath = image.GetPath("frame.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3], CancellationToken.None);
        var provider = new OpenAiLlmProvider("test-key", "https://api.test/v1", "gpt-test", httpClientFactory: () => new HttpClient(handler, disposeHandler: false));

        var response = await provider.CompleteAsync(new LlmRequest(
            Prompt: "Describe this frame.",
            AttachmentPaths: [imagePath],
            Model: null,
            SystemMessage: "system"), CancellationToken.None);

        Assert.Equal("ok", response);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("https://api.test/v1/chat/completions", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests.Single().Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Requests.Single().Headers.Authorization?.Parameter);
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("content").GetString());
        var content = messages[1].GetProperty("content");
        Assert.Equal("Describe this frame.", content[0].GetProperty("text").GetString());
        Assert.StartsWith("data:image/jpeg;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureOpenAiProviderSendsDeploymentChatPayload()
    {
        using var handler = new RecordingHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"azure-ok\"}}]}");
        var provider = new AzureOpenAiLlmProvider(
            "https://azure.test",
            "azure-key",
            "deployment-one",
            model: null,
            apiVersion: "2024-10-21",
            httpClientFactory: () => new HttpClient(handler, disposeHandler: false));

        var response = await provider.CompleteAsync(new LlmRequest(
            Prompt: "Summarize.",
            AttachmentPaths: [],
            Model: "deployment-one"), CancellationToken.None);

        Assert.Equal("azure-ok", response);
        Assert.Equal("https://azure.test/openai/deployments/deployment-one/chat/completions?api-version=2024-10-21", handler.Requests.Single().RequestUri!.ToString());
        Assert.True(handler.Requests.Single().Headers.TryGetValues("api-key", out var values));
        Assert.Equal("azure-key", values.Single());
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.False(body.RootElement.TryGetProperty("model", out _));
        Assert.Equal("Summarize.", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AzureOpenAiProviderAcceptsFullChatCompletionsEndpointWithoutDeployment()
    {
        using var handler = new RecordingHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"full-url-ok\"}}]}");
        var provider = new AzureOpenAiLlmProvider(
            "https://azure.test/openai/deployments/from-url/chat/completions?api-version=2024-10-21",
            "azure-key",
            deployment: string.Empty,
            httpClientFactory: () => new HttpClient(handler, disposeHandler: false));

        var response = await provider.CompleteAsync(new LlmRequest("Ping", []), CancellationToken.None);

        Assert.Equal("full-url-ok", response);
        Assert.Equal("https://azure.test/openai/deployments/from-url/chat/completions?api-version=2024-10-21", handler.Requests.Single().RequestUri!.ToString());
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

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler, IDisposable
    {
        private readonly string responseBody;

        public RecordingHttpMessageHandler(string responseBody)
        {
            this.responseBody = responseBody;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
