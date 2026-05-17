using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zakira.Replay.Core;

/// <summary>
/// Fully-local <see cref="IVisionProvider"/> that never calls an LLM. Three sub-modes:
/// heuristic (zero models), clip (CLIP zero-shot kind classification),
/// clip-caption (CLIP + Florence-2-base-ft image captioning).
/// </summary>
public sealed class LocalOnnxVisionProvider : IVisionProvider, IDisposable
{
    private const int ClipImageSize = 224;
    private const int FlorenceImageSize = 768;
    private const int FlorenceHiddenSize = 768;
    private const long FlorenceEosTokenId = 2;
    private const long FlorencePadTokenId = 1;
    private const long FlorenceDecoderStartTokenId = 2;
    private const string FlorenceTaskPrompt = "Describe in detail what is shown in the image.";

    private static readonly float[] ClipMean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] ClipStd = [0.26862954f, 0.26130258f, 0.27577711f];
    private static readonly float[] FlorenceMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] FlorenceStd = [0.229f, 0.224f, 0.225f];

    public static readonly IReadOnlyList<string> KindLabels =
    [
        "slide", "ui", "code", "diagram", "chart", "dashboard", "other"
    ];

    public static readonly IReadOnlyList<string> ClipKindPrompts =
    [
        "a photograph of a presentation slide",
        "a screenshot of a user interface",
        "a screenshot of source code",
        "a photograph of a diagram",
        "a chart with axes and data series",
        "a dashboard with multiple metrics",
        "a generic photograph"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocalVisionOptions options;
    private readonly IFfmpegClient ffmpeg;
    private readonly object initLock = new();

    private InferenceSession? clipImageSession;
    private float[][]? clipKindEmbeddings;
    private InferenceSession? florenceVisionSession;
    private InferenceSession? florenceEmbedTokensSession;
    private InferenceSession? florenceEncoderSession;
    private InferenceSession? florenceDecoderSession;
    private FlorenceBartBpeTokenizer? florenceTokenizer;

    private LocalVisionMode effectiveMode;
    private bool initialised;
    private bool disposed;
    private List<string>? initWarnings;

    public LocalOnnxVisionProvider(LocalVisionOptions options, IFfmpegClient ffmpeg)
    {
        this.options = options;
        this.ffmpeg = ffmpeg;
        effectiveMode = options.Mode;
    }

    public LocalVisionMode EffectiveMode => effectiveMode;
    public IReadOnlyList<string> InitializationWarnings => initWarnings ?? (IReadOnlyList<string>)Array.Empty<string>();

    public async Task<string> DescribeAsync(VisionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Initialise();

        var ocr = request.OcrContext?.Structured;
        var title = OcrToVisionHeuristics.DeriveTitle(ocr);
        var bullets = OcrToVisionHeuristics.DeriveBullets(ocr);
        var codeBlocks = OcrToVisionHeuristics.DeriveCodeBlocks(ocr);
        var uiElements = OcrToVisionHeuristics.DeriveUiElements(ocr);
        var ocrFreeText = OcrToVisionHeuristics.DeriveFreeText(ocr);

        string kind;
        if (effectiveMode >= LocalVisionMode.Clip && clipImageSession is not null && clipKindEmbeddings is not null)
        {
            kind = await ClassifyKindWithClipAsync(request.ImagePath, cancellationToken).ConfigureAwait(false)
                   ?? OcrToVisionHeuristics.DeriveKind(ocr);
        }
        else
        {
            kind = OcrToVisionHeuristics.DeriveKind(ocr);
        }

        string freeText;
        if (effectiveMode == LocalVisionMode.ClipCaption && IsFlorenceReady())
        {
            var caption = await GenerateFlorenceCaptionAsync(request.ImagePath, cancellationToken).ConfigureAwait(false);
            freeText = ComposeFreeText(caption, ocrFreeText);
        }
        else if (!string.IsNullOrWhiteSpace(ocrFreeText))
        {
            freeText = ocrFreeText;
        }
        else
        {
            freeText = "Frame contains no machine-readable text.";
        }

        return JsonSerializer.Serialize(new
        {
            kind,
            title,
            bullets,
            codeBlocks = codeBlocks.Select(b => new { language = b.Language, text = b.Text }).ToArray(),
            charts = Array.Empty<object>(),
            uiElements,
            freeText
        }, JsonOptions);
    }

    private static string ComposeFreeText(string? caption, string ocrFreeText)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return string.IsNullOrEmpty(ocrFreeText) ? "Frame contains no machine-readable text." : ocrFreeText;
        }

        var trimmed = caption.Trim();
        if (trimmed.Length > 0 && !trimmed.EndsWith('.') && !trimmed.EndsWith('!') && !trimmed.EndsWith('?'))
        {
            trimmed += ".";
        }

        return string.IsNullOrEmpty(ocrFreeText)
            ? $"Frame appears to show: {trimmed}"
            : $"Frame appears to show: {trimmed} Visible text: {ocrFreeText}";
    }

    private bool IsFlorenceReady() =>
        florenceVisionSession is not null
        && florenceEmbedTokensSession is not null
        && florenceEncoderSession is not null
        && florenceDecoderSession is not null
        && florenceTokenizer is not null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clipImageSession?.Dispose();
        florenceVisionSession?.Dispose();
        florenceEmbedTokensSession?.Dispose();
        florenceEncoderSession?.Dispose();
        florenceDecoderSession?.Dispose();
        clipImageSession = null;
        florenceVisionSession = null;
        florenceEmbedTokensSession = null;
        florenceEncoderSession = null;
        florenceDecoderSession = null;
    }

    public void Initialise()
    {
        if (initialised) return;
        lock (initLock)
        {
            if (initialised) return;
            var warnings = new List<string>();
            var actualMode = options.Mode;

            if (actualMode == LocalVisionMode.ClipCaption)
            {
                if (!TryInitClip(warnings)) actualMode = LocalVisionMode.Heuristic;
                else if (!TryInitFlorence(warnings)) actualMode = LocalVisionMode.Clip;
            }
            else if (actualMode == LocalVisionMode.Clip)
            {
                if (!TryInitClip(warnings)) actualMode = LocalVisionMode.Heuristic;
            }

            effectiveMode = actualMode;
            initWarnings = warnings;
            initialised = true;
        }
    }

    private bool TryInitClip(List<string> warnings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.ClipImageEncoderPath) || !File.Exists(options.ClipImageEncoderPath))
            {
                warnings.Add($"CLIP image-encoder model not found at '{options.ClipImageEncoderPath ?? "<unset>"}'. Falling back to heuristic mode.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.ClipKindEmbeddingsPath) || !File.Exists(options.ClipKindEmbeddingsPath))
            {
                warnings.Add($"CLIP kind-embeddings file not found at '{options.ClipKindEmbeddingsPath ?? "<unset>"}'. Generate it with `zakira-replay vision generate-clip-embeddings`. Falling back to heuristic mode.");
                return false;
            }

            clipImageSession = new InferenceSession(options.ClipImageEncoderPath);
            clipKindEmbeddings = LoadKindEmbeddings(options.ClipKindEmbeddingsPath, KindLabels.Count);
            return clipKindEmbeddings is not null;
        }
        catch (Exception ex)
        {
            warnings.Add($"CLIP initialisation failed: {ex.Message}. Falling back to heuristic mode.");
            clipImageSession?.Dispose();
            clipImageSession = null;
            clipKindEmbeddings = null;
            return false;
        }
    }

    private bool TryInitFlorence(List<string> warnings)
    {
        try
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(options.FlorenceVisionEncoderPath) || !File.Exists(options.FlorenceVisionEncoderPath)) missing.Add("vision_encoder");
            if (string.IsNullOrWhiteSpace(options.FlorenceEncoderPath) || !File.Exists(options.FlorenceEncoderPath)) missing.Add("encoder");
            if (string.IsNullOrWhiteSpace(options.FlorenceDecoderPath) || !File.Exists(options.FlorenceDecoderPath)) missing.Add("decoder");
            if (string.IsNullOrWhiteSpace(options.FlorenceEmbedTokensPath) || !File.Exists(options.FlorenceEmbedTokensPath)) missing.Add("embed_tokens");
            if (string.IsNullOrWhiteSpace(options.FlorenceVocabPath) || !File.Exists(options.FlorenceVocabPath)) missing.Add("vocab");
            if (string.IsNullOrWhiteSpace(options.FlorenceMergesPath) || !File.Exists(options.FlorenceMergesPath)) missing.Add("merges");
            if (missing.Count > 0)
            {
                warnings.Add($"Florence-2 captioner files missing ({string.Join(", ", missing)}). Run `zakira-replay deps install vision --mode clip-caption`. Falling back to CLIP-only mode.");
                return false;
            }

            florenceVisionSession = new InferenceSession(options.FlorenceVisionEncoderPath);
            florenceEmbedTokensSession = new InferenceSession(options.FlorenceEmbedTokensPath);
            florenceEncoderSession = new InferenceSession(options.FlorenceEncoderPath);
            florenceDecoderSession = new InferenceSession(options.FlorenceDecoderPath);
            florenceTokenizer = FlorenceBartBpeTokenizer.FromFiles(
                options.FlorenceVocabPath!,
                options.FlorenceMergesPath!,
                options.FlorenceAddedTokensPath);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Florence-2 initialisation failed: {ex.Message}. Falling back to CLIP-only mode.");
            florenceVisionSession?.Dispose();
            florenceEmbedTokensSession?.Dispose();
            florenceEncoderSession?.Dispose();
            florenceDecoderSession?.Dispose();
            florenceVisionSession = null;
            florenceEmbedTokensSession = null;
            florenceEncoderSession = null;
            florenceDecoderSession = null;
            florenceTokenizer = null;
            return false;
        }
    }

    private async Task<string?> ClassifyKindWithClipAsync(string imagePath, CancellationToken cancellationToken)
    {
        var pixels = await ffmpeg.PreprocessImageRgb24Async(imagePath, ClipImageSize, ClipImageSize, cancellationToken).ConfigureAwait(false);
        if (pixels is null) return null;

        var tensor = BuildNormalisedTensor(pixels, ClipImageSize, ClipMean, ClipStd);
        var inputName = clipImageSession!.InputMetadata.Keys.First();
        using var run = clipImageSession.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var output = run.First().AsTensor<float>();
        var embedding = output.ToArray();
        L2Normalise(embedding);

        var bestKind = 0;
        var bestScore = float.MinValue;
        for (var i = 0; i < clipKindEmbeddings!.Length; i++)
        {
            var score = Dot(embedding, clipKindEmbeddings[i]);
            if (score > bestScore) { bestScore = score; bestKind = i; }
        }

        return KindLabels[bestKind];
    }

    private async Task<string?> GenerateFlorenceCaptionAsync(string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Image preprocessing.
            var pixels = await ffmpeg.PreprocessImageRgb24Async(imagePath, FlorenceImageSize, FlorenceImageSize, cancellationToken).ConfigureAwait(false);
            if (pixels is null) return null;
            var pixelTensor = BuildNormalisedTensor(pixels, FlorenceImageSize, FlorenceMean, FlorenceStd);

            // 2. Vision encoder.
            var visionInputName = florenceVisionSession!.InputMetadata.Keys.First();
            float[] imageFeaturesFlat;
            int[] imageFeaturesDims;
            using (var visionRun = florenceVisionSession.Run([NamedOnnxValue.CreateFromTensor(visionInputName, pixelTensor)]))
            {
                var visionOutput = visionRun.First().AsTensor<float>();
                imageFeaturesDims = visionOutput.Dimensions.ToArray();
                imageFeaturesFlat = visionOutput.ToArray();
            }

            if (imageFeaturesDims.Length != 3 || imageFeaturesDims[2] != FlorenceHiddenSize) return null;
            var imageSeqLen = imageFeaturesDims[1];

            // 3. Tokenize task prompt.
            var taskIds = florenceTokenizer!.Encode(FlorenceTaskPrompt);
            var taskLen = taskIds.Length;

            // 4. Embed text tokens.
            var taskTensor = new DenseTensor<long>(taskIds, [1, taskLen]);
            var embedInputName = florenceEmbedTokensSession!.InputMetadata.Keys.First();
            float[] textEmbedsFlat;
            using (var embedRun = florenceEmbedTokensSession.Run([NamedOnnxValue.CreateFromTensor(embedInputName, taskTensor)]))
            {
                textEmbedsFlat = embedRun.First().AsTensor<float>().ToArray();
            }

            // 5. Concatenate image + text embeddings.
            var totalSeq = imageSeqLen + taskLen;
            var fusedFlat = new float[totalSeq * FlorenceHiddenSize];
            Array.Copy(imageFeaturesFlat, 0, fusedFlat, 0, imageSeqLen * FlorenceHiddenSize);
            Array.Copy(textEmbedsFlat, 0, fusedFlat, imageSeqLen * FlorenceHiddenSize, taskLen * FlorenceHiddenSize);
            var fusedTensor = new DenseTensor<float>(fusedFlat, [1, totalSeq, FlorenceHiddenSize]);
            var attentionMask = new long[totalSeq];
            for (var i = 0; i < totalSeq; i++) attentionMask[i] = 1;
            var attnTensor = new DenseTensor<long>(attentionMask, [1, totalSeq]);

            // 6. Encoder.
            float[] encoderHiddenFlat;
            int[] encoderHiddenDims;
            using (var encRun = florenceEncoderSession!.Run(BuildEncoderInputs(florenceEncoderSession, fusedTensor, attnTensor)))
            {
                var encOut = encRun.First().AsTensor<float>();
                encoderHiddenDims = encOut.Dimensions.ToArray();
                encoderHiddenFlat = encOut.ToArray();
            }

            // 7. Greedy decode.
            var maxTokens = options.FlorenceMaxTokens;
            var generated = new List<long> { FlorenceDecoderStartTokenId };
            for (var step = 0; step < maxTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextToken = DecoderStep(encoderHiddenFlat, encoderHiddenDims, attentionMask, generated);
                if (nextToken < 0) return null;
                if (nextToken == FlorenceEosTokenId && generated.Count > 1) break;
                generated.Add(nextToken);
            }

            return florenceTokenizer.Decode(generated.Skip(1).Where(t => t != FlorenceEosTokenId && t != FlorencePadTokenId).ToArray());
        }
        catch
        {
            return null;
        }
    }

    private long DecoderStep(
        float[] encoderHiddenFlat,
        int[] encoderHiddenDims,
        long[] encoderAttentionMask,
        List<long> generated)
    {
        var encoderTensor = new DenseTensor<float>(encoderHiddenFlat, encoderHiddenDims);
        var encMaskTensor = new DenseTensor<long>(encoderAttentionMask, [1, encoderAttentionMask.Length]);
        var inputIds = new DenseTensor<long>(generated.ToArray(), [1, generated.Count]);

        var decoderInputs = new List<NamedOnnxValue>();
        var decoderMeta = florenceDecoderSession!.InputMetadata;

        if (decoderMeta.ContainsKey("encoder_hidden_states"))
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderTensor));
        if (decoderMeta.ContainsKey("encoder_attention_mask"))
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encMaskTensor));

        if (decoderMeta.ContainsKey("input_ids"))
        {
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
        }
        else if (decoderMeta.ContainsKey("decoder_input_ids"))
        {
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("decoder_input_ids", inputIds));
        }
        else if (decoderMeta.ContainsKey("inputs_embeds"))
        {
            using var embedRun = florenceEmbedTokensSession!.Run([NamedOnnxValue.CreateFromTensor(
                florenceEmbedTokensSession.InputMetadata.Keys.First(), inputIds)]);
            var inputEmbedsFlat = embedRun.First().AsTensor<float>().ToArray();
            var embedsTensor = new DenseTensor<float>(inputEmbedsFlat, [1, generated.Count, FlorenceHiddenSize]);
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("inputs_embeds", embedsTensor));
        }

        if (decoderMeta.ContainsKey("use_cache_branch"))
        {
            var useCacheBranch = new DenseTensor<bool>(new[] { false }, [1]);
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("use_cache_branch", useCacheBranch));
        }

        foreach (var name in decoderMeta.Keys)
        {
            if (!name.StartsWith("past_key_values.", StringComparison.Ordinal)) continue;
            if (decoderInputs.Any(v => v.Name == name)) continue;
            var emptyTensor = new DenseTensor<float>(Array.Empty<float>(), [1, 12, 0, 64]);
            decoderInputs.Add(NamedOnnxValue.CreateFromTensor(name, emptyTensor));
        }

        try
        {
            using var decoderRun = florenceDecoderSession.Run(decoderInputs);
            var logits = decoderRun.First(r => r.Name.Contains("logits", StringComparison.OrdinalIgnoreCase)).AsTensor<float>();
            var dims = logits.Dimensions;
            if (dims.Length != 3) return -1;

            var lastPos = dims[1] - 1;
            var bestId = 0L;
            var bestScore = float.MinValue;
            for (var v = 0; v < dims[2]; v++)
            {
                var score = logits[0, lastPos, v];
                if (score > bestScore) { bestScore = score; bestId = v; }
            }

            return bestId;
        }
        catch
        {
            return -1;
        }
    }

    private static IReadOnlyList<NamedOnnxValue> BuildEncoderInputs(
        InferenceSession session,
        DenseTensor<float> fusedEmbeds,
        DenseTensor<long> attentionMask)
    {
        var meta = session.InputMetadata;
        var inputs = new List<NamedOnnxValue>();

        if (meta.ContainsKey("inputs_embeds"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("inputs_embeds", fusedEmbeds));
        else
            inputs.Add(NamedOnnxValue.CreateFromTensor(meta.Keys.First(), fusedEmbeds));

        if (meta.ContainsKey("attention_mask"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));

        return inputs;
    }

    private static DenseTensor<float> BuildNormalisedTensor(byte[] rgb, int size, float[] mean, float[] std)
    {
        var tensor = new DenseTensor<float>([1, 3, size, size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var src = (y * size + x) * 3;
                tensor[0, 0, y, x] = (rgb[src] / 255f - mean[0]) / std[0];
                tensor[0, 1, y, x] = (rgb[src + 1] / 255f - mean[1]) / std[1];
                tensor[0, 2, y, x] = (rgb[src + 2] / 255f - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    private static float[][]? LoadKindEmbeddings(string path, int expectedRows)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % (sizeof(float) * expectedRows) != 0) return null;
            var floatsPerRow = bytes.Length / sizeof(float) / expectedRows;
            if (floatsPerRow == 0) return null;

            var embeddings = new float[expectedRows][];
            for (var row = 0; row < expectedRows; row++)
            {
                embeddings[row] = new float[floatsPerRow];
                Buffer.BlockCopy(bytes, row * floatsPerRow * sizeof(float), embeddings[row], 0, floatsPerRow * sizeof(float));
                L2Normalise(embeddings[row]);
            }

            return embeddings;
        }
        catch
        {
            return null;
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        var min = Math.Min(a.Length, b.Length);
        var sum = 0f;
        for (var i = 0; i < min; i++) sum += a[i] * b[i];
        return sum;
    }

    private static void L2Normalise(float[] vector)
    {
        var sumSq = 0f;
        for (var i = 0; i < vector.Length; i++) sumSq += vector[i] * vector[i];
        var norm = MathF.Sqrt(sumSq);
        if (norm <= 0f) return;
        for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
    }
}
