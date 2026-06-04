using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class BatchRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<AnalyzeRequest, IProgress<string>?, CancellationToken, Task<AnalyzeResult>> analyzer;

    public BatchRunner(Func<AnalysisPipeline> pipelineFactory)
        : this((request, progress, ct) => pipelineFactory().AnalyzeAsync(request, progress, ct))
    {
    }

    // Test seam: tests inject a controllable analyzer delegate (delay, failure, parallelism
    // counter) without having to subclass the sealed AnalysisPipeline.
    internal BatchRunner(Func<AnalyzeRequest, IProgress<string>?, CancellationToken, Task<AnalyzeResult>> analyzer)
    {
        this.analyzer = analyzer;
    }

    public async Task<BatchResult> RunAsync(string manifestPath, IProgress<string>? progress, CancellationToken cancellationToken)
        => await RunAsync(manifestPath, progress, cancellationToken, concurrencyOverride: null).ConfigureAwait(false);

    /// <summary>
    /// Run a batch manifest with optional CLI-supplied concurrency. <paramref name="concurrencyOverride"/>
    /// (when set) takes precedence over <see cref="BatchManifest.Concurrency"/>, which in turn beats
    /// the built-in default of <c>1</c> (sequential). Items are dispatched via
    /// <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// with <c>MaxDegreeOfParallelism</c> capped at the resolved concurrency; ordering of the
    /// returned <see cref="BatchResult.Items"/> mirrors the manifest input order regardless of
    /// completion order. When <see cref="BatchManifest.ContinueOnError"/> is <c>false</c>, the
    /// first failure cancels an internal linked token so no further items start; in-flight items
    /// are best-effort cancelled. User cancellation via <paramref name="cancellationToken"/>
    /// always bubbles up unchanged.
    /// </summary>
    public async Task<BatchResult> RunAsync(string manifestPath, IProgress<string>? progress, CancellationToken cancellationToken, int? concurrencyOverride)
    {
        if (!File.Exists(manifestPath))
        {
            throw new ReplayException($"Batch manifest does not exist: {manifestPath}");
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<BatchManifest>(manifestJson, JsonOptions)
            ?? throw new ReplayException("Batch manifest is empty or invalid.");

        if (manifest.Items.Count == 0)
        {
            throw new ReplayException("Batch manifest contains no items.");
        }

        var concurrency = ResolveConcurrency(manifest, concurrencyOverride);

        var batchId = string.IsNullOrWhiteSpace(manifest.BatchId)
            ? $"batch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : Slug.Create(manifest.BatchId, 80);

        var batchDirectory = Path.Combine(ArtifactStore.GetDefaultRootDirectory(), batchId);
        Directory.CreateDirectory(batchDirectory);

        // One slot per input item — each task writes to its own index, so we get manifest-order
        // results back without locking. Items that never start (e.g. stopped by continueOnError or
        // cancelled mid-flight) leave their slot null and are dropped from the final list.
        var resultSlots = new BatchItemResult?[manifest.Items.Count];

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = stopCts.Token,
        };

        try
        {
            await Parallel.ForEachAsync(
                manifest.Items.Select((item, index) => (item, index)),
                parallelOptions,
                async (entry, itemToken) =>
                {
                    var (item, index) = entry;
                    progress?.Report($"[{index + 1}/{manifest.Items.Count}] {item.Source}");

                    try
                    {
                        var request = BuildAnalyzeRequest(manifest, item);
                        var result = await analyzer(request, progress, itemToken).ConfigureAwait(false);
                        resultSlots[index] = new BatchItemResult(item.Source, true, result.Run.Id, result.Run.Directory, null);
                    }
                    catch (OperationCanceledException)
                    {
                        // Either user cancellation or stop-on-failure: let it propagate so
                        // ForEachAsync exits promptly. Don't synthesise a "failed" entry for an
                        // item that was actively yanked.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        resultSlots[index] = new BatchItemResult(item.Source, false, null, null, ex.Message);
                        if (!manifest.ContinueOnError)
                        {
                            // Signal sibling tasks (and any not-yet-started items) to stop. The
                            // outer catch below swallows the resulting OCE when it's ours.
                            stopCts.Cancel();
                        }
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own stop-on-failure cancellation tripped; the failure that triggered it is
            // already recorded in resultSlots. User-initiated cancellation (outer token) falls
            // through the `when` and propagates to the caller as before.
        }

        var itemResults = resultSlots.Where(slot => slot is not null).Cast<BatchItemResult>().ToList();

        var resultManifest = new BatchResult(batchId, batchDirectory, DateTimeOffset.UtcNow, itemResults);
        var resultPath = Path.Combine(batchDirectory, "batch-result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(resultManifest, JsonOptions), cancellationToken).ConfigureAwait(false);
        await File.AppendAllTextAsync(resultPath, Environment.NewLine, cancellationToken).ConfigureAwait(false);

        return resultManifest;
    }

    /// <summary>
    /// Precedence: CLI <c>--concurrency</c> override beats <see cref="BatchManifest.Concurrency"/>
    /// beats the default of <c>1</c> (sequential). Values below <c>1</c> are clamped to <c>1</c>
    /// so a misconfigured manifest never deadlocks the run.
    /// </summary>
    internal static int ResolveConcurrency(BatchManifest manifest, int? concurrencyOverride)
    {
        var value = concurrencyOverride ?? manifest.Concurrency ?? 1;
        return value < 1 ? 1 : value;
    }

    /// <summary>
    /// Build the per-item <see cref="AnalyzeRequest"/>, layering item overrides over manifest-level
    /// defaults over the request record's own defaults. Extracted (and made internal) so the
    /// manifest-binding contract — including capture mode, auth profile, and diarization — can be
    /// asserted without spinning up the pipeline.
    /// </summary>
    internal static AnalyzeRequest BuildAnalyzeRequest(BatchManifest manifest, BatchItem item)
        => new(
            Source: item.Source,
            VisionInstruction: item.VisionInstruction ?? manifest.VisionInstruction ?? string.Empty,
            OcrInstruction: item.OcrInstruction ?? manifest.OcrInstruction ?? string.Empty,
            IncludeTranscript: item.IncludeTranscript ?? manifest.IncludeTranscript ?? true,
            FrameCount: item.Frames ?? manifest.Frames ?? 7,
            RunId: item.RunId,
            ExtractAudio: item.ExtractAudio ?? manifest.ExtractAudio ?? false,
            UseSpeechToText: item.UseSpeechToText ?? manifest.UseSpeechToText ?? false,
            UseOcr: item.UseOcr ?? manifest.UseOcr ?? false,
            UseVision: item.UseVision ?? manifest.UseVision ?? false,
            MaxAiFrames: item.MaxAiFrames ?? manifest.MaxAiFrames ?? 5,
            Model: item.Model ?? manifest.Model ?? LlmProviderFactory.GetDefaultModel(item.LlmProvider ?? manifest.LlmProvider),
            LlmProvider: LlmProviderFactory.Normalize(item.LlmProvider ?? manifest.LlmProvider),
            UseCache: item.UseCache ?? manifest.UseCache ?? false,
            FrameStrategy: item.FrameStrategy ?? manifest.FrameStrategy ?? FrameSelectionStrategies.Interval,
            CookiesPath: item.CookiesPath ?? manifest.CookiesPath,
            CookiesFromBrowser: item.CookiesFromBrowser ?? manifest.CookiesFromBrowser,
            CaptionLanguages: item.CaptionLanguages ?? manifest.CaptionLanguages,
            SlideGrouping: item.SlideGrouping ?? manifest.SlideGrouping,
            SlideHashDistance: item.SlideHashDistance ?? manifest.SlideHashDistance,
            FramesPerMinute: item.FramesPerMinute ?? manifest.FramesPerMinute,
            SceneSafetyCap: item.SceneSafetyCap ?? manifest.SceneSafetyCap,
            OcrProvider: OcrProviderFactory.Normalize(item.OcrProvider ?? manifest.OcrProvider),
            SmartCrop: item.SmartCrop ?? manifest.SmartCrop,
            SmartCropProfile: item.SmartCropProfile ?? manifest.SmartCropProfile,
            CaptureMode: item.CaptureMode ?? manifest.CaptureMode,
            AuthProfile: item.AuthProfile ?? manifest.AuthProfile,
            UseDiarization: item.UseDiarization ?? manifest.UseDiarization ?? false,
            NumSpeakers: item.NumSpeakers ?? manifest.NumSpeakers,
            DiarizationThreshold: ResolveDiarizationThreshold(item.DiarizationThreshold ?? manifest.DiarizationThreshold),
            SecondaryCaptionLanguages: item.SecondaryCaptionLanguages ?? manifest.SecondaryCaptionLanguages,
            PreferInlineMedia: item.PreferInlineMedia ?? manifest.PreferInlineMedia ?? false,
            AutoplayPolicy: item.AutoplayPolicy ?? manifest.AutoplayPolicy,
            AllowMediaDownload: item.AllowMediaDownload ?? manifest.AllowMediaDownload);

    // Mirror the CLI: a diarization threshold only takes effect when positive; otherwise leave it
    // unset so the pipeline applies its configured default. Narrow double -> float to match
    // AnalyzeRequest.DiarizationThreshold.
    private static float? ResolveDiarizationThreshold(double? threshold)
        => threshold is > 0 ? (float)threshold.Value : null;
}

public sealed class BatchManifest
{
    public string? BatchId { get; set; }

    public string? VisionInstruction { get; set; }

    public string? OcrInstruction { get; set; }

    public int? Frames { get; set; }

    public bool? IncludeTranscript { get; set; }

    public bool? ExtractAudio { get; set; }

    public bool? UseSpeechToText { get; set; }

    public bool? UseOcr { get; set; }

    public bool? UseVision { get; set; }

    public int? MaxAiFrames { get; set; }

    public string? Model { get; set; }

    public string? LlmProvider { get; set; }

    public bool? UseCache { get; set; }

    public string? FrameStrategy { get; set; }

    public string? CookiesPath { get; set; }

    public string? CookiesFromBrowser { get; set; }

    public List<string>? CaptionLanguages { get; set; }

    public List<string>? SecondaryCaptionLanguages { get; set; }

    public bool? PreferInlineMedia { get; set; }

    public string? AutoplayPolicy { get; set; }

    public bool? AllowMediaDownload { get; set; }

    public bool? SlideGrouping { get; set; }

    public int? SlideHashDistance { get; set; }

    public int? FramesPerMinute { get; set; }

    public int? SceneSafetyCap { get; set; }

    public string? OcrProvider { get; set; }

    public bool? SmartCrop { get; set; }

    public string? SmartCropProfile { get; set; }

    public string? CaptureMode { get; set; }

    public string? AuthProfile { get; set; }

    public bool? UseDiarization { get; set; }

    public int? NumSpeakers { get; set; }

    public double? DiarizationThreshold { get; set; }

    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Maximum number of items processed in parallel. <c>null</c> or <c>1</c> = sequential
    /// (preserves historic batch behaviour). When &gt;1, items are dispatched via
    /// <c>Parallel.ForEachAsync</c> with <c>MaxDegreeOfParallelism</c> capped at this value.
    /// CLI <c>--concurrency</c> on <c>batch run</c> overrides this.
    /// </summary>
    public int? Concurrency { get; set; }

    public List<BatchItem> Items { get; set; } = [];
}

public sealed class BatchItem
{
    public string Source { get; set; } = string.Empty;

    public string? VisionInstruction { get; set; }

    public string? OcrInstruction { get; set; }

    public int? Frames { get; set; }

    public bool? IncludeTranscript { get; set; }

    public bool? ExtractAudio { get; set; }

    public bool? UseSpeechToText { get; set; }

    public bool? UseOcr { get; set; }

    public bool? UseVision { get; set; }

    public int? MaxAiFrames { get; set; }

    public string? Model { get; set; }

    public string? LlmProvider { get; set; }

    public string? RunId { get; set; }

    public bool? UseCache { get; set; }

    public string? FrameStrategy { get; set; }

    public string? CookiesPath { get; set; }

    public string? CookiesFromBrowser { get; set; }

    public List<string>? CaptionLanguages { get; set; }

    public List<string>? SecondaryCaptionLanguages { get; set; }

    public bool? PreferInlineMedia { get; set; }

    public string? AutoplayPolicy { get; set; }

    public bool? AllowMediaDownload { get; set; }

    public bool? SlideGrouping { get; set; }

    public int? SlideHashDistance { get; set; }

    public int? FramesPerMinute { get; set; }

    public int? SceneSafetyCap { get; set; }

    public string? OcrProvider { get; set; }

    public bool? SmartCrop { get; set; }

    public string? SmartCropProfile { get; set; }

    public string? CaptureMode { get; set; }

    public string? AuthProfile { get; set; }

    public bool? UseDiarization { get; set; }

    public int? NumSpeakers { get; set; }

    public double? DiarizationThreshold { get; set; }
}

public sealed record BatchResult(
    string BatchId,
    string BatchDirectory,
    DateTimeOffset CompletedAt,
    IReadOnlyList<BatchItemResult> Items);

public sealed record BatchItemResult(
    string Source,
    bool Succeeded,
    string? RunId,
    string? ArtifactDirectory,
    string? Error);
