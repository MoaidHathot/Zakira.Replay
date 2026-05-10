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

    private readonly Func<AnalysisPipeline> pipelineFactory;

    public BatchRunner(Func<AnalysisPipeline> pipelineFactory)
    {
        this.pipelineFactory = pipelineFactory;
    }

    public async Task<BatchResult> RunAsync(string manifestPath, IProgress<string>? progress, CancellationToken cancellationToken)
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

        var batchId = string.IsNullOrWhiteSpace(manifest.BatchId)
            ? $"batch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : Slug.Create(manifest.BatchId, 80);

        var batchDirectory = Path.Combine(ArtifactStore.GetDefaultRootDirectory(), batchId);
        Directory.CreateDirectory(batchDirectory);

        var itemResults = new List<BatchItemResult>();
        for (var i = 0; i < manifest.Items.Count; i++)
        {
            var item = manifest.Items[i];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"[{i + 1}/{manifest.Items.Count}] {item.Source}");

            try
            {
                var request = new AnalyzeRequest(
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
                    SceneSafetyCap: item.SceneSafetyCap ?? manifest.SceneSafetyCap);

                var result = await pipelineFactory().AnalyzeAsync(request, progress, cancellationToken).ConfigureAwait(false);
                itemResults.Add(new BatchItemResult(item.Source, true, result.Run.Id, result.Run.Directory, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                itemResults.Add(new BatchItemResult(item.Source, false, null, null, ex.Message));
                if (!manifest.ContinueOnError)
                {
                    break;
                }
            }
        }

        var resultManifest = new BatchResult(batchId, batchDirectory, DateTimeOffset.UtcNow, itemResults);
        var resultPath = Path.Combine(batchDirectory, "batch-result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(resultManifest, JsonOptions), cancellationToken).ConfigureAwait(false);
        await File.AppendAllTextAsync(resultPath, Environment.NewLine, cancellationToken).ConfigureAwait(false);

        return resultManifest;
    }
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

    public bool? SlideGrouping { get; set; }

    public int? SlideHashDistance { get; set; }

    public int? FramesPerMinute { get; set; }

    public int? SceneSafetyCap { get; set; }

    public bool ContinueOnError { get; set; } = true;

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

    public bool? SlideGrouping { get; set; }

    public int? SlideHashDistance { get; set; }

    public int? FramesPerMinute { get; set; }

    public int? SceneSafetyCap { get; set; }
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
