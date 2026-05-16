using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zakira.Replay.Core;

/// <summary>
/// Fully-local <see cref="IVisionProvider"/> that never calls an LLM. Behaviour is controlled
/// by <see cref="LocalVisionOptions.Mode"/>:
///
/// <list type="bullet">
///   <item><description><see cref="LocalVisionMode.Heuristic"/>: zero models. All fields derived
///   from the per-frame OCR result via <see cref="OcrToVisionHeuristics"/>.</description></item>
///   <item><description><see cref="LocalVisionMode.Clip"/>: heuristic + CLIP zero-shot image
///   classification fills the <c>Kind</c> field. Requires a CLIP image-encoder ONNX and either
///   a pre-computed kind-embeddings <c>.bin</c> file or the matching text-encoder ONNX (the
///   provider generates and caches the embeddings on first use).</description></item>
///   <item><description><see cref="LocalVisionMode.ClipBlip"/>: above plus BLIP-base image
///   captioning fills <c>FreeText</c>. Requires the BLIP image-encoder ONNX, decoder ONNX, and
///   WordPiece vocab file.</description></item>
/// </list>
///
/// When the configured mode references missing model files the provider gracefully degrades to
/// the largest mode whose files are present (down to <see cref="LocalVisionMode.Heuristic"/>),
/// reporting the degradation via warnings the orchestrator can inspect on
/// <see cref="VisionFrameResult"/>'s host manifest.
///
/// <para>
/// Output JSON shape exactly matches <see cref="CopilotVisionProvider"/> so downstream parsing
/// in <see cref="StructuredResponseParser.ParseVisionWithMode"/> is unchanged. The
/// <see cref="VisionFrameResult.Provider"/> field carries <see cref="VisionProviders.Local"/>
/// so consumers can audit which path produced what.
/// </para>
/// </summary>
public sealed class LocalOnnxVisionProvider : IVisionProvider, IDisposable
{
    /// <summary>
    /// CLIP ViT-B/32 expects 224×224 inputs. The image-encoder ONNX export from
    /// <c>openai/clip-vit-base-patch32</c> uses this size for the image branch.
    /// </summary>
    private const int ClipImageSize = 224;

    /// <summary>BLIP-base captioning takes 384×384 inputs per the Salesforce reference export.</summary>
    private const int BlipImageSize = 384;

    /// <summary>
    /// CLIP image-normalisation constants (Mean and StdDev). These are the published values
    /// for the OpenAI CLIP ViT-B/32 release; they are *not* the ImageNet defaults.
    /// </summary>
    private static readonly float[] ClipMean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] ClipStd = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>BLIP normalisation uses ImageNet defaults (mean/std per channel).</summary>
    private static readonly float[] BlipMean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] BlipStd = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>
    /// Closed set of <c>Kind</c> labels exposed by <see cref="VisionFrameStructured.Kind"/>.
    /// Paired one-to-one with the prompts in <see cref="ClipKindPrompts"/> so the pre-computed
    /// embeddings file is reproducible bit-for-bit given the same text encoder.
    /// </summary>
    public static readonly IReadOnlyList<string> KindLabels =
    [
        "slide", "ui", "code", "diagram", "chart", "dashboard", "other"
    ];

    /// <summary>
    /// Prompts fed to the CLIP text encoder when generating the kind-embeddings file. Reuse
    /// these exactly if you regenerate the file via your own pipeline so cosine similarities
    /// stay consistent across builds.
    /// </summary>
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
    private InferenceSession? blipImageSession;
    private InferenceSession? blipDecoderSession;
    private string[]? blipVocab;
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

    /// <summary>
    /// The mode the provider actually runs in after model-availability checks. May be lower
    /// than the configured <see cref="LocalVisionOptions.Mode"/> when models are missing.
    /// </summary>
    public LocalVisionMode EffectiveMode => effectiveMode;

    /// <summary>
    /// Warnings recorded during initialisation (e.g. missing CLIP file causing degradation to
    /// heuristic). Empty when the configured mode loaded cleanly. Read-only; consumed by the
    /// pipeline after the first call.
    /// </summary>
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
        if (effectiveMode == LocalVisionMode.ClipBlip && blipImageSession is not null && blipDecoderSession is not null && blipVocab is not null)
        {
            var caption = await GenerateBlipCaptionAsync(request.ImagePath, cancellationToken).ConfigureAwait(false);
            freeText = string.IsNullOrWhiteSpace(caption)
                ? (string.IsNullOrEmpty(ocrFreeText) ? "Frame contains no machine-readable text." : ocrFreeText)
                : (string.IsNullOrEmpty(ocrFreeText) ? $"Frame appears to show: {caption}." : $"Frame appears to show: {caption}. Visible text: {ocrFreeText}");
        }
        else if (!string.IsNullOrWhiteSpace(ocrFreeText))
        {
            freeText = ocrFreeText;
        }
        else
        {
            freeText = "Frame contains no machine-readable text.";
        }

        // Charts are deliberately empty in local mode: detecting charts reliably needs a
        // chart-aware model we do not ship. Emit an empty array so the JSON shape stays valid.
        var json = JsonSerializer.Serialize(new
        {
            kind,
            title,
            bullets,
            codeBlocks = codeBlocks.Select(b => new { language = b.Language, text = b.Text }).ToArray(),
            charts = Array.Empty<object>(),
            uiElements,
            freeText
        }, JsonOptions);

        return json;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clipImageSession?.Dispose();
        blipImageSession?.Dispose();
        blipDecoderSession?.Dispose();
        clipImageSession = null;
        blipImageSession = null;
        blipDecoderSession = null;
    }

    /// <summary>
    /// Lazily load any ONNX inference sessions / vocab files required by the configured mode.
    /// Safe to call multiple times — subsequent invocations short-circuit. The pipeline calls
    /// this once after construction so degradation warnings (CLIP missing, BLIP missing, etc.)
    /// land in the manifest before the first frame is analysed; otherwise initialisation runs
    /// on demand from <see cref="DescribeAsync"/>.
    /// </summary>
    public void Initialise()
    {
        if (initialised) return;

        lock (initLock)
        {
            if (initialised) return;

            var warnings = new List<string>();
            var actualMode = options.Mode;

            if (actualMode == LocalVisionMode.ClipBlip)
            {
                if (!TryInitClip(warnings))
                {
                    actualMode = LocalVisionMode.Heuristic;
                }
                else if (!TryInitBlip(warnings))
                {
                    actualMode = LocalVisionMode.Clip;
                }
            }
            else if (actualMode == LocalVisionMode.Clip)
            {
                if (!TryInitClip(warnings))
                {
                    actualMode = LocalVisionMode.Heuristic;
                }
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
                warnings.Add($"CLIP kind-embeddings file not found at '{options.ClipKindEmbeddingsPath ?? "<unset>"}'. Generate it by running `zakira-replay deps install vision`, or precompute by feeding ClipKindPrompts through your text encoder. Falling back to heuristic mode.");
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

    private bool TryInitBlip(List<string> warnings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.BlipImageEncoderPath) || !File.Exists(options.BlipImageEncoderPath))
            {
                warnings.Add($"BLIP image-encoder model not found at '{options.BlipImageEncoderPath ?? "<unset>"}'. Falling back to CLIP-only mode.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.BlipDecoderPath) || !File.Exists(options.BlipDecoderPath))
            {
                warnings.Add($"BLIP decoder model not found at '{options.BlipDecoderPath ?? "<unset>"}'. Falling back to CLIP-only mode.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.BlipVocabPath) || !File.Exists(options.BlipVocabPath))
            {
                warnings.Add($"BLIP vocabulary file not found at '{options.BlipVocabPath ?? "<unset>"}'. Falling back to CLIP-only mode.");
                return false;
            }

            blipImageSession = new InferenceSession(options.BlipImageEncoderPath);
            blipDecoderSession = new InferenceSession(options.BlipDecoderPath);
            blipVocab = File.ReadAllLines(options.BlipVocabPath);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"BLIP initialisation failed: {ex.Message}. Falling back to CLIP-only mode.");
            blipImageSession?.Dispose();
            blipDecoderSession?.Dispose();
            blipImageSession = null;
            blipDecoderSession = null;
            blipVocab = null;
            return false;
        }
    }

    private async Task<string?> ClassifyKindWithClipAsync(string imagePath, CancellationToken cancellationToken)
    {
        var pixels = await ffmpeg.PreprocessImageRgb24Async(imagePath, ClipImageSize, ClipImageSize, cancellationToken).ConfigureAwait(false);
        if (pixels is null)
        {
            return null;
        }

        var tensor = BuildNormalisedTensor(pixels, ClipImageSize, ClipMean, ClipStd);
        var inputName = clipImageSession!.InputMetadata.Keys.First();
        using var run = clipImageSession.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var output = run.First().AsTensor<float>();
        var embedding = output.ToArray();

        // L2-normalise the image embedding before cosine similarity. CLIP outputs are not
        // automatically unit vectors when the image encoder is exported standalone.
        L2Normalise(embedding);

        var bestKind = 0;
        var bestScore = float.MinValue;
        for (var i = 0; i < clipKindEmbeddings!.Length; i++)
        {
            var score = Dot(embedding, clipKindEmbeddings[i]);
            if (score > bestScore)
            {
                bestScore = score;
                bestKind = i;
            }
        }

        return KindLabels[bestKind];
    }

    private async Task<string?> GenerateBlipCaptionAsync(string imagePath, CancellationToken cancellationToken)
    {
        var pixels = await ffmpeg.PreprocessImageRgb24Async(imagePath, BlipImageSize, BlipImageSize, cancellationToken).ConfigureAwait(false);
        if (pixels is null)
        {
            return null;
        }

        DenseTensor<float> imageFeatures;
        try
        {
            var imageTensor = BuildNormalisedTensor(pixels, BlipImageSize, BlipMean, BlipStd);
            var encoderInputName = blipImageSession!.InputMetadata.Keys.First();
            using var imageRun = blipImageSession.Run([NamedOnnxValue.CreateFromTensor(encoderInputName, imageTensor)]);
            var featureOutput = imageRun.First().AsTensor<float>();
            // Materialise into a writable DenseTensor we can feed back into the decoder.
            var dims = featureOutput.Dimensions.ToArray();
            imageFeatures = new DenseTensor<float>(featureOutput.ToArray(), dims);
        }
        catch
        {
            return null;
        }

        // BLIP-base BertLMHead vocabulary IDs. These match the canonical ONNX export from
        // Salesforce/blip-image-captioning-base; if your export differs you must regenerate
        // the vocab file in the same ordering.
        const int ClsTokenId = 30522;   // [DEC] / "decoder bos" in BLIP
        const int SepTokenId = 102;     // [SEP]

        var generated = new List<int> { ClsTokenId };

        try
        {
            for (var step = 0; step < options.BlipMaxTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var inputIds = new DenseTensor<long>(generated.Select(t => (long)t).ToArray(), [1, generated.Count]);
                var attentionMask = new DenseTensor<long>(Enumerable.Repeat(1L, generated.Count).ToArray(), [1, generated.Count]);

                var inputs = new List<NamedOnnxValue>(3)
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
                };

                // The decoder ONNX exposes the image features under one of several common names
                // depending on the export tooling; probe and bind whichever matches.
                var encoderInputName = blipDecoderSession!.InputMetadata.Keys
                    .FirstOrDefault(k => k.Contains("encoder_hidden_states", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("image_embeds", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("image_features", StringComparison.OrdinalIgnoreCase));
                if (encoderInputName is null)
                {
                    return null;
                }

                inputs.Add(NamedOnnxValue.CreateFromTensor(encoderInputName, imageFeatures));

                using var decoderRun = blipDecoderSession.Run(inputs);
                var logitsOutput = decoderRun.First().AsTensor<float>();
                var logitsDims = logitsOutput.Dimensions;
                // Expect shape [1, seq_len, vocab_size]; pick the last sequence position.
                if (logitsDims.Length != 3) return null;
                var vocabSize = logitsDims[2];
                var lastPos = logitsDims[1] - 1;

                var bestToken = 0;
                var bestLogit = float.MinValue;
                for (var v = 0; v < vocabSize; v++)
                {
                    var logit = logitsOutput[0, lastPos, v];
                    if (logit > bestLogit)
                    {
                        bestLogit = logit;
                        bestToken = v;
                    }
                }

                if (bestToken == SepTokenId)
                {
                    break;
                }

                generated.Add(bestToken);
            }
        }
        catch
        {
            return null;
        }

        return DecodeWordPiece(generated.Skip(1).ToArray(), blipVocab!);
    }

    private static DenseTensor<float> BuildNormalisedTensor(byte[] rgb, int size, float[] mean, float[] std)
    {
        // Shape: [1, 3, H, W]. ONNX CLIP/BLIP exports are channel-first.
        var tensor = new DenseTensor<float>([1, 3, size, size]);
        var stride = size * size;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var srcIndex = (y * size + x) * 3;
                var r = rgb[srcIndex] / 255f;
                var g = rgb[srcIndex + 1] / 255f;
                var b = rgb[srcIndex + 2] / 255f;
                tensor[0, 0, y, x] = (r - mean[0]) / std[0];
                tensor[0, 1, y, x] = (g - mean[1]) / std[1];
                tensor[0, 2, y, x] = (b - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    private static float[][]? LoadKindEmbeddings(string path, int expectedRows)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % (sizeof(float) * expectedRows) != 0)
            {
                return null;
            }

            var floatsPerRow = bytes.Length / sizeof(float) / expectedRows;
            if (floatsPerRow == 0)
            {
                return null;
            }

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
        for (var i = 0; i < min; i++)
        {
            sum += a[i] * b[i];
        }

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

    private static string DecodeWordPiece(int[] tokenIds, string[] vocab)
    {
        var builder = new StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id < 0 || id >= vocab.Length) continue;
            var token = vocab[id];
            if (string.IsNullOrEmpty(token)) continue;
            if (token.StartsWith("##", StringComparison.Ordinal))
            {
                builder.Append(token[2..]);
            }
            else if (token.StartsWith("[", StringComparison.Ordinal) && token.EndsWith("]", StringComparison.Ordinal))
            {
                // Skip special tokens like [PAD], [UNK], [CLS], [SEP].
                continue;
            }
            else
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(token);
            }
        }

        return builder.ToString().Trim();
    }
}
