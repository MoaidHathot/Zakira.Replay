using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

/// <summary>
/// Background runner for ad-hoc frame captures (<c>frames-start</c> / <c>frames-status</c> /
/// <c>frames-result</c> / <c>frames-cancel</c>). The synchronous <c>frames</c> tool held the MCP
/// request open for the whole capture; against a remote source that re-resolved the media and
/// seeked a signed URL once per frame, which regularly outlived client request timeouts.
/// Mirrors <see cref="McpJobManager"/>: jobs persist as JSON under
/// <c>&lt;runs&gt;/.mcp/frame-jobs</c> and survive server restarts.
/// </summary>
public sealed class McpFrameJobManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<IFrameCaptureService> serviceFactory;
    private readonly string jobsDirectory;
    private readonly ConcurrentDictionary<string, McpFrameJob> jobs = new(StringComparer.OrdinalIgnoreCase);

    public McpFrameJobManager(Func<IFrameCaptureService> serviceFactory, string? jobsDirectory = null)
    {
        this.serviceFactory = serviceFactory;
        this.jobsDirectory = jobsDirectory ?? GetJobsDirectory(ArtifactStore.GetDefaultRootDirectory());
        Directory.CreateDirectory(this.jobsDirectory);
        LoadPersistedJobs();
    }

    public static string GetJobsDirectory(string runsRoot) => Path.Combine(runsRoot, ".mcp", "frame-jobs");

    public string JobsDirectory => jobsDirectory;

    public McpFrameJob Create(FrameCaptureRequest request)
    {
        var job = new McpFrameJob(Guid.NewGuid().ToString("N"), request, Persist);
        jobs[job.JobId] = job;
        Persist(job.State());
        _ = Task.Run(() => RunJobAsync(job));
        return job;
    }

    public McpFrameJob? Get(string jobId) => jobs.TryGetValue(jobId, out var job) ? job : null;

    public bool Cancel(string jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Cancel();
        return true;
    }

    private async Task RunJobAsync(McpFrameJob job)
    {
        job.MarkRunning();
        try
        {
            var result = await serviceFactory().CaptureAsync(job.Request, new Progress(job), job.CancellationToken).ConfigureAwait(false);
            job.MarkSucceeded(result);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
        }
        catch (Exception ex)
        {
            job.MarkFailed(McpJob.DescribeFailure(ex));
        }
    }

    private void LoadPersistedJobs()
    {
        foreach (var path in Directory.EnumerateFiles(jobsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var state = JsonSerializer.Deserialize<McpFrameJobState>(File.ReadAllText(path), JsonOptions);
                if (state is null)
                {
                    continue;
                }

                var job = McpFrameJob.Restore(state, Persist);
                if (job.Status is McpJobStatus.Pending or McpJobStatus.Running)
                {
                    job.MarkFailed("Server stopped before the job completed.");
                }

                jobs[job.JobId] = job;
            }
            catch (JsonException)
            {
                // Ignore corrupt snapshots; callers see the job as unknown.
            }
        }
    }

    private void Persist(McpFrameJobState state)
    {
        var path = Path.Combine(jobsDirectory, state.JobId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine);
    }

    private sealed class Progress : IProgress<string>
    {
        private readonly McpFrameJob job;
        public Progress(McpFrameJob job) { this.job = job; }
        public void Report(string value) => job.AddLog(value);
    }
}

public sealed class McpFrameJob
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly List<string> logs = [];
    private readonly Action<McpFrameJobState>? onChanged;

    public McpFrameJob(string jobId, FrameCaptureRequest request, Action<McpFrameJobState>? onChanged = null)
    {
        JobId = jobId;
        Request = request;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = McpJobStatus.Pending;
        this.onChanged = onChanged;
    }

    private McpFrameJob(McpFrameJobState state, Action<McpFrameJobState>? onChanged)
    {
        JobId = state.JobId;
        Request = state.Request;
        CreatedAt = state.CreatedAt;
        StartedAt = state.StartedAt;
        CompletedAt = state.CompletedAt;
        Status = Enum.TryParse<McpJobStatus>(state.Status, ignoreCase: true, out var parsed) ? parsed : McpJobStatus.Failed;
        Error = state.Error;
        logs.AddRange(state.Logs);
        if (!string.IsNullOrWhiteSpace(state.RunId) && !string.IsNullOrWhiteSpace(state.ArtifactDirectory) && state.Manifest is not null)
        {
            Result = new FrameCaptureResult(new VideoRun(state.RunId, state.ArtifactDirectory), state.Manifest);
        }

        this.onChanged = onChanged;
    }

    public static McpFrameJob Restore(McpFrameJobState state, Action<McpFrameJobState>? onChanged = null) => new(state, onChanged);

    public string JobId { get; }
    public FrameCaptureRequest Request { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public McpJobStatus Status { get; private set; }
    public FrameCaptureResult? Result { get; private set; }
    public string? Error { get; private set; }

    [JsonIgnore]
    public CancellationToken CancellationToken => cancellation.Token;

    public void MarkRunning() => Transition(() => { Status = McpJobStatus.Running; StartedAt = DateTimeOffset.UtcNow; });

    public void MarkSucceeded(FrameCaptureResult result) => Transition(() => { Result = result; Status = McpJobStatus.Succeeded; CompletedAt = DateTimeOffset.UtcNow; });

    public void MarkFailed(string message) => Transition(() => { Error = message; Status = McpJobStatus.Failed; CompletedAt ??= DateTimeOffset.UtcNow; });

    public void MarkCancelled() => Transition(() => { Status = McpJobStatus.Cancelled; CompletedAt = DateTimeOffset.UtcNow; });

    public void AddLog(string message) => Transition(() =>
    {
        logs.Add($"{DateTimeOffset.UtcNow:u} {message}");
        if (logs.Count > 200)
        {
            logs.RemoveAt(0);
        }
    });

    public void Cancel() => cancellation.Cancel();

    public McpFrameJobSnapshot Snapshot(bool includeLogs)
    {
        lock (gate)
        {
            return new McpFrameJobSnapshot(
                JobId,
                Status.ToString().ToLowerInvariant(),
                CreatedAt,
                StartedAt,
                CompletedAt,
                Request.Source,
                Request.RunId,
                Result?.Run.Id,
                Result?.Run.Directory,
                Result?.Manifest,
                Error,
                includeLogs ? logs.ToArray() : []);
        }
    }

    public McpFrameJobState State()
    {
        lock (gate)
        {
            return StateUnsafe();
        }
    }

    private void Transition(Action mutate)
    {
        // Persist inside the lock so snapshots reach disk in transition order; invoking the
        // callback after releasing the lock let a log line written concurrently with
        // MarkSucceeded overwrite the terminal state with an older "running" snapshot.
        lock (gate)
        {
            mutate();
            onChanged?.Invoke(StateUnsafe());
        }
    }

    private McpFrameJobState StateUnsafe() => new(
        JobId,
        Request,
        CreatedAt,
        StartedAt,
        CompletedAt,
        Status.ToString().ToLowerInvariant(),
        Result?.Run.Id,
        Result?.Run.Directory,
        Result?.Manifest,
        Error,
        logs.ToArray());
}

public sealed record McpFrameJobSnapshot(
    string JobId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Source,
    string? RequestedRunId,
    string? RunId,
    string? ArtifactDirectory,
    FrameCaptureManifest? Manifest,
    string? Error,
    IReadOnlyList<string> Logs);

public sealed record McpFrameJobState(
    string JobId,
    FrameCaptureRequest Request,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? RunId,
    string? ArtifactDirectory,
    FrameCaptureManifest? Manifest,
    string? Error,
    IReadOnlyList<string> Logs);
