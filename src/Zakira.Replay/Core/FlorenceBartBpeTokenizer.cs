using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zakira.Replay.Core;

/// <summary>
/// Hand-rolled BART-style byte-level BPE tokenizer for Florence-2 / BART. Loads vocab.json +
/// merges.txt + optional added_tokens.json from disk. Same byte-level BPE algorithm as GPT-2.
/// Used by <see cref="LocalOnnxVisionProvider"/> to encode task prompts and decode generated
/// captions without an LLM in the loop.
/// </summary>
public sealed class FlorenceBartBpeTokenizer
{
    /// <summary>BART BOS token id.</summary>
    public const long BosTokenId = 0;
    /// <summary>BART PAD token id.</summary>
    public const long PadTokenId = 1;
    /// <summary>BART EOS token id (also decoder_start_token_id for Florence).</summary>
    public const long EosTokenId = 2;
    /// <summary>BART UNK token id.</summary>
    public const long UnkTokenId = 3;

    private readonly Dictionary<string, int> vocab;
    private readonly Dictionary<int, string> idToToken;
    private readonly Dictionary<(string A, string B), int> mergeRanks;
    private readonly Dictionary<string, int> addedTokens;

    // Byte-level BPE mapping (same as GPT-2). Encodes raw UTF-8 bytes into printable Unicode
    // chars so the BPE algorithm doesn't have to deal with control characters or whitespace.
    private static readonly char[] BytesToUnicode = BuildBytesToUnicode();
    private static readonly Dictionary<char, byte> UnicodeToBytes = BuildUnicodeToBytes();

    private FlorenceBartBpeTokenizer(
        Dictionary<string, int> vocab,
        Dictionary<(string, string), int> mergeRanks,
        Dictionary<string, int> addedTokens)
    {
        this.vocab = vocab;
        this.mergeRanks = mergeRanks;
        this.addedTokens = addedTokens;
        idToToken = new Dictionary<int, string>(vocab.Count + addedTokens.Count);
        foreach (var kv in vocab) idToToken[kv.Value] = kv.Key;
        foreach (var kv in addedTokens) idToToken[kv.Value] = kv.Key;
    }

    public static FlorenceBartBpeTokenizer FromFiles(string vocabPath, string mergesPath, string? addedTokensPath = null)
    {
        var vocabText = File.ReadAllText(vocabPath);
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabText)
            ?? throw new ReplayException($"florence-vocab.json at '{vocabPath}' could not be parsed.");

        var mergeRanks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var raw in File.ReadAllLines(mergesPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            mergeRanks[(parts[0], parts[1])] = rank++;
        }

        var addedTokens = new Dictionary<string, int>();
        if (!string.IsNullOrWhiteSpace(addedTokensPath) && File.Exists(addedTokensPath))
        {
            var addedText = File.ReadAllText(addedTokensPath);
            var added = JsonSerializer.Deserialize<Dictionary<string, int>>(addedText);
            if (added is not null)
            {
                foreach (var kv in added) addedTokens[kv.Key] = kv.Value;
            }
        }

        return new FlorenceBartBpeTokenizer(vocab, mergeRanks, addedTokens);
    }

    /// <summary>
    /// Encode a UTF-8 string into BART token IDs. Prepends BOS, appends EOS. BART tokenisation
    /// uses byte-level BPE (same as GPT-2): each UTF-8 byte maps to a unique unicode char,
    /// then BPE merges are applied. Whitespace at the start of words is preserved via the
    /// `Ġ` character (the byte-level encoding of space).
    /// </summary>
    public long[] Encode(string text)
    {
        var ids = new List<long> { BosTokenId };
        if (string.IsNullOrEmpty(text))
        {
            ids.Add(EosTokenId);
            return ids.ToArray();
        }

        // GPT-2/BART pre-tokenization: split into words while preserving leading whitespace.
        // We use a simple regex-free split that handles common punctuation.
        foreach (var piece in PreTokenize(text))
        {
            if (piece.Length == 0) continue;

            // Map each UTF-8 byte to its byte-level unicode char.
            var bytes = Encoding.UTF8.GetBytes(piece);
            var unicodePiece = new StringBuilder(bytes.Length);
            foreach (var b in bytes) unicodePiece.Append(BytesToUnicode[b]);
            var bpeInput = unicodePiece.ToString();

            // Apply BPE merges.
            foreach (var subToken in BpeMerge(bpeInput))
            {
                if (vocab.TryGetValue(subToken, out var id))
                {
                    ids.Add(id);
                }
                else
                {
                    ids.Add(UnkTokenId);
                }
            }
        }

        ids.Add(EosTokenId);
        return ids.ToArray();
    }

    /// <summary>
    /// Decode an array of token IDs back to text. Skips special tokens (BOS, EOS, PAD, UNK)
    /// and the &lt;loc_*&gt; / &lt;cap&gt; etc. added tokens. Byte-level Unicode is reversed
    /// back into the original UTF-8 bytes.
    /// </summary>
    public string Decode(IReadOnlyList<long> ids)
    {
        var concatenated = new StringBuilder();
        foreach (var id in ids)
        {
            if (id == BosTokenId || id == EosTokenId || id == PadTokenId || id == UnkTokenId) continue;
            var iid = (int)id;
            if (!idToToken.TryGetValue(iid, out var token)) continue;
            // Skip Florence task-output markers like <loc_X> / <cap> / <od> / <ocr>.
            if (token.StartsWith('<') && token.EndsWith('>'))
            {
                if (token.StartsWith("<loc_", StringComparison.Ordinal)
                    || token == "<cap>" || token == "</cap>"
                    || token == "<dcap>" || token == "</dcap>"
                    || token == "<ncap>" || token == "</ncap>"
                    || token == "<od>" || token == "</od>"
                    || token == "<ocr>" || token == "</ocr>"
                    || token == "<grounding>" || token == "</grounding>"
                    || token == "<seg>" || token == "</seg>"
                    || token == "<poly>" || token == "</poly>"
                    || token == "<proposal>" || token == "</proposal>"
                    || token == "<region_cap>" || token == "</region_cap>"
                    || token == "<region_to_desciption>" || token == "</region_to_desciption>"
                    || token == "<and>" || token == "<sep>")
                {
                    continue;
                }
            }
            concatenated.Append(token);
        }

        // Reverse byte-level encoding.
        var raw = concatenated.ToString();
        var bytes = new List<byte>(raw.Length);
        foreach (var c in raw)
        {
            if (UnicodeToBytes.TryGetValue(c, out var b)) bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    private static IEnumerable<string> PreTokenize(string text)
    {
        // Mirror the GPT-2 pre-tokenization split: spans of letters/digits/punctuation,
        // with the space *before* a word becoming part of the word (the `Ġ` prefix in
        // BPE space). Simplified for ASCII; sufficient for English captions.
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var hasLeadingSpace = false;
            // Consume leading whitespace.
            while (i < text.Length && char.IsWhiteSpace(text[i])) { i++; hasLeadingSpace = true; }

            if (i >= text.Length)
            {
                if (i > start) yield return text[start..i];
                yield break;
            }

            var wordStart = hasLeadingSpace ? i - 1 : i;
            if (hasLeadingSpace && text[i - 1] != ' ')
            {
                // Use a single space prefix; collapse runs of whitespace.
                wordStart = i;
                if (char.IsLetterOrDigit(text[i]) || IsPunctOrSymbol(text[i]))
                {
                    yield return " " + ConsumeWord(text, ref i);
                    continue;
                }
            }

            if (char.IsLetterOrDigit(text[i]))
            {
                if (hasLeadingSpace)
                {
                    yield return " " + ConsumeAlnum(text, ref i);
                }
                else
                {
                    yield return ConsumeAlnum(text, ref i);
                }
            }
            else if (IsPunctOrSymbol(text[i]))
            {
                if (hasLeadingSpace)
                {
                    yield return " " + text[i].ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    yield return text[i].ToString(CultureInfo.InvariantCulture);
                }
                i++;
            }
            else
            {
                i++;
            }
        }
    }

    private static string ConsumeWord(string text, ref int i)
    {
        if (char.IsLetterOrDigit(text[i])) return ConsumeAlnum(text, ref i);
        var c = text[i].ToString(CultureInfo.InvariantCulture);
        i++;
        return c;
    }

    private static string ConsumeAlnum(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
        return text[start..i];
    }

    private static bool IsPunctOrSymbol(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c);

    private IEnumerable<string> BpeMerge(string word)
    {
        if (word.Length == 0) return Array.Empty<string>();

        var pieces = new List<string>(word.Length);
        foreach (var c in word) pieces.Add(c.ToString(CultureInfo.InvariantCulture));

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

    /// <summary>
    /// GPT-2 byte-level encoding: maps each 0-255 byte to a unique printable Unicode char.
    /// Reference: https://github.com/openai/gpt-2/blob/master/src/encoder.py
    /// </summary>
    private static char[] BuildBytesToUnicode()
    {
        var bs = new List<int>();
        for (var c = '!'; c <= '~'; c++) bs.Add(c);
        for (var c = (char)0xa1; c <= 0xac; c++) bs.Add(c);
        for (var c = (char)0xae; c <= 0xff; c++) bs.Add(c);

        var cs = new List<int>(bs);
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }

        var result = new char[256];
        for (var i = 0; i < bs.Count; i++)
        {
            result[bs[i]] = (char)cs[i];
        }

        return result;
    }

    private static Dictionary<char, byte> BuildUnicodeToBytes()
    {
        var bytesToUnicode = BuildBytesToUnicode();
        var dict = new Dictionary<char, byte>(256);
        for (var i = 0; i < bytesToUnicode.Length; i++)
        {
            dict[bytesToUnicode[i]] = (byte)i;
        }

        return dict;
    }
}
