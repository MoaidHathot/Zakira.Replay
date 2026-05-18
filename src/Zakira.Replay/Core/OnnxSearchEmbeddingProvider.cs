using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Zakira.Replay.Core;

/// <summary>
/// ONNX-backed dense-vector embedding provider for the <c>sqlite-onnx</c> search backend.
///
/// 0.10.0 generalises the provider so it can drive three retrieval model families:
/// <list type="bullet">
///   <item><description><c>bert</c> — legacy / generic BERT WordPiece (the historical
///     <c>all-MiniLM-L6-v2</c> path). Mean pooling, no prefix.</description></item>
///   <item><description><c>bge</c> — BAAI BGE (<c>bge-small-en-v1.5</c>, default for 0.10.0)
///     and architecturally-identical Snowflake arctic-embed-* models. CLS pooling, query-side
///     prefix only.</description></item>
///   <item><description><c>e5</c> — Microsoft/intfloat E5 family (<c>multilingual-e5-small</c>,
///     <c>e5-small-v2</c>). Mean pooling, symmetric query+passage prefixes.</description></item>
/// </list>
///
/// The hand-rolled WordPiece tokenizer that shipped through 0.9.x has been retired in
/// favour of <see cref="BertTokenizer"/> from <c>Microsoft.ML.Tokenizers</c>, which handles
/// the BERT vocab.txt path natively and ships shared infrastructure (basic tokenization,
/// special-token handling, CJK splitting, Unicode normalization) we used to maintain in
/// ~110 lines of bespoke code.
///
/// SentencePiece tokenizer support for multilingual-e5 is provided through
/// <see cref="SentencePieceTokenizer"/>; the constructor picks the right tokenizer family
/// from the supplied <see cref="SearchEmbeddingModelKind"/> and the vocabulary/tokenizer
/// file extension.
/// </summary>
public sealed class OnnxSearchEmbeddingProvider : ISearchEmbeddingProvider, IDisposable
{
    private const string BgeQueryPrefix = "Represent this sentence for searching relevant passages: ";
    private const string E5QueryPrefix = "query: ";
    private const string E5PassagePrefix = "passage: ";

    private readonly InferenceSession session;
    private readonly Tokenizer tokenizer;
    private readonly int maxSequenceLength;
    private readonly int? configuredDimensions;
    private readonly SearchEmbeddingModelKind modelKind;
    private readonly string modelId;
    private readonly int padId;
    private readonly int clsId;
    private readonly int sepId;
    private readonly int unknownId;

    public OnnxSearchEmbeddingProvider(
        string modelPath,
        string tokenizerPath,
        int maxSequenceLength = 256,
        int? embeddingDimensions = null,
        SearchEmbeddingModelKind modelKind = SearchEmbeddingModelKind.Bert,
        string? modelId = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new ReplayException($"ONNX embedding model was not found: {modelPath}");
        }

        if (!File.Exists(tokenizerPath))
        {
            throw new ReplayException($"ONNX embedding tokenizer file was not found: {tokenizerPath}");
        }

        session = new InferenceSession(modelPath);
        (tokenizer, padId, clsId, sepId, unknownId) = LoadTokenizer(tokenizerPath, modelKind);
        this.maxSequenceLength = Math.Max(8, maxSequenceLength);
        this.modelKind = modelKind;
        this.modelId = string.IsNullOrWhiteSpace(modelId)
            ? Path.GetFileName(Path.GetDirectoryName(modelPath) ?? string.Empty)
            : modelId!;
        configuredDimensions = embeddingDimensions;
    }

    public string Name => "onnx";

    public string ModelId => modelId;

    public string ModelKind => modelKind.ToString().ToLowerInvariant();

    public int Dimensions => configuredDimensions ?? 0;

    /// <summary>
    /// Legacy entry point kept for the in-process tests and any third-party
    /// <see cref="ISearchEmbeddingProvider"/> consumer that pre-dates 0.10.0. New callers
    /// should use the side-aware overload so asymmetric models (bge / e5) apply the right
    /// prefix.
    /// </summary>
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        return EmbedAsync(text, SearchEmbeddingSide.Document, cancellationToken);
    }

    public Task<float[]> EmbedAsync(string text, SearchEmbeddingSide side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var withPrefix = ApplyPrefix(text, side);
        var encoded = EncodeWithPaddingAndSpecialTokens(withPrefix);
        var inputs = BuildInputs(encoded);
        using var results = session.Run(inputs);
        var embedding = ExtractEmbedding(results, encoded.AttentionMask, modelKind);
        return Task.FromResult(Normalize(embedding));
    }

    public void Dispose()
    {
        session.Dispose();
    }

    private string ApplyPrefix(string text, SearchEmbeddingSide side)
    {
        return modelKind switch
        {
            SearchEmbeddingModelKind.Bge when side == SearchEmbeddingSide.Query => BgeQueryPrefix + text,
            SearchEmbeddingModelKind.E5 when side == SearchEmbeddingSide.Query => E5QueryPrefix + text,
            SearchEmbeddingModelKind.E5 when side == SearchEmbeddingSide.Document => E5PassagePrefix + text,
            _ => text
        };
    }

    private static (Tokenizer Tokenizer, int PadId, int ClsId, int SepId, int UnknownId) LoadTokenizer(string tokenizerPath, SearchEmbeddingModelKind modelKind)
    {
        var extension = Path.GetExtension(tokenizerPath).ToLowerInvariant();
        // SentencePiece BPE for XLM-RoBERTa (multilingual-e5-small) ships as a binary .model
        // file; the BERT-family models ship a plain-text WordPiece vocab.txt. The kind hint
        // is a fallback for users who symlink files with non-canonical names.
        if (extension == ".model" || (modelKind == SearchEmbeddingModelKind.E5 && extension != ".txt"))
        {
            using var stream = File.OpenRead(tokenizerPath);
            // XLM-R adds <s>/</s> as BOS/EOS at encode time; we control specials ourselves so
            // we pass addBeginningOfSentence=false, addEndOfSentence=false.
            var sp = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
            // Canonical XLM-R ids: <s>=0, <pad>=1, </s>=2, <unk>=3. Look them up in SpecialTokens
            // when available (works for the upstream SentencePiece model); otherwise fall back
            // to the well-known ids.
            return (
                sp,
                sp.SpecialTokens?.TryGetValue("<pad>", out var pad) == true ? pad : 1,
                sp.BeginningOfSentenceId,
                sp.EndOfSentenceId,
                sp.UnknownId);
        }

        var options = new BertOptions
        {
            // Generic-BERT and the BGE family ship uncased vocabularies; arctic-embed-s ships
            // cased. BertTokenizer auto-respects the casing in `vocab.txt` when
            // LowerCaseBeforeTokenization defaults to true but the vocab contains only
            // lowercase entries; we set it explicitly to match upstream sentence-transformers
            // behaviour.
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true
        };
        var bert = BertTokenizer.Create(tokenizerPath, options);
        return (bert, bert.PaddingTokenId, bert.ClassificationTokenId, bert.SeparatorTokenId, bert.UnknownTokenId);
    }

    private static int? ResolveSpecialId(SentencePieceTokenizer tokenizer, string token)
    {
        if (tokenizer.SpecialTokens is not null && tokenizer.SpecialTokens.TryGetValue(token, out var id))
        {
            return id;
        }
        return null;
    }

    private EncodedText EncodeWithPaddingAndSpecialTokens(string text)
    {
        // Reserve two slots for CLS+SEP / BOS+EOS so the model still gets a well-formed
        // sequence even at the truncation boundary.
        var contentBudget = maxSequenceLength - 2;
        IReadOnlyList<int> rawIds = tokenizer switch
        {
            BertTokenizer bt => bt.EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true),
            // SentencePieceTokenizer signature is (text, addBOS, addEOS, considerNormalization, considerPreTokenization).
            // We add specials ourselves to keep a uniform CLS/SEP-style layout downstream.
            SentencePieceTokenizer sp => sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false, considerNormalization: true, considerPreTokenization: false),
            _ => tokenizer.EncodeToIds(text)
        };
        var truncated = rawIds.Count > contentBudget ? rawIds.Take(contentBudget).ToList() : new List<int>(rawIds);

        var ids = new List<long>(maxSequenceLength) { clsId };
        ids.AddRange(truncated.Select(id => (long)id));
        ids.Add(sepId);

        var attention = Enumerable.Repeat(1L, ids.Count).ToList();
        while (ids.Count < maxSequenceLength)
        {
            ids.Add(padId);
            attention.Add(0L);
        }

        // BERT (MiniLM, BGE, arctic) feeds token_type_ids of zeros; XLM-R doesn't use them.
        // We always allocate a zeroed buffer and let BuildInputs decide which inputs the
        // model actually consumes via its declared input metadata.
        return new EncodedText(ids.ToArray(), attention.ToArray(), new long[maxSequenceLength]);
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

    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, IReadOnlyList<long> attentionMask, SearchEmbeddingModelKind modelKind)
    {
        // Some ONNX exports expose a pre-pooled tensor directly (`sentence_embedding`,
        // `pooler_output`, `embeddings`). Honour that fast path regardless of declared
        // model kind because the pooling is already baked in.
        var preferred = results.FirstOrDefault(result =>
            result.Name.Equals("sentence_embedding", StringComparison.OrdinalIgnoreCase)
            || result.Name.Equals("pooler_output", StringComparison.OrdinalIgnoreCase)
            || result.Name.Equals("embeddings", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            return FlattenFirstVector(preferred.AsTensor<float>());
        }

        // last_hidden_state branch — choose pooling based on model kind.
        var hiddenState = results.Select(result => result.AsTensor<float>()).FirstOrDefault()
            ?? throw new ReplayException("ONNX embedding model did not return a float tensor.");

        if (hiddenState.Dimensions.Length != 3)
        {
            return FlattenFirstVector(hiddenState);
        }

        // BGE-family (and Snowflake arctic-embed-* which shares the architecture) trains a
        // CLS-pooled vector; mean-pooling those embeddings degrades retrieval quality
        // measurably. E5 and generic BERT/MiniLM use mean pooling over the attention mask.
        return modelKind switch
        {
            SearchEmbeddingModelKind.Bge => ClsPool(hiddenState),
            _ => MeanPool(hiddenState, attentionMask)
        };
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

    private static float[] ClsPool(Tensor<float> tensor)
    {
        var hiddenSize = tensor.Dimensions[2];
        var pooled = new float[hiddenSize];
        for (var hidden = 0; hidden < hiddenSize; hidden++)
        {
            pooled[hidden] = tensor[0, 0, hidden];
        }
        return pooled;
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

    private sealed record EncodedText(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);

    /// <summary>
    /// Resolves the canonical <see cref="SearchEmbeddingModelKind"/> for a model id. Used by
    /// the installer / config glue so a user who sets <c>search.onnx.model = "bge-small-en-v1.5"</c>
    /// gets the right embedding scheme automatically without having to set
    /// <c>search.onnx.modelKind</c> as well.
    /// </summary>
    public static SearchEmbeddingModelKind ResolveKind(string? modelId, string? explicitKind = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            return ParseKind(explicitKind);
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return SearchEmbeddingModelKind.Bert;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        if (normalized.Contains("bge", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("arctic-embed", StringComparison.OrdinalIgnoreCase))
        {
            return SearchEmbeddingModelKind.Bge;
        }
        if (normalized.StartsWith("e5-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("multilingual-e5", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/e5-", StringComparison.OrdinalIgnoreCase))
        {
            return SearchEmbeddingModelKind.E5;
        }

        return SearchEmbeddingModelKind.Bert;
    }

    public static SearchEmbeddingModelKind ParseKind(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "bert" or "minilm" or "all-minilm" or "all-minilm-l6-v2" or "default" => SearchEmbeddingModelKind.Bert,
            "bge" or "bge-small" or "bge-small-en-v1.5" or "arctic" or "arctic-embed-s" or "snowflake-arctic-embed-s" or "snowflake" => SearchEmbeddingModelKind.Bge,
            "e5" or "e5-small" or "multilingual-e5" or "multilingual-e5-small" => SearchEmbeddingModelKind.E5,
            _ => throw new ReplayException($"Unknown search-embedding model kind: '{value}'. Expected one of: bert, bge, e5.")
        };
    }
}
