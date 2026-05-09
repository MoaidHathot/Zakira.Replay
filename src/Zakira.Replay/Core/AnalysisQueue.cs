using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class AnalysisQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<AnalysisPipeline> pipelineFactory;
    private readonly string queuesRootDirectory;

    public AnalysisQueue(Func<AnalysisPipeline> pipelineFactory, string? queuesRootDirectory = null)
    {
        this.pipelineFactory = pipelineFactory;
        this.queuesRootDirectory = queuesRootDirectory ?? Path.Combine(ArtifactStore.GetDefaultRootDirectory(), ".queue");
        Directory.CreateDirectory(this.queuesRootDirectory);
    }

    public async Task<AnalysisQueueEnqueueResult> EnqueueAsync(
        string? queueId,
        AnalyzeRequest request,
        string? jobId,
        int retries,
        CancellationToken cancellationToken)
    {
        var state = await LoadOrCreateAsync(queueId, cancellationToken).ConfigureAwait(false);
        var normalizedJobId = string.IsNullOrWhiteSpace(jobId)
            ? Guid.NewGuid().ToString("N")
            : Slug.Create(jobId, 80);

        if (state.Jobs.Any(job => job.JobId.Equals(normalizedJobId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReplayException($"Queue job already exists: {normalizedJobId}");
        }

        var job = new AnalysisQueueJob
        {
            JobId = normalizedJobId,
            Request = request,
            Status = AnalysisQueueJobStatuses.Pending,
            Attempts = 0,
            MaxAttempts = Math.Max(1, retries + 1),
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        state.Jobs.Add(job);
        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new AnalysisQueueEnqueueResult(state.QueueId, job.JobId, GetQueueDirectory(state.QueueId), job);
    }

    public async Task<AnalysisQueueState> GetStatusAsync(string? queueId, CancellationToken cancellationToken)
    {
        var state = await LoadOrCreateAsync(queueId, cancellationToken).ConfigureAwait(false);
        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<AnalysisQueueRunResult> RunAsync(string? queueId, AnalysisQueueRunOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var state = await LoadOrCreateAsync(queueId, cancellationToken).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        var concurrency = Math.Max(1, options.Concurrency);
        var retries = Math.Max(0, options.Retries);
        var attemptedJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terminalJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runnable = state.Jobs
                .Where(job => IsRunnable(job, retries))
                .Take(concurrency)
                .ToArray();
            if (runnable.Length == 0)
            {
                break;
            }

            foreach (var job in runnable)
            {
                job.Status = AnalysisQueueJobStatuses.Running;
                job.Attempts++;
                job.MaxAttempts = Math.Max(job.MaxAttempts, retries + 1);
                job.StartedAt = DateTimeOffset.UtcNow;
                job.CompletedAt = null;
                job.Error = null;
                attemptedJobIds.Add(job.JobId);
                progress?.Report($"Starting queue job {job.JobId} attempt {job.Attempts}/{job.MaxAttempts}: {job.Request.Source}");
            }

            await SaveAsync(state, cancellationToken).ConfigureAwait(false);

            var outcomes = await Task.WhenAll(runnable.Select(job => RunJobAttemptAsync(job, cancellationToken))).ConfigureAwait(false);
            foreach (var outcome in outcomes)
            {
                var job = outcome.Job;
                var completedAt = DateTimeOffset.UtcNow;
                job.CompletedAt = completedAt;
                if (outcome.Result is not null)
                {
                    job.Status = AnalysisQueueJobStatuses.Succeeded;
                    job.RunId = outcome.Result.Run.Id;
                    job.ArtifactDirectory = outcome.Result.Run.Directory;
                    job.Error = null;
                    job.AttemptHistory.Add(new AnalysisQueueJobAttempt(job.Attempts, job.StartedAt ?? completedAt, completedAt, AnalysisQueueJobStatuses.Succeeded, job.RunId, job.ArtifactDirectory, null));
                    terminalJobIds.Add(job.JobId);
                    progress?.Report($"Succeeded queue job {job.JobId}: {job.ArtifactDirectory}");
                }
                else
                {
                    job.Error = outcome.Error ?? "Unknown queue job failure.";
                    job.AttemptHistory.Add(new AnalysisQueueJobAttempt(job.Attempts, job.StartedAt ?? completedAt, completedAt, AnalysisQueueJobStatuses.Failed, null, null, job.Error));
                    if (job.Attempts < job.MaxAttempts)
                    {
                        job.Status = AnalysisQueueJobStatuses.Pending;
                        progress?.Report($"Failed queue job {job.JobId}; retry pending: {job.Error}");
                    }
                    else
                    {
                        job.Status = AnalysisQueueJobStatuses.Failed;
                        terminalJobIds.Add(job.JobId);
                        progress?.Report($"Failed queue job {job.JobId}: {job.Error}");
                    }
                }
            }

            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        var result = new AnalysisQueueRunResult(
            SchemaVersion: "0.1",
            QueueId: state.QueueId,
            QueueDirectory: GetQueueDirectory(state.QueueId),
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            Concurrency: concurrency,
            Retries: retries,
            Attempted: attemptedJobIds.Count,
            Succeeded: terminalJobIds.Select(id => state.Jobs.Single(job => job.JobId == id)).Count(job => job.Status == AnalysisQueueJobStatuses.Succeeded),
            Failed: terminalJobIds.Select(id => state.Jobs.Single(job => job.JobId == id)).Count(job => job.Status == AnalysisQueueJobStatuses.Failed),
            Pending: state.Jobs.Count(job => job.Status == AnalysisQueueJobStatuses.Pending),
            Jobs: state.Jobs.Select(job => new AnalysisQueueRunJobResult(job.JobId, job.Status, job.Attempts, job.RunId, job.ArtifactDirectory, job.Error)).ToArray());

        await WriteRunResultAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<AnalysisQueueJobOutcome> RunJobAttemptAsync(AnalysisQueueJob job, CancellationToken cancellationToken)
    {
        try
        {
            var result = await pipelineFactory().AnalyzeAsync(job.Request, progress: null, cancellationToken).ConfigureAwait(false);
            return new AnalysisQueueJobOutcome(job, result, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AnalysisQueueJobOutcome(job, null, ex.Message);
        }
    }

    private async Task<AnalysisQueueState> LoadOrCreateAsync(string? queueId, CancellationToken cancellationToken)
    {
        var normalizedQueueId = NormalizeQueueId(queueId);
        var path = GetQueuePath(normalizedQueueId);
        if (!File.Exists(path))
        {
            return new AnalysisQueueState
            {
                QueueId = normalizedQueueId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<AnalysisQueueState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ReplayException($"Queue file is empty or invalid: {path}");
        RecoverInterruptedJobs(state);
        return state;
    }

    private async Task SaveAsync(AnalysisQueueState state, CancellationToken cancellationToken)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        var path = GetQueuePath(state.QueueId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRunResultAsync(AnalysisQueueRunResult result, CancellationToken cancellationToken)
    {
        var path = Path.Combine(result.QueueDirectory, "last-run-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private string GetQueuePath(string queueId) => Path.Combine(GetQueueDirectory(queueId), "queue.json");

    private string GetQueueDirectory(string queueId) => Path.Combine(queuesRootDirectory, queueId);

    private static string NormalizeQueueId(string? queueId) => string.IsNullOrWhiteSpace(queueId) ? "default" : Slug.Create(queueId, 80);

    private static bool IsRunnable(AnalysisQueueJob job, int retries)
    {
        if (job.Status is AnalysisQueueJobStatuses.Succeeded or AnalysisQueueJobStatuses.Running)
        {
            return false;
        }

        job.MaxAttempts = Math.Max(job.MaxAttempts, retries + 1);
        return job.Status == AnalysisQueueJobStatuses.Pending
            || (job.Status == AnalysisQueueJobStatuses.Failed && job.Attempts < job.MaxAttempts);
    }

    private static void RecoverInterruptedJobs(AnalysisQueueState state)
    {
        foreach (var job in state.Jobs.Where(job => job.Status == AnalysisQueueJobStatuses.Running))
        {
            job.Status = AnalysisQueueJobStatuses.Pending;
            job.Error = "Worker stopped before the job completed.";
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed record AnalysisQueueJobOutcome(AnalysisQueueJob Job, AnalyzeResult? Result, string? Error);
}

public sealed class AnalysisQueueState
{
    public string SchemaVersion { get; set; } = "0.1";

    public string QueueId { get; set; } = "default";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AnalysisQueueJob> Jobs { get; set; } = [];
}

public sealed class AnalysisQueueJob
{
    public string JobId { get; set; } = string.Empty;

    public AnalyzeRequest Request { get; set; } = null!;

    public string Status { get; set; } = AnalysisQueueJobStatuses.Pending;

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 1;

    public DateTimeOffset EnqueuedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? RunId { get; set; }

    public string? ArtifactDirectory { get; set; }

    public string? Error { get; set; }

    public List<AnalysisQueueJobAttempt> AttemptHistory { get; set; } = [];
}

public sealed record AnalysisQueueJobAttempt(
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Status,
    string? RunId,
    string? ArtifactDirectory,
    string? Error);

public static class AnalysisQueueJobStatuses
{
    public const string Pending = "pending";

    public const string Running = "running";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";
}

public sealed record AnalysisQueueRunOptions(int Concurrency = 1, int Retries = 0);

public sealed record AnalysisQueueEnqueueResult(string QueueId, string JobId, string QueueDirectory, AnalysisQueueJob Job);

public sealed record AnalysisQueueRunResult(
    string SchemaVersion,
    string QueueId,
    string QueueDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Concurrency,
    int Retries,
    int Attempted,
    int Succeeded,
    int Failed,
    int Pending,
    IReadOnlyList<AnalysisQueueRunJobResult> Jobs);

public sealed record AnalysisQueueRunJobResult(
    string JobId,
    string Status,
    int Attempts,
    string? RunId,
    string? ArtifactDirectory,
    string? Error);
