using System.Text.Json;
using Zakira.Replay.Core;
using Zakira.Replay.Mcp;

namespace Zakira.Replay.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task ToolsListIncludesJobLifecycleTools()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var server = new McpServer(() => throw new InvalidOperationException("Pipeline should not be created for tools/list."), stdout, stderr);

        await server.RunAsync(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}" + Environment.NewLine), CancellationToken.None);

        using var document = JsonDocument.Parse(stdout.ToString());
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("create_analysis_job", names);
        Assert.Contains("get_job_status", names);
        Assert.Contains("get_job_result", names);
        Assert.Contains("cancel_job", names);
        Assert.Contains("enqueue_analysis_queue_job", names);
        Assert.Contains("run_analysis_queue", names);
        Assert.Contains("get_analysis_queue_status", names);
        Assert.Contains("build_chapters", names);
    }

    [Fact]
    public async Task ToolsListSearchToolsIncludeBackendOptions()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var server = new McpServer(() => throw new InvalidOperationException("Pipeline should not be created for tools/list."), stdout, stderr);

        await server.RunAsync(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}" + Environment.NewLine), CancellationToken.None);

        using var document = JsonDocument.Parse(stdout.ToString());
        var tools = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var buildSearch = tools.Single(tool => tool.GetProperty("name").GetString() == "build_search_index");
        var querySearch = tools.Single(tool => tool.GetProperty("name").GetString() == "query_search_index");
        var analyze = tools.Single(tool => tool.GetProperty("name").GetString() == "create_analysis_job");

        Assert.True(buildSearch.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("backend", out _));
        Assert.True(querySearch.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("backend", out _));
        Assert.True(analyze.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("llmProvider", out _));
        Assert.True(analyze.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("everyFrame", out _));
    }

    [Fact]
    public async Task BuildSearchIndexToolAcceptsSqliteBackend()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var server = new McpServer(() => throw new InvalidOperationException("Pipeline should not be created for search tools."), stdout, stderr);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "build_search_index",
                arguments = new
                {
                    runDirectory,
                    backend = "sqlite"
                }
            }
        });

        await server.RunAsync(new StringReader(request + Environment.NewLine), CancellationToken.None);

        using var response = JsonDocument.Parse(stdout.ToString());
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var payload = JsonDocument.Parse(text!);

        Assert.Equal("sqlite", payload.RootElement.GetProperty("backend").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("documentCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.sqlite")));
    }

    [Fact]
    public async Task BuildChaptersToolWritesChapterArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var server = new McpServer(() => throw new InvalidOperationException("Pipeline should not be created for chapter tools."), stdout, stderr);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "build_chapters",
                arguments = new
                {
                    runDirectory,
                    minDuration = 20,
                    maxDuration = 45
                }
            }
        });

        await server.RunAsync(new StringReader(request + Environment.NewLine), CancellationToken.None);

        using var response = JsonDocument.Parse(stdout.ToString());
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var payload = JsonDocument.Parse(text!);

        Assert.True(payload.RootElement.GetProperty("chapterCount").GetInt32() >= 2);
        Assert.True(File.Exists(Path.Combine(runDirectory, "chapters", "chapters.json")));
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
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var server = new McpServer(() => AnalysisPipelineTests.CreatePipeline(store), stdout, stderr);
            var enqueue = SerializeToolCall(1, "enqueue_analysis_queue_job", new
            {
                source = sourcePath,
                queueId = "mcp-queue",
                jobId = "mcp-job",
                frames = 0,
                noTranscript = true
            });
            var run = SerializeToolCall(2, "run_analysis_queue", new
            {
                queueId = "mcp-queue",
                concurrency = 1,
                retries = 0
            });
            var status = SerializeToolCall(3, "get_analysis_queue_status", new
            {
                queueId = "mcp-queue"
            });

            await server.RunAsync(new StringReader(enqueue + Environment.NewLine + run + Environment.NewLine + status + Environment.NewLine), CancellationToken.None);

            var responses = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, responses.Length);
            using var runResponse = JsonDocument.Parse(responses[1]);
            var runText = runResponse.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            using var runPayload = JsonDocument.Parse(runText!);
            using var statusResponse = JsonDocument.Parse(responses[2]);
            var statusText = statusResponse.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            using var statusPayload = JsonDocument.Parse(statusText!);

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

    private static string SerializeToolCall(int id, string name, object arguments)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name,
                arguments
            }
        });
    }

    [Fact]
    public async Task BuildEvidenceAlignmentToolWritesAlignmentArtifacts()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateChapterRunAsync(temp);
        var chapterDirectory = Path.Combine(runDirectory, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        var chapters = new ChapterDocument("0.5", "chapter-run", DateTimeOffset.UtcNow, "offline-lexical",
            [new Chapter(0, 60, "00:00", "01:00", []), new Chapter(60, 90, "01:00", "01:30", [])]);
        await File.WriteAllTextAsync(Path.Combine(chapterDirectory, "chapters.json"), JsonSerializer.Serialize(chapters, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var server = new McpServer(() => throw new InvalidOperationException("Pipeline should not be created for alignment tools."), stdout, stderr);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "build_evidence_alignment",
                arguments = new { runDirectory }
            }
        });

        await server.RunAsync(new StringReader(request + Environment.NewLine), CancellationToken.None);

        using var response = JsonDocument.Parse(stdout.ToString());
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var payload = JsonDocument.Parse(text!);

        Assert.True(payload.RootElement.GetProperty("chaptersLoaded").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("chapterCount").GetInt32() >= 1);
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-chapter.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "evidence-aligned", "by-slide.json")));
    }

    private static async Task<string> CreateSearchRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "search-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.7",
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
            SchemaVersion: "0.7",
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
