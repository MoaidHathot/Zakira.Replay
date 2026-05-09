using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zakira.Replay.Core;

public sealed partial class OnnxSearchEmbeddingProvider : ISearchEmbeddingProvider, IDisposable
{
    private readonly InferenceSession session;
    private readonly WordPieceTokenizer tokenizer;
    private readonly int maxSequenceLength;
    private readonly int? configuredDimensions;

    public OnnxSearchEmbeddingProvider(string modelPath, string vocabularyPath, int maxSequenceLength = 256, int? embeddingDimensions = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new ReplayException($"ONNX embedding model was not found: {modelPath}");
        }

        if (!File.Exists(vocabularyPath))
        {
            throw new ReplayException($"ONNX embedding vocabulary was not found: {vocabularyPath}");
        }

        session = new InferenceSession(modelPath);
        tokenizer = WordPieceTokenizer.Load(vocabularyPath);
        this.maxSequenceLength = Math.Max(8, maxSequenceLength);
        configuredDimensions = embeddingDimensions;
    }

    public string Name => "onnx";

    public int Dimensions => configuredDimensions ?? 0;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = tokenizer.Encode(text, maxSequenceLength);
        var inputs = BuildInputs(encoded);
        using var results = session.Run(inputs);
        var embedding = ExtractEmbedding(results, encoded.AttentionMask);
        return Task.FromResult(Normalize(embedding));
    }

    public void Dispose()
    {
        session.Dispose();
    }

    private List<NamedOnnxValue> BuildInputs(EncodedText encoded)
    {
        var inputs = new List<NamedOnnxValue>();
        foreach (var (name, metadata) in session.InputMetadata)
        {
            var source = name switch
            {
                "input_ids" => encoded.InputIds,
                "attention_mask" => encoded.AttentionMask,
                "token_type_ids" => encoded.TokenTypeIds,
                _ when name.Contains("input_ids", StringComparison.OrdinalIgnoreCase) => encoded.InputIds,
                _ when name.Contains("attention", StringComparison.OrdinalIgnoreCase) => encoded.AttentionMask,
                _ when name.Contains("token_type", StringComparison.OrdinalIgnoreCase) || name.Contains("segment", StringComparison.OrdinalIgnoreCase) => encoded.TokenTypeIds,
                _ => null
            };

            if (source is null)
            {
                continue;
            }

            if (metadata.ElementDataType == TensorElementType.Int32)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(source.Select(value => (int)value).ToArray(), [1, source.Length])));
            }
            else
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(source, [1, source.Length])));
            }
        }

        if (inputs.Count == 0)
        {
            throw new ReplayException("ONNX embedding model did not expose supported text inputs such as input_ids and attention_mask.");
        }

        return inputs;
    }

    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, IReadOnlyList<long> attentionMask)
    {
        var preferred = results.FirstOrDefault(result =>
            result.Name.Equals("sentence_embedding", StringComparison.OrdinalIgnoreCase)
            || result.Name.Equals("pooler_output", StringComparison.OrdinalIgnoreCase)
            || result.Name.Equals("embeddings", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            return FlattenFirstVector(preferred.AsTensor<float>());
        }

        var firstTensor = results.Select(result => result.AsTensor<float>()).FirstOrDefault();
        if (firstTensor is null)
        {
            throw new ReplayException("ONNX embedding model did not return a float tensor.");
        }

        if (firstTensor.Dimensions.Length == 3)
        {
            return MeanPool(firstTensor, attentionMask);
        }

        return FlattenFirstVector(firstTensor);
    }

    private static float[] FlattenFirstVector(Tensor<float> tensor)
    {
        if (tensor.Dimensions.Length == 1)
        {
            return tensor.ToArray();
        }

        var dimensions = tensor.Dimensions;
        var vectorLength = dimensions[^1];
        var vector = new float[vectorLength];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = tensor.GetValue(i);
        }

        return vector;
    }

    private static float[] MeanPool(Tensor<float> tensor, IReadOnlyList<long> attentionMask)
    {
        var sequenceLength = tensor.Dimensions[1];
        var hiddenSize = tensor.Dimensions[2];
        var pooled = new float[hiddenSize];
        var includedTokens = 0;
        for (var token = 0; token < sequenceLength; token++)
        {
            if (token >= attentionMask.Count || attentionMask[token] == 0)
            {
                continue;
            }

            includedTokens++;
            for (var hidden = 0; hidden < hiddenSize; hidden++)
            {
                pooled[hidden] += tensor[0, token, hidden];
            }
        }

        if (includedTokens > 0)
        {
            for (var hidden = 0; hidden < hiddenSize; hidden++)
            {
                pooled[hidden] /= includedTokens;
            }
        }

        return pooled;
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm <= 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    private sealed class WordPieceTokenizer
    {
        private readonly Dictionary<string, int> vocabulary;
        private readonly int padId;
        private readonly int unknownId;
        private readonly int clsId;
        private readonly int sepId;

        private WordPieceTokenizer(Dictionary<string, int> vocabulary)
        {
            this.vocabulary = vocabulary;
            padId = GetId("[PAD]", 0);
            unknownId = GetId("[UNK]", 1);
            clsId = GetId("[CLS]", unknownId);
            sepId = GetId("[SEP]", unknownId);
        }

        public static WordPieceTokenizer Load(string vocabularyPath)
        {
            var vocabulary = File.ReadLines(vocabularyPath)
                .Select((token, index) => new { Token = token.Trim(), Index = index })
                .Where(item => item.Token.Length > 0)
                .ToDictionary(item => item.Token, item => item.Index, StringComparer.Ordinal);
            return new WordPieceTokenizer(vocabulary);
        }

        public EncodedText Encode(string text, int maxSequenceLength)
        {
            var ids = new List<long> { clsId };
            foreach (var token in BasicTokenRegex().Matches(text.ToLowerInvariant()).Select(match => match.Value))
            {
                foreach (var piece in TokenizeWord(token))
                {
                    ids.Add(piece);
                    if (ids.Count >= maxSequenceLength - 1)
                    {
                        break;
                    }
                }

                if (ids.Count >= maxSequenceLength - 1)
                {
                    break;
                }
            }

            ids.Add(sepId);
            var attention = Enumerable.Repeat(1L, ids.Count).ToList();
            while (ids.Count < maxSequenceLength)
            {
                ids.Add(padId);
                attention.Add(0);
            }

            return new EncodedText(ids.ToArray(), attention.ToArray(), new long[maxSequenceLength]);
        }

        private IEnumerable<long> TokenizeWord(string token)
        {
            if (vocabulary.TryGetValue(token, out var tokenId))
            {
                yield return tokenId;
                yield break;
            }

            if (token.Length > 100)
            {
                yield return unknownId;
                yield break;
            }

            var pieces = new List<int>();
            var start = 0;
            while (start < token.Length)
            {
                var end = token.Length;
                int? current = null;
                while (start < end)
                {
                    var substring = token[start..end];
                    if (start > 0)
                    {
                        substring = "##" + substring;
                    }

                    if (vocabulary.TryGetValue(substring, out var pieceId))
                    {
                        current = pieceId;
                        break;
                    }

                    end--;
                }

                if (!current.HasValue)
                {
                    yield return unknownId;
                    yield break;
                }

                pieces.Add(current.Value);
                start = end;
            }

            foreach (var piece in pieces)
            {
                yield return piece;
            }
        }

        private int GetId(string token, int fallback) => vocabulary.GetValueOrDefault(token, fallback);
    }

    private sealed record EncodedText(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);

    [GeneratedRegex("[a-z0-9]+|[^\\s]")]
    private static partial Regex BasicTokenRegex();
}
