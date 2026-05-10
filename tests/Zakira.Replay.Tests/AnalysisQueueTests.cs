using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class AnalysisQueueTests
{
    [Fact]
    public async Task QueuePersistsEnqueuedJobsAndRunsToSuccess()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var store = new ArtifactStore(temp.GetPath("runs"));
        var queue = new AnalysisQueue(() => AnalysisPipelineTests.CreatePipeline(store), temp.GetPath("queues"));

        var enqueue = await queue.EnqueueAsync("main", AnalysisPipelineTests.CreateRequest(sourcePath, "queue-run"), "job-1", retries: 0, CancellationToken.None);
        var statusBeforeRun = await queue.GetStatusAsync("main", CancellationToken.None);
        var result = await queue.RunAsync("main", new AnalysisQueueRunOptions(Concurrency: 1, Retries: 0), progress: null, CancellationToken.None);
        var statusAfterRun = await queue.GetStatusAsync("main", CancellationToken.None);

        Assert.Equal("main", enqueue.QueueId);
        Assert.Equal("job-1", enqueue.JobId);
        Assert.Single(statusBeforeRun.Jobs);
        Assert.Equal(AnalysisQueueJobStatuses.Pending, statusBeforeRun.Jobs[0].Status);
        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(AnalysisQueueJobStatuses.Succeeded, statusAfterRun.Jobs.Single().Status);
        Assert.True(File.Exists(Path.Combine(enqueue.QueueDirectory, "queue.json")));
        Assert.True(File.Exists(Path.Combine(enqueue.QueueDirectory, "last-run-result.json")));
    }

    [Fact]
    public async Task QueueRetriesFailedJobsUntilTheySucceed()
    {
        using var temp = new TestTempDirectory();
        var store = new ArtifactStore(temp.GetPath("runs"));
        var attempts = 0;
        var queue = new AnalysisQueue(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new AnalysisPipeline(store, new ThrowingYtDlpClient(), new ThrowingFfmpegClient());
            }

            return new AnalysisPipeline(store, new SucceedingYtDlpClient(), new ThrowingFfmpegClient());
        }, temp.GetPath("queues"));
        var request = new AnalyzeRequest(
            Source: "https://example.test/video",
            VisionInstruction: "retry test",
            IncludeTranscript: false,
            FrameCount: 0,
            RunId: "queue-retry");

        await queue.EnqueueAsync("retry", request, "retry-job", retries: 1, CancellationToken.None);
        var result = await queue.RunAsync("retry", new AnalysisQueueRunOptions(Concurrency: 1, Retries: 1), progress: null, CancellationToken.None);
        var status = await queue.GetStatusAsync("retry", CancellationToken.None);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        var job = status.Jobs.Single();
        Assert.Equal(2, job.Attempts);
        Assert.Equal(2, job.AttemptHistory.Count);
        Assert.Equal(AnalysisQueueJobStatuses.Failed, job.AttemptHistory[0].Status);
        Assert.Equal(AnalysisQueueJobStatuses.Succeeded, job.Status);
    }

    [Fact]
    public async Task QueueCliEnqueueStatusAndRunUsePersistentQueue()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var previousCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var enqueueExit = await Cli.CliApp.RunAsync(["queue", "enqueue", sourcePath, "--queue-id", "cli", "--job-id", "cli-job", "--frames", "0", "--no-transcript"], stdout, stderr, CancellationToken.None);
            var statusExit = await Cli.CliApp.RunAsync(["queue", "status", "--queue-id", "cli", "--json"], stdout, stderr, CancellationToken.None);
            var runExit = await Cli.CliApp.RunAsync(["queue", "run", "--queue-id", "cli", "--concurrency", "1"], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, enqueueExit);
            Assert.Equal(0, statusExit);
            Assert.Equal(0, runExit);
            Assert.Contains("cli-job", stdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(temp.Path, "runs", ".queue", "cli", "queue.json")));
            Assert.True(File.Exists(Path.Combine(temp.Path, "runs", ".queue", "cli", "last-run-result.json")));
            Assert.Empty(stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    private sealed class ThrowingYtDlpClient : IYtDlpClient
    {
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken) => throw new ReplayException("simulated metadata failure");

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => throw new ReplayException("simulated subtitle failure");

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => throw new ReplayException("simulated media url failure");

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("simulated media download failure");
    }

    private sealed class SucceedingYtDlpClient : IYtDlpClient
    {
        public Task<YtDlpInfo> GetInfoAsync(AnalyzeRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new YtDlpInfo
            {
                Id = "remote",
                Title = "Remote Video",
                WebpageUrl = request.Source,
                DurationSeconds = 60
            });
        }

        public Task<TranscriptArtifact?> DownloadBestSubtitleAsync(AnalyzeRequest request, VideoRun run, IReadOnlyList<string> subtitleLanguages, CancellationToken cancellationToken) => Task.FromResult<TranscriptArtifact?>(null);

        public Task<string?> GetBestMediaUrlAsync(AnalyzeRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> DownloadMediaForProcessingAsync(AnalyzeRequest request, VideoRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class ThrowingFfmpegClient : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken) => throw new ReplayException("simulated frame failure");

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken) => throw new ReplayException("simulated audio failure");

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken) => throw new ReplayException("simulated clip failure");

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken) => throw new ReplayException("simulated probe failure");

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken) => throw new ReplayException("simulated silence detection failure");

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken) => throw new ReplayException("simulated audio range failure");

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken) => throw new ReplayException("simulated perceptual hash failure");
    }
}
