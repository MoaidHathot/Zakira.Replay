namespace Zakira.Replay.Core;

/// <summary>
/// Catalog of RapidOCR PP-OCRv5 language packs supported by Zakira.Replay. Each pack pairs a
/// recognition ONNX model with a character dictionary file; the detection and classification
/// models are shared across packs (one download covers them all). Switching packs is a config
/// change, not a re-download — multiple packs can coexist on disk.
/// </summary>
/// <remarks>
/// All listed packs were HEAD-verified against the RapidAI ModelScope mirror
/// (<c>v3.8.0</c> tag) at release time. Languages that PP-OCRv5 does not currently publish
/// (Japanese, Thai, Georgian, Kannada, Traditional Chinese) are intentionally omitted —
/// PP-OCRv4 still ships them but we don't mix versions in a single release.
/// </remarks>
public static class OcrLanguagePacks
{
    /// <summary>Default pack — matches the pre-language-pack 0.5.0 and earlier behaviour.</summary>
    public const string Latin = "latin";

    /// <summary>Simplified Chinese plus shared Han characters.</summary>
    public const string Chinese = "chinese";

    /// <summary>English-only — denser dictionary than Latin for Western European meetings.</summary>
    public const string English = "english";

    public const string Korean = "korean";

    public const string Cyrillic = "cyrillic";

    public const string Arabic = "arabic";

    public const string Devanagari = "devanagari";

    public const string Greek = "greek";

    public const string Telugu = "telugu";

    public const string Tamil = "tamil";

    /// <summary>
    /// All packs known to the installer + provider. Use <see cref="TryGet"/> for case-insensitive
    /// alias-aware lookup.
    /// </summary>
    public static IReadOnlyList<OcrLanguagePack> All { get; } =
    [
        new OcrLanguagePack(
            Name: Latin,
            RecognitionModelFile: "latin_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_latin_dict.txt",
            RecognitionModelDirectory: "latin_PP-OCRv5_rec_mobile",
            Aliases: ["en", "european", "western", "eu"],
            DisplayName: "Latin (multi-language Western European)"),
        new OcrLanguagePack(
            Name: Chinese,
            RecognitionModelFile: "ch_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_dict.txt",
            RecognitionModelDirectory: "ch_PP-OCRv5_rec_mobile",
            Aliases: ["zh", "cn", "ch", "simplified-chinese", "simp", "han"],
            DisplayName: "Chinese (simplified, shared Han)"),
        new OcrLanguagePack(
            Name: English,
            RecognitionModelFile: "en_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_en_dict.txt",
            RecognitionModelDirectory: "en_PP-OCRv5_rec_mobile",
            Aliases: ["en-only"],
            DisplayName: "English (denser dictionary than Latin)"),
        new OcrLanguagePack(
            Name: Korean,
            RecognitionModelFile: "korean_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_korean_dict.txt",
            RecognitionModelDirectory: "korean_PP-OCRv5_rec_mobile",
            Aliases: ["ko", "kr", "hangul"],
            DisplayName: "Korean (Hangul)"),
        new OcrLanguagePack(
            Name: Cyrillic,
            RecognitionModelFile: "cyrillic_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_cyrillic_dict.txt",
            RecognitionModelDirectory: "cyrillic_PP-OCRv5_rec_mobile",
            Aliases: ["ru", "russian", "ukrainian", "uk", "be", "bg", "sr"],
            DisplayName: "Cyrillic (Russian / Ukrainian / Belarusian / Bulgarian / Serbian)"),
        new OcrLanguagePack(
            Name: Arabic,
            RecognitionModelFile: "arabic_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_arabic_dict.txt",
            RecognitionModelDirectory: "arabic_PP-OCRv5_rec_mobile",
            Aliases: ["ar", "fa", "ur", "persian", "farsi", "urdu"],
            DisplayName: "Arabic (Arabic / Persian / Urdu)"),
        new OcrLanguagePack(
            Name: Devanagari,
            RecognitionModelFile: "devanagari_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_devanagari_dict.txt",
            RecognitionModelDirectory: "devanagari_PP-OCRv5_rec_mobile",
            Aliases: ["hi", "hindi", "mr", "marathi", "ne", "nepali", "sa", "sanskrit"],
            DisplayName: "Devanagari (Hindi / Marathi / Nepali / Sanskrit)"),
        new OcrLanguagePack(
            Name: Greek,
            RecognitionModelFile: "el_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_el_dict.txt",
            RecognitionModelDirectory: "el_PP-OCRv5_rec_mobile",
            Aliases: ["el", "gr", "ell"],
            DisplayName: "Greek"),
        new OcrLanguagePack(
            Name: Telugu,
            RecognitionModelFile: "te_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_te_dict.txt",
            RecognitionModelDirectory: "te_PP-OCRv5_rec_mobile",
            Aliases: ["te", "telegu"],
            DisplayName: "Telugu"),
        new OcrLanguagePack(
            Name: Tamil,
            RecognitionModelFile: "ta_PP-OCRv5_rec_mobile.onnx",
            DictionaryFile: "ppocrv5_ta_dict.txt",
            RecognitionModelDirectory: "ta_PP-OCRv5_rec_mobile",
            Aliases: ["ta", "tamizh", "tha"],
            DisplayName: "Tamil")
    ];

    /// <summary>
    /// Look up a pack by canonical name or any registered alias. Comparison is
    /// case-insensitive and tolerates underscore/hyphen swaps.
    /// </summary>
    public static bool TryGet(string? input, out OcrLanguagePack pack)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            pack = Get(Latin);
            return true;
        }

        var normalized = input.Trim().ToLowerInvariant().Replace('_', '-');
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                pack = candidate;
                return true;
            }

            foreach (var alias in candidate.Aliases)
            {
                if (string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    pack = candidate;
                    return true;
                }
            }
        }

        pack = default!;
        return false;
    }

    /// <summary>
    /// Get a pack by canonical name. Throws <see cref="ReplayException"/> for unknown packs.
    /// Use <see cref="TryGet"/> in CLI / config code paths so error messages can be customised.
    /// </summary>
    public static OcrLanguagePack Get(string name)
    {
        if (!TryGet(name, out var pack))
        {
            throw new ReplayException($"Unknown OCR language pack: '{name}'. Known packs: {string.Join(", ", All.Select(p => p.Name))}.");
        }

        return pack;
    }

    /// <summary>
    /// Normalise an input to its canonical pack name. Returns <see cref="Latin"/> for null /
    /// blank input. Returns the input verbatim (lowercased) for unknown packs so the caller can
    /// emit a structured error pointing at <see cref="All"/>.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (TryGet(input, out var pack))
        {
            return pack.Name;
        }

        return input!.Trim().ToLowerInvariant().Replace('_', '-');
    }
}

/// <summary>
/// Metadata for a single RapidOCR PP-OCRv5 language pack. The detection and classification
/// models are shared across packs and not represented here.
/// </summary>
public sealed record OcrLanguagePack(
    string Name,
    string RecognitionModelFile,
    string DictionaryFile,
    string RecognitionModelDirectory,
    IReadOnlyList<string> Aliases,
    string DisplayName);
