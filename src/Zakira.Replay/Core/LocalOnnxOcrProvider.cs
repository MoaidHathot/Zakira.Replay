using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

namespace Zakira.Replay.Core;

/// <summary>
/// Local OCR provider backed by RapidOcrNet (PP-OCRv5 latin models). Runs entirely on the
/// caller's machine via Microsoft.ML.OnnxRuntime — no LLM, no network — and returns the same
/// JSON shape as <see cref="CopilotOcrProvider"/> so the downstream
/// <see cref="StructuredResponseParser.ParseOcr(string)"/> pipeline is unchanged.
/// </summary>
public sealed class LocalOnnxOcrProvider : IOcrProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocalOcrModelPaths paths;
    private readonly object initLock = new();
    private RapidOcr? engine;
    private bool initialised;

    public LocalOnnxOcrProvider(LocalOcrModelPaths paths)
    {
        this.paths = paths;
    }

    public Task<string> ExtractTextAsync(string imagePath, string ocrInstruction, CancellationToken cancellationToken)
    {
        // RapidOcrNet is synchronous and CPU-bound; defer to a worker thread so we keep the
        // pipeline's async-await contract intact and cancellation can still propagate.
        return Task.Run(() => ExtractTextCore(imagePath, ocrInstruction, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        engine?.Dispose();
        engine = null;
    }

    private string ExtractTextCore(string imagePath, string _, CancellationToken cancellationToken)
    {
        EnsureInitialised();
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(imagePath))
        {
            throw new ReplayException($"Local OCR cannot read image: file not found at '{imagePath}'.");
        }

        OcrResult result;
        using (var bitmap = SKBitmap.Decode(imagePath) ?? throw new ReplayException($"Local OCR could not decode image: '{imagePath}'."))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = engine!.Detect(bitmap, RapidOcrOptions.Default);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SerializeResultAsOcrJson(result);
    }

    /// <summary>
    /// Translates a <see cref="OcrResult"/> into the same strict JSON shape
    /// <see cref="CopilotOcrProvider"/> asks the LLM to emit.
    /// Public so unit tests can verify the contract directly.
    /// </summary>
    public static string SerializeResultAsOcrJson(OcrResult result)
    {
        // Preserve reading order top-to-bottom, then left-to-right. RapidOCR returns blocks in
        // detection order which is roughly that already, but we sort to be robust against the
        // dbnet's NMS choices on overlapping boxes.
        var lines = (result.TextBlocks ?? [])
            .Where(block => block is not null)
            .Select(block => new
            {
                Text = (block.GetText() ?? string.Empty).Trim(),
                Y = block.BoxPoints is { Length: > 0 } points ? (int)points.Min(p => p.Y) : 0,
                X = block.BoxPoints is { Length: > 0 } pts ? (int)pts.Min(p => p.X) : 0
            })
            .Where(line => line.Text.Length > 0)
            .OrderBy(line => line.Y / 8) // bucket by ~line height so we don't reorder same-line columns
            .ThenBy(line => line.X)
            .Select(line => line.Text)
            .ToArray();

        var freeText = string.Join("\n", lines);

        var payload = new
        {
            freeText,
            lines,
            tables = Array.Empty<object>() // Table reconstruction needs layout analysis; future work.
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private void EnsureInitialised()
    {
        if (initialised)
        {
            return;
        }

        lock (initLock)
        {
            if (initialised)
            {
                return;
            }

            var missing = paths.MissingFiles();
            if (missing.Count > 0)
            {
                throw new ReplayException(
                    $"Local OCR models not found. Run `zakira-replay deps install ocr` or set `ocr.local.modelDirectory` in config. Missing: {string.Join(", ", missing)}.");
            }

            try
            {
                var ocr = new RapidOcr();
                ocr.InitModels(paths.DetectionPath, paths.ClassificationPath, paths.RecognitionPath, paths.DictionaryPath);
                engine = ocr;
                initialised = true;
            }
            catch (Exception ex) when (ex is not ReplayException)
            {
                throw new ReplayException($"Failed to initialise local OCR (RapidOcrNet): {ex.Message}", ex);
            }
        }
    }
}

/// <summary>
/// Resolved on-disk paths for the four PP-OCRv5 latin model files that
/// <see cref="LocalOnnxOcrProvider"/> needs. Use
/// <see cref="LocalOcrModelPaths.Resolve(ReplayConfig)"/> to populate from config/env defaults.
/// </summary>
public sealed record LocalOcrModelPaths(string DetectionPath, string ClassificationPath, string RecognitionPath, string DictionaryPath)
{
    /// <summary>
    /// Resolve model paths from environment variables, then explicit <c>ocr.local.*Path</c>
    /// config keys, then the configured (or default) RapidOCR model directory.
    /// </summary>
    public static LocalOcrModelPaths Resolve(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        var installer = new PortableDependencyInstaller(config);
        var directory = installer.Layout.OcrModelDirectory;

        var det = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_DETECTION_MODEL_PATH"),
            config.Ocr.Local.DetectionModelPath,
            Path.Combine(directory, PortableDependencyInstaller.OcrDetectionModelFile));

        var cls = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_CLASSIFICATION_MODEL_PATH"),
            config.Ocr.Local.ClassificationModelPath,
            Path.Combine(directory, PortableDependencyInstaller.OcrClassificationModelFile));

        var rec = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_RECOGNITION_MODEL_PATH"),
            config.Ocr.Local.RecognitionModelPath,
            Path.Combine(directory, PortableDependencyInstaller.OcrRecognitionModelFile));

        var dict = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_DICTIONARY_PATH"),
            config.Ocr.Local.DictionaryPath,
            Path.Combine(directory, PortableDependencyInstaller.OcrDictionaryFile));

        return new LocalOcrModelPaths(det!, cls!, rec!, dict!);
    }

    public IReadOnlyList<string> MissingFiles()
    {
        var missing = new List<string>(4);
        if (!File.Exists(DetectionPath)) missing.Add(DetectionPath);
        if (!File.Exists(ClassificationPath)) missing.Add(ClassificationPath);
        if (!File.Exists(RecognitionPath)) missing.Add(RecognitionPath);
        if (!File.Exists(DictionaryPath)) missing.Add(DictionaryPath);
        return missing;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
