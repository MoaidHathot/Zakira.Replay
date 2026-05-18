using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

/// <summary>
/// MCP resources for Zakira.Replay artifacts. Resources are agent-readable artifacts at
/// stable URIs under the <c>replay://</c> scheme. Unlike tools, they aren't invocations —
/// clients (Claude Desktop, Cursor, VS Code) can attach them directly to chats or read them
/// without firing a tool call.
///
/// The URI design mirrors the on-disk layout: <c>replay://runs/{id}/manifest</c> resolves
/// to <c>runs/{id}/manifest.json</c>. <c>replay://jobs/{id}/logs</c> exposes the in-memory
/// log buffer of an MCP analysis job so agents can subscribe to long-running progress.
/// </summary>
[McpServerResourceType]
public sealed class ReplayResources
{
    private static readonly JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ArtifactStore artifactStore;
    private readonly McpJobManager jobManager;

    public ReplayResources(ArtifactStore artifactStore, McpJobManager jobManager)
    {
        this.artifactStore = artifactStore;
        this.jobManager = jobManager;
    }

    [McpServerResource(UriTemplate = "replay://runs", Name = "Runs index", MimeType = "application/json")]
    [Description("Returns the list of analysis runs available on disk (id, source, mtime).")]
    public ResourceContents ListRuns()
    {
        var root = artifactStore.RootDirectory;
        if (!Directory.Exists(root))
        {
            return Text("replay://runs", "{\"runs\":[]}");
        }

        var runs = Directory.EnumerateDirectories(root)
            .Where(dir => !Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal))
            .Select(dir => new
            {
                id = Path.GetFileName(dir),
                directory = dir,
                manifestExists = File.Exists(Path.Combine(dir, "manifest.json")),
                lastWriteUtc = Directory.GetLastWriteTimeUtc(dir)
            })
            .OrderByDescending(run => run.lastWriteUtc)
            .ToArray();

        return Text("replay://runs", JsonSerializer.Serialize(new { runs }, JsonOptions));
    }

    [McpServerResource(UriTemplate = "replay://runs/{id}/manifest", Name = "Run manifest", MimeType = "application/json")]
    [Description("Returns the manifest.json for an analysis run.")]
    public ResourceContents GetManifest(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "manifest.json", $"replay://runs/{id}/manifest", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/evidence", Name = "Run evidence", MimeType = "application/json")]
    [Description("Returns the evidence.json fact stream for an analysis run.")]
    public ResourceContents GetEvidence(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "evidence.json", $"replay://runs/{id}/evidence", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/transcript", Name = "Run transcript", MimeType = "text/markdown")]
    [Description("Returns the speaker-attributed transcript.md for an analysis run.")]
    public ResourceContents GetTranscript(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "transcript.md", $"replay://runs/{id}/transcript", "text/markdown");

    [McpServerResource(UriTemplate = "replay://runs/{id}/chapters", Name = "Run chapters", MimeType = "application/json")]
    [Description("Returns chapters/chapters.json for an analysis run.")]
    public ResourceContents GetChapters(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "chapters/chapters.json", $"replay://runs/{id}/chapters", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/aligned/by-chapter", Name = "Evidence aligned by chapter", MimeType = "application/json")]
    [Description("Returns evidence-aligned/by-chapter.json — the cross-modal alignment view rolled up by chapter.")]
    public ResourceContents GetAlignedByChapter(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "evidence-aligned/by-chapter.json", $"replay://runs/{id}/aligned/by-chapter", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/aligned/by-slide", Name = "Evidence aligned by slide", MimeType = "application/json")]
    [Description("Returns evidence-aligned/by-slide.json — the cross-modal alignment view rolled up by slide.")]
    public ResourceContents GetAlignedBySlide(string id) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), "evidence-aligned/by-slide.json", $"replay://runs/{id}/aligned/by-slide", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/frames/{frameId}/ocr", Name = "Frame OCR result", MimeType = "application/json")]
    [Description("Returns ocr/{frameId}.json — per-frame OCR output.")]
    public ResourceContents GetFrameOcr(string id, string frameId) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), $"ocr/{frameId}.json", $"replay://runs/{id}/frames/{frameId}/ocr", "application/json");

    [McpServerResource(UriTemplate = "replay://runs/{id}/frames/{frameId}/vision", Name = "Frame vision result", MimeType = "application/json")]
    [Description("Returns vision/{frameId}.json — per-frame structured vision output.")]
    public ResourceContents GetFrameVision(string id, string frameId) =>
        ReadFile(new VideoRun(id, Path.Combine(artifactStore.RootDirectory, id)), $"vision/{frameId}.json", $"replay://runs/{id}/frames/{frameId}/vision", "application/json");

    [McpServerResource(UriTemplate = "replay://jobs/{jobId}/logs", Name = "Job log buffer", MimeType = "text/plain")]
    [Description("Returns the most recent log lines from an in-process MCP analysis job.")]
    public ResourceContents GetJobLogs(string jobId)
    {
        var job = jobManager.Get(jobId) ?? throw new McpException($"Unknown jobId: {jobId}");
        var snapshot = job.Snapshot(includeLogs: true);
        return Text($"replay://jobs/{jobId}/logs", string.Join(Environment.NewLine, snapshot.Logs));
    }

    private static ResourceContents ReadFile(VideoRun run, string relativePath, string uri, string mimeType)
    {
        var path = run.GetPath(relativePath);
        if (!File.Exists(path))
        {
            throw new McpException($"Resource not found: {uri} (path: {path})");
        }

        var text = File.ReadAllText(path);
        return new TextResourceContents
        {
            Uri = uri,
            MimeType = mimeType,
            Text = text
        };
    }

    private static ResourceContents Text(string uri, string text)
    {
        return new TextResourceContents
        {
            Uri = uri,
            MimeType = "application/json",
            Text = text
        };
    }
}
