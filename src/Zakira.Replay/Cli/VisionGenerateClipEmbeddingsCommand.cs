using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Zakira.Replay.Core;

namespace Zakira.Replay.Cli;

/// <summary>
/// Implementation of <c>zakira-replay vision generate-clip-embeddings</c>: takes a CLIP
/// text-encoder ONNX file plus the CLIP BPE tokenizer files (vocab.json + merges.txt) and
/// writes a 7×512 float32 binary that <see cref="LocalOnnxVisionProvider"/> consumes for
/// zero-shot kind classification. The output filename is
/// <see cref="LocalVisionOptions.ClipKindEmbeddingsFile"/> and matches the canonical layout
/// under <c>&lt;portable&gt;/models/vision/</c>.
/// </summary>
internal static class VisionGenerateClipEmbeddingsCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var parsed = CommandOptions.Parse(args);
        var config = new ConfigStore().Load();
        var options = LocalVisionOptions.Resolve(config);

        var textEncoderPath = parsed.Get("text-encoder") ?? options.ClipTextEncoderPath;
        var vocabPath = parsed.Get("vocab") ?? Path.Combine(options.ModelDirectory, "clip-vocab.json");
        var mergesPath = parsed.Get("merges") ?? Path.Combine(options.ModelDirectory, "clip-merges.txt");
        var outputPath = parsed.Get("out") ?? options.ClipKindEmbeddingsPath ?? Path.Combine(options.ModelDirectory, LocalVisionOptions.ClipKindEmbeddingsFile);

        if (string.IsNullOrWhiteSpace(textEncoderPath) || !File.Exists(textEncoderPath))
        {
            throw new ReplayException(
                $"CLIP text-encoder ONNX not found at '{textEncoderPath ?? "<unset>"}'. " +
                "Run `zakira-replay deps install vision --mode clip` first, or pass `--text-encoder <path>` explicitly.");
        }

        if (!File.Exists(vocabPath))
        {
            throw new ReplayException(
                $"CLIP tokenizer vocab.json not found at '{vocabPath}'. " +
                "Run `zakira-replay deps install vision --mode clip` first, or pass `--vocab <path>` explicitly.");
        }

        if (!File.Exists(mergesPath))
        {
            throw new ReplayException(
                $"CLIP tokenizer merges.txt not found at '{mergesPath}'. " +
                "Run `zakira-replay deps install vision --mode clip` first, or pass `--merges <path>` explicitly.");
        }

        stdout.WriteLine($"Loading tokenizer from {vocabPath} and {mergesPath}...");
        var tokenizer = ClipBpeTokenizer.FromFiles(vocabPath, mergesPath);

        stdout.WriteLine($"Loading CLIP text encoder from {textEncoderPath}...");
        using var session = new InferenceSession(textEncoderPath);

        var prompts = LocalOnnxVisionProvider.ClipKindPrompts;
        var labels = LocalOnnxVisionProvider.KindLabels;
        if (prompts.Count != labels.Count)
        {
            throw new ReplayException($"Internal inconsistency: {prompts.Count} prompts vs {labels.Count} labels.");
        }

        // Discover embedding dimension by running the first prompt through the model. CLIP
        // ViT-B/32 ships 512-d embeddings; we don't hardcode it so future ViT-L/14 swaps work.
        stdout.WriteLine("Discovering text-embedding dimension via test inference...");
        var firstTokens = tokenizer.Tokenize(prompts[0]);
        var firstEmbedding = await RunTextEncoderAsync(session, firstTokens, cancellationToken).ConfigureAwait(false);
        L2Normalise(firstEmbedding);
        stdout.WriteLine($"  Embedding dimension: {firstEmbedding.Length}");

        var allEmbeddings = new float[prompts.Count * firstEmbedding.Length];
        Buffer.BlockCopy(firstEmbedding, 0, allEmbeddings, 0, firstEmbedding.Length * sizeof(float));
        stdout.WriteLine($"  [{labels[0]}] '{prompts[0]}' -> {firstEmbedding.Length} floats");

        for (var i = 1; i < prompts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = tokenizer.Tokenize(prompts[i]);
            var embedding = await RunTextEncoderAsync(session, tokens, cancellationToken).ConfigureAwait(false);
            if (embedding.Length != firstEmbedding.Length)
            {
                throw new ReplayException($"Embedding dimension changed mid-batch: {firstEmbedding.Length} -> {embedding.Length}.");
            }
            L2Normalise(embedding);
            Buffer.BlockCopy(embedding, 0, allEmbeddings, i * embedding.Length * sizeof(float), embedding.Length * sizeof(float));
            stdout.WriteLine($"  [{labels[i]}] '{prompts[i]}'");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var bytes = new byte[allEmbeddings.Length * sizeof(float)];
        Buffer.BlockCopy(allEmbeddings, 0, bytes, 0, bytes.Length);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);

        stdout.WriteLine();
        stdout.WriteLine($"Wrote {prompts.Count} embeddings ({firstEmbedding.Length} floats each, {bytes.Length} bytes total) to:");
        stdout.WriteLine($"  {outputPath}");
        return 0;
    }

    private static Task<float[]> RunTextEncoderAsync(InferenceSession session, int[] tokens, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = new long[tokens.Length];
            var mask = new long[tokens.Length];
            for (var i = 0; i < tokens.Length; i++)
            {
                ids[i] = tokens[i];
                // CLIP's attention mask is 1 for every position up to the EOS token, 0 after.
                // The pooled output uses the EOS-position embedding regardless, so a fully-1
                // mask is safe; matching the HF transformers reference implementation.
                mask[i] = 1;
            }

            var idsTensor = new DenseTensor<long>(ids, [1, tokens.Length]);
            var maskTensor = new DenseTensor<long>(mask, [1, tokens.Length]);

            // The Xenova export exposes input_ids + attention_mask. Probe to handle exports
            // that only expose input_ids (e.g. ONNX Optimum minimal export).
            var inputNames = session.InputMetadata.Keys.ToArray();
            var idsName = inputNames.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase)) ?? inputNames[0];
            var maskName = inputNames.FirstOrDefault(n => n.Contains("attention_mask", StringComparison.OrdinalIgnoreCase));

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(idsName, idsTensor) };
            if (maskName is not null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(maskName, maskTensor));
            }

            using var run = session.Run(inputs);
            // Prefer the pooler_output (already pooled to [1, hidden_size]); fall back to the
            // EOS-position slice of last_hidden_state.
            var pooler = run.FirstOrDefault(r => r.Name.Contains("pooler", StringComparison.OrdinalIgnoreCase)
                                                || r.Name.Contains("text_embeds", StringComparison.OrdinalIgnoreCase)
                                                || r.Name.Contains("pooled", StringComparison.OrdinalIgnoreCase));
            if (pooler is not null)
            {
                var tensor = pooler.AsTensor<float>();
                return tensor.ToArray();
            }

            var hidden = run.First().AsTensor<float>();
            var dims = hidden.Dimensions;
            if (dims.Length == 3)
            {
                // [1, seq, hidden]. Find the EOS position (the highest token ID is the EOS).
                var eosIndex = Array.LastIndexOf(tokens, ClipBpeTokenizer.EosTokenId);
                if (eosIndex < 0)
                {
                    eosIndex = tokens.Length - 1;
                }

                var hiddenSize = dims[2];
                var result = new float[hiddenSize];
                for (var h = 0; h < hiddenSize; h++)
                {
                    result[h] = hidden[0, eosIndex, h];
                }

                return result;
            }

            if (dims.Length == 2)
            {
                return hidden.ToArray();
            }

            throw new ReplayException($"CLIP text encoder produced an unexpected output rank ({dims.Length}).");
        }, cancellationToken);
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

/// <summary>
/// Hand-rolled CLIP BPE tokenizer covering the ASCII subset that the
/// <see cref="LocalOnnxVisionProvider.ClipKindPrompts"/> uses. Loads vocab.json + merges.txt
/// from disk in the same format the Xenova / Hugging Face <c>clip-vit-base-patch32</c> export
/// publishes. Matches the upstream CLIP tokenizer for ASCII input; non-ASCII bytes would need
/// the GPT-2 bytes-to-unicode encoding, which we omit (the prompts are pure ASCII so this is
/// safe). Output length is padded / truncated to 77 tokens.
/// </summary>
public sealed class ClipBpeTokenizer
{
    /// <summary>CLIP convention: BOS token id is 49406 in the 49408-token vocabulary.</summary>
    public const int BosTokenId = 49406;

    /// <summary>CLIP convention: EOS token id is 49407, also used as the padding token.</summary>
    public const int EosTokenId = 49407;

    /// <summary>Standard CLIP context length.</summary>
    public const int MaxSequenceLength = 77;

    private readonly Dictionary<string, int> vocab;
    private readonly Dictionary<(string A, string B), int> mergeRanks;

    private ClipBpeTokenizer(Dictionary<string, int> vocab, Dictionary<(string A, string B), int> mergeRanks)
    {
        this.vocab = vocab;
        this.mergeRanks = mergeRanks;
    }

    public static ClipBpeTokenizer FromFiles(string vocabJsonPath, string mergesTxtPath)
    {
        var vocabText = File.ReadAllText(vocabJsonPath);
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabText)
            ?? throw new ReplayException($"clip-vocab.json at '{vocabJsonPath}' could not be parsed as a token -> id map.");

        var mergeRanks = new Dictionary<(string A, string B), int>();
        var rank = 0;
        foreach (var raw in File.ReadAllLines(mergesTxtPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            mergeRanks[(parts[0], parts[1])] = rank++;
        }

        if (mergeRanks.Count == 0)
        {
            throw new ReplayException($"clip-merges.txt at '{mergesTxtPath}' contained no merge rules.");
        }

        return new ClipBpeTokenizer(vocab, mergeRanks);
    }

    /// <summary>
    /// Tokenize the given text into CLIP token IDs. Prepends BOS, appends EOS, pads with EOS
    /// up to <see cref="MaxSequenceLength"/>. Returns exactly 77 token IDs.
    /// </summary>
    public int[] Tokenize(string text)
    {
        var ids = new List<int>(MaxSequenceLength) { BosTokenId };

        var lowered = text.Trim().ToLowerInvariant();
        var words = lowered.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (ids.Count >= MaxSequenceLength - 1) break;
            foreach (var piece in BpeEncodeWord(word))
            {
                if (vocab.TryGetValue(piece, out var id))
                {
                    ids.Add(id);
                    if (ids.Count >= MaxSequenceLength - 1) break;
                }
                // Silently drop pieces missing from the vocab. For pure-ASCII prompts this
                // shouldn't happen with the canonical Xenova vocab.
            }
        }

        ids.Add(EosTokenId);
        while (ids.Count < MaxSequenceLength)
        {
            ids.Add(EosTokenId);
        }

        return ids.ToArray();
    }

    private IReadOnlyList<string> BpeEncodeWord(string word)
    {
        if (word.Length == 0)
        {
            return Array.Empty<string>();
        }

        // CLIP convention: append `</w>` to the final character to mark end-of-word.
        var pieces = new List<string>(word.Length);
        for (var i = 0; i < word.Length - 1; i++)
        {
            pieces.Add(word[i].ToString(CultureInfo.InvariantCulture));
        }
        pieces.Add(word[^1].ToString(CultureInfo.InvariantCulture) + "</w>");

        // Greedy merge by lowest rank until no merges apply.
        while (pieces.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < pieces.Count - 1; i++)
            {
                if (mergeRanks.TryGetValue((pieces[i], pieces[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) break;

            pieces[bestIndex] = pieces[bestIndex] + pieces[bestIndex + 1];
            pieces.RemoveAt(bestIndex + 1);
        }

        return pieces;
    }
}
