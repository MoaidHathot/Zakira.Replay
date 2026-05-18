using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Tests;

/// <summary>
/// Exercises the 0.9.0 MCP surface (verb.noun tool names + replay:// resources) via the
/// official ModelContextProtocol SDK's in-memory pipe transport. The hand-rolled JSON-RPC
/// server from 0.8.x has been retired; these tests therefore probe the contract through
/// a real <see cref="McpClient"/> instead of the deleted internal API.
/// </summary>
public sealed class McpTests
{
    [Fact]
    public async Task ToolsListIncludesVerbDotNounAnalysisTools()
    {
        await using var harness = await McpTestHarness.CreateAsync();

        var tools = await harness.Client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).ToArray();

        Assert.Contains("analyze", names);
        Assert.Contains("analyze.start", names);
        Assert.Contains("analyze.status", names);
        Assert.Contains("analyze.result", names);
        Assert.Contains("analyze.cancel", names);
        Assert.Contains("queue.enqueue", names);
        Assert.Contains("queue.run", names);
        Assert.Contains("queue.status", names);
        Assert.Contains("frames", names);
        Assert.Contains("clip", names);
        Assert.Contains("index.build", names);
        Assert.Contains("index.query", names);
        Assert.Contains("chapters.build", names);
        Assert.Contains("align", names);
        Assert.Contains("discover", names);
        Assert.Contains("doctor", names);
    }

    [Fact]
    public async Task FramesToolDeclaresMutuallyExclusiveAtAndWindowInputs()
    {
        await using var harness = await McpTestHarness.CreateAsync();

        var tools = await harness.Client.ListToolsAsync();
        var frames = tools.Single(tool => tool.Name == "frames");

        var json = JsonSerializer.Serialize(frames.JsonSchema);
        var schema = JsonDocument.Parse(json).RootElement;
        var properties = FindToolProperties(schema);
        Assert.True(properties.TryGetProperty("at", out _), json);
        Assert.True(properties.TryGetProperty("from", out _), json);
        Assert.True(properties.TryGetProperty("to", out _), json);
        Assert.True(properties.TryGetProperty("count", out _), json);
        Assert.True(properties.TryGetProperty("strategy", out _), json);
        Assert.True(properties.TryGetProperty("maxLongEdgePixels", out _), json);
        Assert.True(properties.TryGetProperty("jpegQuality", out _), json);
        Assert.True(properties.TryGetProperty("computePerceptualHash", out _), json);
    }

    [Fact]
    public async Task AnalyzeStartToolDeclaresProviderInputs()
    {
        await using var harness = await McpTestHarness.CreateAsync();

        var tools = await harness.Client.ListToolsAsync();
        var analyze = tools.Single(tool => tool.Name == "analyze.start");

        var json = JsonSerializer.Serialize(analyze.JsonSchema);
        var schema = JsonDocument.Parse(json).RootElement;
        var properties = FindToolProperties(schema);
        Assert.True(properties.TryGetProperty("source", out _), json);
        Assert.True(properties.TryGetProperty("llmProvider", out _), json);
        Assert.True(properties.TryGetProperty("everyFrame", out _), json);
    }

    /// <summary>
    /// Returns the leaf <c>properties</c> object regardless of whether the SDK flattened a
    /// record-typed parameter into top-level properties or nested it under the parameter name.
    /// </summary>
    private static JsonElement FindToolProperties(JsonElement schema)
    {
        var topProperties = schema.GetProperty("properties");
        // If there's exactly one property of type "object", peek inside.
        var entries = topProperties.EnumerateObject().ToArray();
        if (entries.Length == 1 && entries[0].Value.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "object" && entries[0].Value.TryGetProperty("properties", out var nested))
        {
            return nested;
        }
        return topProperties;
    }

    [Fact]
    public async Task IndexBuildToolAcceptsSqliteBackend()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        await using var harness = await McpTestHarness.CreateAsync();

        var payload = await harness.CallAndParseAsync("index.build", new Dictionary<string, object?>
        {
            ["runDirectory"] = runDirectory,
            ["backend"] = "sqlite"
        });

        Assert.Equal("sqlite", payload.RootElement.GetProperty("backend").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("documentCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.sqlite")));
    }

    [Fact]
    public async Task ChaptersBuildToolWritesChapterArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);
        await using var harness = await McpTestHarness.CreateAsync();

        var payload = await harness.CallAndParseAsync("chapters.build", new Dictionary<string, object?>
        {
            ["runDirectory"] = runDirectory,
            ["minDuration"] = 20,
            ["maxDuration"] = 45
        });

        Assert.True(payload.RootElement.GetProperty("chapterCount").GetInt32() >= 2);
        Assert.True(File.Exists(Path.Combine(runDirectory, "chapters", "chapters.json")));
    }

    [Fact]
    public async Task AlignToolWritesAlignmentArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);
        var chapterDirectory = Path.Combine(runDirectory, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        var chapters = new ChapterDocument("0.5", "chapter-run", DateTimeOffset.UtcNow, "offline-lexical",
            [new Chapter(0, 60, "00:00", "01:00", []), new Chapter(60, 90, "01:00", "01:30", [])]);
        await File.WriteAllTextAsync(
            Path.Combine(chapterDirectory, "chapters.json"),
            JsonSerializer.Serialize(chapters, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);

        await using var harness = await McpTestHarness.CreateAsync();

        var payload = await harness.CallAndParseAsync("align", new Dictionary<string, object?>
        {
            ["runDirectory"] = runDirectory
        });

        Assert.True(payload.RootElement.GetProperty("chaptersLoaded").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("chapterCount").GetInt32() >= 1);
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-chapter.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-slide.json")));
    }

    [Fact]
    public async Task QueueToolsEnqueueRunAndReturnStatus()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            var store = new ArtifactStore(temp.GetPath("runs"));
            await using var harness = await McpTestHarness.CreateAsync(services =>
            {
                services.AddSingleton(store);
                services.AddTransient(_ => AnalysisPipelineTests.CreatePipeline(store));
            });

            await harness.CallToolAsync("queue.enqueue", new Dictionary<string, object?>
            {
                ["source"] = sourcePath,
                ["queueId"] = "mcp-queue",
                ["jobId"] = "mcp-job",
                ["frames"] = 0,
                ["frameStrategy"] = "interval",
                ["noTranscript"] = true
            });

            var runPayload = await harness.CallAndParseAsync("queue.run", new Dictionary<string, object?>
            {
                ["queueId"] = "mcp-queue",
                ["concurrency"] = 1,
                ["retries"] = 0
            });
            var statusPayload = await harness.CallAndParseAsync("queue.status", new Dictionary<string, object?>
            {
                ["queueId"] = "mcp-queue"
            });

            Assert.Equal(1, runPayload.RootElement.GetProperty("succeeded").GetInt32());
            Assert.Equal("mcp-queue", statusPayload.RootElement.GetProperty("queueId").GetString());
            Assert.Equal("succeeded", statusPayload.RootElement.GetProperty("jobs")[0].GetProperty("status").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [Fact]
    public async Task ResourcesListIncludesReplayUriTemplates()
    {
        await using var harness = await McpTestHarness.CreateAsync();

        var templates = await harness.Client.ListResourceTemplatesAsync();
        var uris = templates.Select(t => t.UriTemplate).ToArray();

        Assert.Contains("replay://runs/{id}/manifest", uris);
        Assert.Contains("replay://runs/{id}/evidence", uris);
        Assert.Contains("replay://runs/{id}/transcript", uris);
        Assert.Contains("replay://runs/{id}/chapters", uris);
        Assert.Contains("replay://runs/{id}/aligned/by-chapter", uris);
        Assert.Contains("replay://runs/{id}/aligned/by-slide", uris);
        Assert.Contains("replay://runs/{id}/frames/{frameId}/ocr", uris);
        Assert.Contains("replay://runs/{id}/frames/{frameId}/vision", uris);
        Assert.Contains("replay://jobs/{jobId}/logs", uris);
    }

    [Fact]
    public async Task RunsListResourceReturnsRunDirectories()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("source.mp4", "mcp-list");
        await store.WriteJsonAsync(run, "manifest.json", AnalysisPipelineTests.CreateManifest("source.mp4", run.Id, DateTimeOffset.UnixEpoch), CancellationToken.None);

        await using var harness = await McpTestHarness.CreateAsync(services =>
        {
            services.AddSingleton(store);
        });

        var result = await harness.Client.ReadResourceAsync(new Uri("replay://runs"));
        var text = result.Contents.OfType<TextResourceContents>().Single().Text;
        using var doc = JsonDocument.Parse(text);
        var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Contains(runs, r => r.GetProperty("id").GetString() == "mcp-list");
    }

    [Fact]
    public async Task EvidenceResourceReturnsRunArtifact()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var run = store.CreateRun("source.mp4", "mcp-evidence");
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.9",
            Source: "source.mp4",
            VisionInstruction: "test",
            OcrInstruction: "",
            RunId: run.Id,
            Title: null,
            WebpageUrl: null,
            DurationSeconds: 1,
            AudioPath: null,
            Transcript: [],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await store.WriteJsonAsync(run, "evidence.json", evidence, CancellationToken.None);

        await using var harness = await McpTestHarness.CreateAsync(services =>
        {
            services.AddSingleton(store);
        });

        var result = await harness.Client.ReadResourceAsync(new Uri($"replay://runs/{run.Id}/evidence"));
        var text = result.Contents.OfType<TextResourceContents>().Single().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("0.9", doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("mcp-evidence", doc.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task JobManagerRunsAnalysisJobToSucceededSnapshot()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var existingRun = store.CreateRun(sourcePath, "mcp-reuse");
        await store.WriteJsonAsync(existingRun, "manifest.json", AnalysisPipelineTests.CreateManifest(sourcePath, existingRun.Id, DateTimeOffset.UnixEpoch), CancellationToken.None);
        var manager = new McpJobManager(() => AnalysisPipelineTests.CreatePipeline(store), temp.GetPath("jobs"));

        var job = manager.Create(AnalysisPipelineTests.CreateRequest(sourcePath, "mcp-reuse"));
        var snapshot = await WaitForTerminalSnapshotAsync(job);

        Assert.Equal("succeeded", snapshot.Status);
        Assert.Equal(existingRun.Id, snapshot.RunId);
        Assert.True(snapshot.Reused);
        Assert.NotNull(snapshot.Manifest);
        Assert.Contains(snapshot.Logs, log => log.Contains("Reusing existing run", StringComparison.Ordinal));
    }

    [Fact]
    public void JobManagerCancelReturnsFalseForUnknownJob()
    {
        using var temp = new TestTempDirectory();
        var manager = new McpJobManager(() => throw new InvalidOperationException("Pipeline should not be created."), temp.GetPath("jobs"));

        Assert.False(manager.Cancel("does-not-exist"));
    }

    [Fact]
    public async Task JobManagerRestoresCompletedJobSnapshotsFromDisk()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var existingRun = store.CreateRun(sourcePath, "mcp-persisted");
        await store.WriteJsonAsync(existingRun, "manifest.json", AnalysisPipelineTests.CreateManifest(sourcePath, existingRun.Id, DateTimeOffset.UnixEpoch), CancellationToken.None);
        var jobsDirectory = temp.GetPath("jobs");
        var manager = new McpJobManager(() => AnalysisPipelineTests.CreatePipeline(store), jobsDirectory);

        var job = manager.Create(AnalysisPipelineTests.CreateRequest(sourcePath, "mcp-persisted"));
        var completed = await WaitForTerminalSnapshotAsync(job);
        var restoredManager = new McpJobManager(() => throw new InvalidOperationException("Restored terminal jobs should not rerun."), jobsDirectory);
        var restored = restoredManager.Get(job.JobId)?.Snapshot(includeLogs: true);

        Assert.Equal("succeeded", completed.Status);
        Assert.NotNull(restored);
        Assert.Equal("succeeded", restored.Status);
        Assert.Equal(existingRun.Id, restored.RunId);
        Assert.True(restored.Reused);
    }

    private static async Task<McpJobSnapshot> WaitForTerminalSnapshotAsync(McpJob job)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = job.Snapshot(includeLogs: true);
            if (snapshot.Status is "succeeded" or "failed" or "cancelled")
            {
                return snapshot;
            }

            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException("Timed out waiting for MCP job to finish.");
    }

    private static async Task<string> CreateSearchRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "search-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "source.mp4",
            VisionInstruction: "test",
            OcrInstruction: "",
            RunId: "search-run",
            Title: "Search Fixture",
            WebpageUrl: null,
            DurationSeconds: 60,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(1, 5, "00:01", "The router supports WireGuard VPN."),
                new TranscriptSegment(10, 12, "00:10", "The speaker shows travel accessories.")
            ],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return runDirectory;
    }

    private static async Task<string> CreateChapterRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "chapter-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "source.mp4",
            VisionInstruction: "test",
            OcrInstruction: "",
            RunId: "chapter-run",
            Title: "Chapter Fixture",
            WebpageUrl: null,
            DurationSeconds: 90,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(0, 15, "00:00", "The router setup covers VPN tunnels and network firewall rules."),
                new TranscriptSegment(15, 30, "00:15", "Router throughput is measured during secure WireGuard traffic."),
                new TranscriptSegment(45, 60, "00:45", "The speaker changes to travel bags and packing organization."),
                new TranscriptSegment(60, 80, "01:00", "Travel accessories are compared for airports and daily carry.")
            ],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return runDirectory;
    }
}

/// <summary>
/// Wires an in-process MCP server (via the SDK's <see cref="StreamServerTransport"/>) to a
/// real <see cref="McpClient"/> over an in-memory <see cref="Pipe"/>. Mirrors how a hosted
/// agent would talk to <c>zakira-replay mcp serve --transport stdio</c> without spawning
/// a subprocess. Tests can override DI registrations to substitute the analysis pipeline
/// with a deterministic fake (see <c>AnalysisPipelineTests.CreatePipeline</c>).
/// </summary>
internal sealed class McpTestHarness : IAsyncDisposable
{
    private readonly IHost host;
    private readonly Pipe clientToServerPipe = new();
    private readonly Pipe serverToClientPipe = new();
    private McpServer? server;
    private McpClient? client;

    private McpTestHarness(IHost host)
    {
        this.host = host;
    }

    public McpClient Client => client ?? throw new InvalidOperationException("Harness not started.");

    public static async Task<McpTestHarness> CreateAsync(Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.Services.AddReplay();
        builder.Services.AddSingleton<McpJobManager>(provider => new McpJobManager(
            () => provider.GetRequiredService<AnalysisPipeline>(),
            Path.Combine(Path.GetTempPath(), "Zakira.Replay.Tests", Guid.NewGuid().ToString("N"))));
        configureServices?.Invoke(builder.Services);
        builder.Services.AddSingleton<ReplayTools>();
        builder.Services.AddSingleton<ReplayResources>();

        var host = builder.Build();
        var harness = new McpTestHarness(host);
        await harness.StartAsync();
        return harness;
    }

    private async Task StartAsync()
    {
        var transport = new StreamServerTransport(
            clientToServerPipe.Reader.AsStream(),
            serverToClientPipe.Writer.AsStream());

        var replayTools = host.Services.GetRequiredService<ReplayTools>();
        server = McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Zakira.Replay", Version = ReplayVersion.Current },
            ToolCollection = [
                McpServerTool.Create((Delegate)replayTools.AnalyzeAsync, new() { Name = "analyze", Description = "Synchronous analysis (≤10 min)." }),
                McpServerTool.Create((Delegate)replayTools.AnalyzeStart, new() { Name = "analyze.start" }),
                McpServerTool.Create((Delegate)replayTools.AnalyzeStatus, new() { Name = "analyze.status" }),
                McpServerTool.Create((Delegate)replayTools.AnalyzeResult, new() { Name = "analyze.result" }),
                McpServerTool.Create((Delegate)replayTools.AnalyzeCancel, new() { Name = "analyze.cancel" }),
                McpServerTool.Create((Delegate)replayTools.QueueEnqueueAsync, new() { Name = "queue.enqueue" }),
                McpServerTool.Create((Delegate)replayTools.QueueRunAsync, new() { Name = "queue.run" }),
                McpServerTool.Create((Delegate)replayTools.QueueStatusAsync, new() { Name = "queue.status" }),
                McpServerTool.Create((Delegate)replayTools.ClipAsync, new() { Name = "clip" }),
                McpServerTool.Create((Delegate)replayTools.FramesAsync, new() { Name = "frames" }),
                McpServerTool.Create((Delegate)replayTools.IndexBuildAsync, new() { Name = "index.build" }),
                McpServerTool.Create((Delegate)replayTools.IndexQueryAsync, new() { Name = "index.query" }),
                McpServerTool.Create((Delegate)replayTools.ChaptersBuildAsync, new() { Name = "chapters.build" }),
                McpServerTool.Create((Delegate)replayTools.AlignAsync, new() { Name = "align" }),
                McpServerTool.Create((Delegate)replayTools.DiscoverAsync, new() { Name = "discover" }),
                McpServerTool.Create((Delegate)replayTools.Doctor, new() { Name = "doctor" })
            ],
            ResourceCollection = BuildResourceCollection(host.Services.GetRequiredService<ReplayResources>())
        });
        _ = server.RunAsync();

        var clientTransport = new StreamClientTransport(
            clientToServerPipe.Writer.AsStream(),
            serverToClientPipe.Reader.AsStream());

        client = await McpClient.CreateAsync(clientTransport);
    }

    private static McpServerResourceCollection BuildResourceCollection(ReplayResources resources)
    {
        var collection = new McpServerResourceCollection();
        collection.Add(McpServerResource.Create(resources.ListRuns, new() { Name = "Runs index", UriTemplate = "replay://runs", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetManifest, new() { Name = "Run manifest", UriTemplate = "replay://runs/{id}/manifest", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetEvidence, new() { Name = "Run evidence", UriTemplate = "replay://runs/{id}/evidence", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetTranscript, new() { Name = "Run transcript", UriTemplate = "replay://runs/{id}/transcript", MimeType = "text/markdown" }));
        collection.Add(McpServerResource.Create(resources.GetChapters, new() { Name = "Run chapters", UriTemplate = "replay://runs/{id}/chapters", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetAlignedByChapter, new() { Name = "Evidence aligned by chapter", UriTemplate = "replay://runs/{id}/aligned/by-chapter", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetAlignedBySlide, new() { Name = "Evidence aligned by slide", UriTemplate = "replay://runs/{id}/aligned/by-slide", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create((string id, string frameId) => resources.GetFrameOcr(id, frameId), new() { Name = "Frame OCR result", UriTemplate = "replay://runs/{id}/frames/{frameId}/ocr", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create((string id, string frameId) => resources.GetFrameVision(id, frameId), new() { Name = "Frame vision result", UriTemplate = "replay://runs/{id}/frames/{frameId}/vision", MimeType = "application/json" }));
        collection.Add(McpServerResource.Create(resources.GetJobLogs, new() { Name = "Job log buffer", UriTemplate = "replay://jobs/{jobId}/logs", MimeType = "text/plain" }));
        return collection;
    }

    public async Task<CallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        return await Client.CallToolAsync(name, arguments, cancellationToken: CancellationToken.None);
    }

    public async Task<JsonDocument> CallAndParseAsync(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await CallToolAsync(name, arguments);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonDocument.Parse(text);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }
        if (server is not null)
        {
            await server.DisposeAsync();
        }
        host.Dispose();
    }
}

internal sealed class NullLoggerProvider : ILoggerProvider
{
    public static readonly NullLoggerProvider Instance = new();
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
    public void Dispose() { }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
