using System.Collections.Concurrent;
using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class BatchConcurrencyTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, null, 1)]        // both unset → default 1 (sequential, preserves legacy behaviour)
    [InlineData(null, 4, 4)]            // manifest only
    [InlineData(2, null, 2)]            // CLI override only
    [InlineData(8, 2, 8)]               // override beats manifest (higher)
    [InlineData(1, 8, 1)]               // override beats manifest (lower)
    [InlineData(0, null, 1)]            // override below 1 clamps to 1
    [InlineData(null, 0, 1)]            // manifest below 1 clamps to 1
    [InlineData(-3, -5, 1)]             // both invalid clamp to 1
    public void ResolveConcurrencyAppliesPrecedenceAndClamp(int? overrideValue, int? manifestValue, int expected)
    {
        var manifest = new BatchManifest { Concurrency = manifestValue };

        Assert.Equal(expected, BatchRunner.ResolveConcurrency(manifest, overrideValue));
    }

    [Fact]
    public void ManifestBindsConcurrencyFromJson()
    {
        var json = """{ "concurrency": 5, "items": [{ "source": "x" }] }""";

        var manifest = JsonSerializer.Deserialize<BatchManifest>(json, WebOptions);

        Assert.NotNull(manifest);
        Assert.Equal(5, manifest!.Concurrency);
    }

    [Fact]
    public async Task ParallelismRespectsManifestConcurrency()
    {
        // 6 items, concurrency 3, each "analysis" sleeps long enough that the runner must hold
        // three workers open simultaneously to clear the batch in a reasonable time. Peak
        // overlap is captured via Interlocked; > concurrency would indicate the cap is broken,
        // < concurrency would indicate workers are being serialised.
        var observed = new ConcurrencyProbe();
        using var harness = await BatchHarness.CreateAsync(itemCount: 6, manifestConcurrency: 3);
        var runner = new BatchRunner(async (request, progress, ct) =>
        {
            observed.Enter();
            try { await Task.Delay(80, ct).ConfigureAwait(false); }
            finally { observed.Leave(); }
            return harness.SuccessFor(request);
        });

        var result = await harness.RunAsync(runner);

        Assert.Equal(6, result.Items.Count);
        Assert.All(result.Items, item => Assert.True(item.Succeeded));
        Assert.Equal(3, observed.MaxConcurrent);
    }

    [Fact]
    public async Task OverrideConcurrencyBeatsManifest()
    {
        var observed = new ConcurrencyProbe();
        using var harness = await BatchHarness.CreateAsync(itemCount: 4, manifestConcurrency: 1);
        var runner = new BatchRunner(async (request, progress, ct) =>
        {
            observed.Enter();
            try { await Task.Delay(60, ct).ConfigureAwait(false); }
            finally { observed.Leave(); }
            return harness.SuccessFor(request);
        });

        var result = await harness.RunAsync(runner, concurrencyOverride: 4);

        Assert.Equal(4, result.Items.Count);
        // Override must actually take effect; the manifest value of 1 must not pin the cap to 1.
        Assert.Equal(4, observed.MaxConcurrent);
    }

    [Fact]
    public async Task ResultsPreserveManifestOrderDespiteOutOfOrderCompletion()
    {
        // Item N's analyser sleeps (count - N) * 30ms so item 0 finishes last, item N-1 first.
        // The result list must still mirror the manifest order — index-keyed slots make that
        // free, but a future regression (e.g. switching to ConcurrentBag append) would notice.
        const int count = 4;
        using var harness = await BatchHarness.CreateAsync(itemCount: count, manifestConcurrency: count);
        var runner = new BatchRunner(async (request, progress, ct) =>
        {
            var index = harness.IndexOf(request);
            await Task.Delay((count - index) * 30, ct).ConfigureAwait(false);
            return harness.SuccessFor(request);
        });

        var result = await harness.RunAsync(runner);

        Assert.Equal(count, result.Items.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(harness.SourceAt(i), result.Items[i].Source);
        }
    }

    [Fact]
    public async Task ContinueOnErrorFalseStopsFurtherWorkUnderParallelism()
    {
        // continueOnError=false: when item 1 fails fast, the runner cancels its internal token
        // so items 2 and 3 never start, and the slow-running item 0 is yanked mid-flight. Final
        // result contains only the recorded failure (cancelled items don't get synthesised
        // entries).
        using var harness = await BatchHarness.CreateAsync(
            itemCount: 4,
            manifestConcurrency: 2,
            continueOnError: false);

        var started = new ConcurrentBag<int>();
        var runner = new BatchRunner(async (request, progress, ct) =>
        {
            var index = harness.IndexOf(request);
            started.Add(index);

            if (index == 0)
            {
                await Task.Delay(2_000, ct).ConfigureAwait(false); // gets cancelled
                return harness.SuccessFor(request);
            }
            if (index == 1)
            {
                throw new InvalidOperationException("boom");
            }
            return harness.SuccessFor(request);
        });

        var result = await harness.RunAsync(runner);

        var failure = Assert.Single(result.Items);
        Assert.Equal(harness.SourceAt(1), failure.Source);
        Assert.False(failure.Succeeded);
        Assert.Equal("boom", failure.Error);
        // Items 2 and 3 must never have been dispatched once item 1 cancelled the run.
        Assert.DoesNotContain(2, started);
        Assert.DoesNotContain(3, started);
    }

    [Fact]
    public async Task ContinueOnErrorTrueProcessesAllItemsDespiteFailures()
    {
        // Default (continueOnError=true): a mid-batch failure must NOT take down siblings or
        // later items. All four items appear in the result, one failed and three succeeded.
        using var harness = await BatchHarness.CreateAsync(itemCount: 4, manifestConcurrency: 2);
        var runner = new BatchRunner((request, progress, ct) =>
        {
            if (harness.IndexOf(request) == 2)
            {
                throw new InvalidOperationException("middle failed");
            }
            return Task.FromResult(harness.SuccessFor(request));
        });

        var result = await harness.RunAsync(runner);

        Assert.Equal(4, result.Items.Count);
        Assert.Equal(3, result.Items.Count(item => item.Succeeded));
        var failure = Assert.Single(result.Items.Where(item => !item.Succeeded));
        Assert.Equal(harness.SourceAt(2), failure.Source);
        Assert.Equal("middle failed", failure.Error);
    }

    [Fact]
    public async Task UserCancellationPropagatesAsOperationCanceledException()
    {
        using var harness = await BatchHarness.CreateAsync(itemCount: 4, manifestConcurrency: 2);
        using var userCts = new CancellationTokenSource();
        var runner = new BatchRunner(async (request, progress, ct) =>
        {
            await Task.Delay(2_000, ct).ConfigureAwait(false);
            return harness.SuccessFor(request);
        });

        // Trigger cancellation shortly after the run starts; the outer token MUST surface as OCE
        // (distinguishing it from the internal stop-on-failure token, which is swallowed).
        userCts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(runner, cancellationToken: userCts.Token));
    }

    [Fact]
    public async Task PublicPipelineFactoryConstructorStillWorks()
    {
        // The factory-based public constructor must remain usable so existing callers
        // (CLI, MCP, scripts) don't have to switch to the internal analyzer delegate seam.
        using var temp = new TestTempDirectory();
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var manifestPath = temp.GetPath("batch.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BatchManifest
        {
            BatchId = "ctor-compat",
            Frames = 0,
            IncludeTranscript = false,
            Items = [new BatchItem { Source = sourcePath, RunId = "ctor-compat-item" }],
        }, WebOptions), CancellationToken.None);

        var store = new ArtifactStore(temp.GetPath("runs"));
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            var runner = new BatchRunner(() => AnalysisPipelineTests.CreatePipeline(store));
            var result = await runner.RunAsync(manifestPath, progress: null, CancellationToken.None);
            Assert.True(result.Items[0].Succeeded);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
        }
    }

    /// <summary>
    /// Test harness that materialises a manifest file on disk, redirects the artifact root to a
    /// scoped temp directory (so concurrent test runs never collide), and exposes helpers to map
    /// AnalyzeRequest → manifest index. Disposed via <c>using</c> to restore CWD and clean up.
    /// </summary>
    private sealed class BatchHarness : IDisposable
    {
        private readonly TestTempDirectory temp;
        private readonly string manifestPath;
        private readonly string previousCwd;
        private readonly Dictionary<string, int> sourceToIndex;

        public IReadOnlyList<string> Sources { get; }

        private BatchHarness(TestTempDirectory temp, string manifestPath, IReadOnlyList<string> sources)
        {
            this.temp = temp;
            this.manifestPath = manifestPath;
            Sources = sources;
            sourceToIndex = sources.Select((source, index) => (source, index)).ToDictionary(x => x.source, x => x.index);
            previousCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = temp.Path;
        }

        public static async Task<BatchHarness> CreateAsync(
            int itemCount,
            int? manifestConcurrency = null,
            bool continueOnError = true)
        {
            var temp = new TestTempDirectory();
            // Synthetic URLs; the fake analyzer never touches them.
            var sources = Enumerable.Range(0, itemCount).Select(i => $"https://example.test/v{i}").ToArray();
            var manifest = new BatchManifest
            {
                BatchId = $"concurrency-{Guid.NewGuid():N}",
                Concurrency = manifestConcurrency,
                ContinueOnError = continueOnError,
                Items = sources.Select((source, index) => new BatchItem
                {
                    Source = source,
                    RunId = $"item-{index}",
                }).ToList(),
            };
            var manifestPath = Path.Combine(temp.Path, "batch.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, WebOptions));
            return new BatchHarness(temp, manifestPath, sources);
        }

        public Task<BatchResult> RunAsync(
            BatchRunner runner,
            int? concurrencyOverride = null,
            CancellationToken cancellationToken = default)
            => runner.RunAsync(manifestPath, progress: null, cancellationToken, concurrencyOverride);

        public int IndexOf(AnalyzeRequest request) => sourceToIndex[request.Source];

        public string SourceAt(int index) => Sources[index];

        // Synthesise a minimal AnalyzeResult; the runner only reads Run.Id / Run.Directory.
        public AnalyzeResult SuccessFor(AnalyzeRequest request)
        {
            var index = IndexOf(request);
            var run = new VideoRun($"item-{index}", Path.Combine(temp.Path, "runs", $"item-{index}"));
            Directory.CreateDirectory(run.Directory);
            var manifest = AnalysisPipelineTests.CreateManifest(request.Source, run.Id, DateTimeOffset.UtcNow);
            return new AnalyzeResult(run, manifest);
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = previousCwd;
            temp.Dispose();
        }
    }

    /// <summary>
    /// Lock-free tracker for the maximum number of concurrently-executing analyser calls. CAS
    /// loop avoids the "between Increment and Read" race that a naive
    /// <c>Math.Max(max, current)</c> would have.
    /// </summary>
    private sealed class ConcurrencyProbe
    {
        private int current;
        private int max;

        public int MaxConcurrent => Volatile.Read(ref max);

        public void Enter()
        {
            var snapshot = Interlocked.Increment(ref current);
            int observed;
            do
            {
                observed = Volatile.Read(ref max);
                if (snapshot <= observed) return;
            }
            while (Interlocked.CompareExchange(ref max, snapshot, observed) != observed);
        }

        public void Leave() => Interlocked.Decrement(ref current);
    }
}
