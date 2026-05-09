using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

public sealed class McpJobManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<AnalysisPipeline> pipelineFactory;
    private readonly string jobsDirectory;
    private readonly ConcurrentDictionary<string, McpJob> jobs = new(StringComparer.OrdinalIgnoreCase);

    public McpJobManager(Func<AnalysisPipeline> pipelineFactory, string? jobsDirectory = null)
    {
        this.pipelineFactory = pipelineFactory;
        this.jobsDirectory = jobsDirectory ?? Path.Combine(ArtifactStore.GetDefaultRootDirectory(), ".mcp", "jobs");
        Directory.CreateDirectory(this.jobsDirectory);
        LoadPersistedJobs();
    }

    public McpJob Create(AnalyzeRequest request)
    {
        var job = new McpJob(Guid.NewGuid().ToString("N"), request, Persist);
        jobs[job.JobId] = job;
        Persist(job);
        _ = Task.Run(() => RunJobAsync(job));
        return job;
    }

    public McpJob? Get(string jobId)
    {
        return jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public bool Cancel(string jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Cancel();
        return true;
    }

    private async Task RunJobAsync(McpJob job)
    {
        job.MarkRunning();
        var progress = new McpJobProgress(job);
        try
        {
            var result = await pipelineFactory().AnalyzeAsync(job.Request, progress, job.CancellationToken).ConfigureAwait(false);
            job.MarkSucceeded(result);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex);
        }
    }

    private void LoadPersistedJobs()
    {
        foreach (var path in Directory.EnumerateFiles(jobsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var state = JsonSerializer.Deserialize<McpJobState>(File.ReadAllText(path), JsonOptions);
                if (state is null)
                {
                    continue;
                }

                var job = McpJob.Restore(state, Persist);
                if (job.Status is McpJobStatus.Pending or McpJobStatus.Running)
                {
                    job.MarkFailed("Server stopped before the job completed.");
                }

                jobs[job.JobId] = job;
            }
            catch (JsonException)
            {
                // Ignore corrupt job snapshots; callers will see the job as unknown.
            }
        }
    }

    private void Persist(McpJob job)
    {
        Persist(job.State());
    }

    private void Persist(McpJobState state)
    {
        var path = Path.Combine(jobsDirectory, state.JobId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine);
    }
}

internal sealed class McpJobProgress : IProgress<string>
{
    private readonly McpJob job;

    public McpJobProgress(McpJob job)
    {
        this.job = job;
    }

    public void Report(string value)
    {
        job.AddLog(value);
    }
}

public sealed class McpJob
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly List<string> logs = [];
    private readonly Action<McpJobState>? onChanged;

    public McpJob(string jobId, AnalyzeRequest request, Action<McpJobState>? onChanged = null)
    {
        JobId = jobId;
        Request = request;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = McpJobStatus.Pending;
        this.onChanged = onChanged;
    }

    private McpJob(McpJobState state, Action<McpJobState>? onChanged)
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
            Result = new AnalyzeResult(new VideoRun(state.RunId, state.ArtifactDirectory), state.Manifest, state.Reused ?? false);
        }

        this.onChanged = onChanged;
    }

    public static McpJob Restore(McpJobState state, Action<McpJobState>? onChanged = null) => new(state, onChanged);

    public string JobId { get; }

    public AnalyzeRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public McpJobStatus Status { get; private set; }

    public AnalyzeResult? Result { get; private set; }

    public string? Error { get; private set; }

    [JsonIgnore]
    public CancellationToken CancellationToken => cancellation.Token;

    public void MarkRunning()
    {
        McpJobState state;
        lock (gate)
        {
            Status = McpJobStatus.Running;
            StartedAt = DateTimeOffset.UtcNow;
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void MarkSucceeded(AnalyzeResult result)
    {
        McpJobState state;
        lock (gate)
        {
            Result = result;
            Status = McpJobStatus.Succeeded;
            CompletedAt = DateTimeOffset.UtcNow;
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void MarkFailed(Exception ex)
    {
        McpJobState state;
        lock (gate)
        {
            Error = ex is ProcessFailedException processFailed && !string.IsNullOrWhiteSpace(processFailed.StandardError)
                ? ex.Message + Environment.NewLine + processFailed.StandardError.Trim()
                : ex.Message;
            Status = McpJobStatus.Failed;
            CompletedAt = DateTimeOffset.UtcNow;
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void MarkFailed(string message)
    {
        McpJobState state;
        lock (gate)
        {
            Error = message;
            Status = McpJobStatus.Failed;
            CompletedAt ??= DateTimeOffset.UtcNow;
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void MarkCancelled()
    {
        McpJobState state;
        lock (gate)
        {
            Status = McpJobStatus.Cancelled;
            CompletedAt = DateTimeOffset.UtcNow;
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void AddLog(string message)
    {
        McpJobState state;
        lock (gate)
        {
            logs.Add($"{DateTimeOffset.UtcNow:u} {message}");
            if (logs.Count > 200)
            {
                logs.RemoveAt(0);
            }
            state = StateUnsafe();
            onChanged?.Invoke(state);
        }
    }

    public void Cancel()
    {
        cancellation.Cancel();
    }

    public McpJobSnapshot Snapshot(bool includeLogs)
    {
        lock (gate)
        {
            return new McpJobSnapshot(
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
                Result?.Reused,
                Error,
                includeLogs ? logs.ToArray() : []);
        }
    }

    public McpJobState State()
    {
        lock (gate)
        {
            return StateUnsafe();
        }
    }

    private McpJobState StateUnsafe()
    {
        return new McpJobState(
            JobId,
            Request,
            CreatedAt,
            StartedAt,
            CompletedAt,
            Status.ToString().ToLowerInvariant(),
            Result?.Run.Id,
            Result?.Run.Directory,
            Result?.Manifest,
            Result?.Reused,
            Error,
            logs.ToArray());
    }
}

public enum McpJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record McpJobSnapshot(
    string JobId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Source,
    string? RequestedRunId,
    string? RunId,
    string? ArtifactDirectory,
    ArtifactManifest? Manifest,
    bool? Reused,
    string? Error,
    IReadOnlyList<string> Logs);

public sealed record McpJobState(
    string JobId,
    AnalyzeRequest Request,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? RunId,
    string? ArtifactDirectory,
    ArtifactManifest? Manifest,
    bool? Reused,
    string? Error,
    IReadOnlyList<string> Logs);
