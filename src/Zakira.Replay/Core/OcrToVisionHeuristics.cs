using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Pure functions that derive <see cref="VisionFrameStructured"/> fields from an
/// <see cref="OcrFrameStructured"/> result for the same frame. Used by
/// <see cref="LocalOnnxVisionProvider"/> to fill the structured fields (Title, Bullets,
/// CodeBlocks, UiElements) and to provide a fallback <c>Kind</c> when no CLIP model is
/// available. No I/O, no external state, no randomness — entirely deterministic given the
/// same OCR input. Verifiable via <c>OcrToVisionHeuristicsTests</c>.
/// </summary>
public static partial class OcrToVisionHeuristics
{
    /// <summary>
    /// Hard cap on the number of bullets / code blocks / UI elements we'll surface from a
    /// single frame to keep the manifest bounded. Lines past this cap are dropped silently.
    /// </summary>
    public const int MaxEntries = 32;

    /// <summary>
    /// Score-based derivation of <see cref="VisionFrameStructured.Kind"/> from an OCR result.
    /// Falls back to <c>"other"</c> when scores are too low or ambiguous. Callers using CLIP
    /// should prefer the CLIP output; this is the heuristic-mode fallback.
    /// </summary>
    public static string DeriveKind(OcrFrameStructured? ocr)
    {
        if (ocr is null || (string.IsNullOrWhiteSpace(ocr.FreeText) && ocr.Lines.Count == 0))
        {
            return "other";
        }

        var lines = ocr.Lines;
        var text = ocr.FreeText ?? string.Join('\n', lines);

        var codeScore = ScoreCode(text, lines);
        var uiScore = ScoreUi(text, lines);
        var chartScore = ScoreChart(text, lines);
        var dashboardScore = ScoreDashboard(text, lines);
        var slideScore = ScoreSlide(text, lines);

        // Pick the highest-scoring kind that exceeds the noise floor (3). Ties prefer "code"
        // over "ui" over "slide" because false-positive UI classification is more disruptive
        // for slide-deck content than the reverse.
        var ranked = new (string Kind, int Score)[]
        {
            ("code", codeScore),
            ("ui", uiScore),
            ("chart", chartScore),
            ("dashboard", dashboardScore),
            ("slide", slideScore)
        };

        var best = ranked.OrderByDescending(t => t.Score).First();
        return best.Score >= 3 ? best.Kind : "other";
    }

    /// <summary>
    /// Extract the most likely slide / page title from an OCR result. Returns null when no
    /// clear title is detectable (typical for pure UI or pure code frames). Heuristic: the
    /// first non-empty OCR line that is reasonably short (≤ 100 chars), not bullet-prefixed,
    /// not a single number, and not a low-information UI keyword.
    /// </summary>
    public static string? DeriveTitle(OcrFrameStructured? ocr)
    {
        if (ocr is null)
        {
            return null;
        }

        foreach (var rawLine in ocr.Lines)
        {
            var line = rawLine.Trim();
            if (line.Length is 0 or > 100)
            {
                continue;
            }

            if (BulletPrefixRegex().IsMatch(line))
            {
                continue;
            }

            if (line.All(char.IsDigit) || line.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c)))
            {
                continue;
            }

            if (UiButtonRegex().IsMatch(line) && line.Length < 20)
            {
                continue;
            }

            return line;
        }

        return null;
    }

    /// <summary>
    /// Extract bullet-style lines from an OCR result. Recognises common bullet glyphs
    /// (<c>•</c>, <c>·</c>, <c>‣</c>), dash/asterisk bullets, and numbered/lettered lists
    /// (<c>1.</c>, <c>1)</c>, <c>(a)</c>). Bullet glyphs and numbering prefixes are stripped
    /// from the returned text.
    /// </summary>
    public static IReadOnlyList<string> DeriveBullets(OcrFrameStructured? ocr)
    {
        if (ocr is null)
        {
            return [];
        }

        var bullets = new List<string>();
        foreach (var rawLine in ocr.Lines)
        {
            var line = rawLine.Trim();
            var match = BulletPrefixRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var body = line[match.Length..].Trim();
            if (body.Length > 0)
            {
                bullets.Add(body);
                if (bullets.Count >= MaxEntries)
                {
                    break;
                }
            }
        }

        return bullets;
    }

    /// <summary>
    /// Detect contiguous code-like runs in the OCR output. A run starts when a line contains a
    /// strong code symbol (curly brace, semicolon, common keywords like <c>def</c>/<c>function</c>
    /// /<c>class</c>/<c>return</c>) and continues while subsequent lines share code-symbol
    /// density or maintain consistent indentation. Returns one <see cref="VisionCodeBlock"/>
    /// per run with a best-effort language guess.
    /// </summary>
    public static IReadOnlyList<VisionCodeBlock> DeriveCodeBlocks(OcrFrameStructured? ocr)
    {
        if (ocr is null || ocr.Lines.Count == 0)
        {
            return [];
        }

        var blocks = new List<VisionCodeBlock>();
        var current = new List<string>();
        var lastWasCode = false;

        foreach (var rawLine in ocr.Lines)
        {
            var line = rawLine;
            var looksLikeCode = LooksLikeCodeLine(line);

            if (looksLikeCode)
            {
                current.Add(line);
                lastWasCode = true;
            }
            else if (lastWasCode && (line.Length == 0 || line.StartsWith(' ') || line.StartsWith('\t')))
            {
                // Continue the run: blank lines and indented continuations are still part of the block.
                current.Add(line);
            }
            else
            {
                if (current.Count > 0)
                {
                    blocks.Add(FlushBlock(current));
                    current = [];
                    if (blocks.Count >= MaxEntries) return blocks;
                }
                lastWasCode = false;
            }
        }

        if (current.Count > 0)
        {
            blocks.Add(FlushBlock(current));
        }

        // Require at least two lines per block to avoid one-liner false positives.
        return blocks.Where(b => b.Text.Count(c => c == '\n') >= 1 || b.Text.Length > 40).ToArray();
    }

    /// <summary>
    /// Surface UI-control candidates from the OCR result. Matches a curated whitelist of common
    /// button/field labels (Submit, Cancel, OK, Save, Login, Sign in, Username, Password, etc.)
    /// and emits them in the same <c>"button: Submit"</c> / <c>"field: Email"</c> format the
    /// LLM provider returns.
    /// </summary>
    public static IReadOnlyList<string> DeriveUiElements(OcrFrameStructured? ocr)
    {
        if (ocr is null)
        {
            return [];
        }

        var elements = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in ocr.Lines)
        {
            var line = rawLine.Trim();
            if (line.Length is 0 or > 60)
            {
                continue;
            }

            var category = ClassifyUiLine(line);
            if (category is null)
            {
                continue;
            }

            var entry = $"{category}: {line}";
            if (seen.Add(entry))
            {
                elements.Add(entry);
                if (elements.Count >= MaxEntries)
                {
                    break;
                }
            }
        }

        return elements;
    }

    /// <summary>
    /// Build the <see cref="VisionFrameStructured.FreeText"/> string from raw OCR text. The
    /// returned text concatenates the non-empty OCR lines with newline separators and is
    /// considered the most trustworthy part of the structured output (it is the literal visible
    /// text). The local provider uses this directly when no captioning model is loaded; with
    /// BLIP, the caller prepends a model-derived description.
    /// </summary>
    public static string DeriveFreeText(OcrFrameStructured? ocr)
    {
        if (ocr is null)
        {
            return string.Empty;
        }

        if (ocr.Lines.Count > 0)
        {
            return string.Join('\n', ocr.Lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        return ocr.FreeText?.Trim() ?? string.Empty;
    }

    private static int ScoreCode(string text, IReadOnlyList<string> lines)
    {
        var score = 0;
        if (CodeKeywordRegex().IsMatch(text)) score += 3;
        var braces = text.Count(c => c is '{' or '}' or '(' or ')' or ';');
        if (braces >= 6) score += 3; else if (braces >= 2) score += 1;
        var indentedLines = lines.Count(l => l.StartsWith(' ') || l.StartsWith('\t'));
        if (indentedLines >= 3) score += 2;
        if (text.Contains("=>", StringComparison.Ordinal) || text.Contains("::", StringComparison.Ordinal)) score += 1;
        return score;
    }

    private static int ScoreUi(string text, IReadOnlyList<string> lines)
    {
        var score = 0;
        var uiHits = lines.Count(l => UiButtonRegex().IsMatch(l.Trim()));
        if (uiHits >= 3) score += 4;
        else if (uiHits >= 1) score += 2;
        if (text.Contains("Username", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Email", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    private static int ScoreChart(string text, IReadOnlyList<string> lines)
    {
        var score = 0;
        if (Regex.IsMatch(text, "\\b(X|Y) axis\\b", RegexOptions.IgnoreCase)) score += 4;
        if (Regex.IsMatch(text, "\\b(legend|series|axis)\\b", RegexOptions.IgnoreCase)) score += 1;
        // Many short numeric labels suggests tick labels.
        var numericTokens = lines.Count(l => l.Trim().Length is > 0 and <= 8 && l.Trim().All(c => char.IsDigit(c) || c is '.' or ',' or '%' or '-'));
        if (numericTokens >= 6) score += 2;
        return score;
    }

    private static int ScoreDashboard(string text, IReadOnlyList<string> lines)
    {
        var score = 0;
        // Dashboards typically have many large-number tokens with units (%, $, k, M).
        var unitNumbers = Regex.Matches(text, "\\b\\d[\\d.,]*\\s?(?:%|\\$|k|M|ms|s|GB|MB|TB)\\b").Count;
        if (unitNumbers >= 4) score += 3;
        else if (unitNumbers >= 2) score += 1;
        if (Regex.IsMatch(text, "\\b(total|avg|average|rate|throughput|latency|error|success)\\b", RegexOptions.IgnoreCase)) score += 2;
        return score;
    }

    private static int ScoreSlide(string text, IReadOnlyList<string> lines)
    {
        var score = 0;
        var bullets = lines.Count(l => BulletPrefixRegex().IsMatch(l.Trim()));
        if (bullets >= 3) score += 4;
        else if (bullets >= 1) score += 2;
        if (lines.Count >= 1 && lines[0].Trim().Length is > 3 and < 80) score += 1;
        return score;
    }

    private static bool LooksLikeCodeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (CodeKeywordRegex().IsMatch(line)) return true;
        var symbols = line.Count(c => c is '{' or '}' or '(' or ')' or ';' or '=' or '<' or '>' or '|');
        return symbols >= 3 && line.Length > 4;
    }

    private static VisionCodeBlock FlushBlock(IReadOnlyList<string> lines)
    {
        var trimmed = string.Join('\n', lines).TrimEnd();
        return new VisionCodeBlock(GuessLanguage(trimmed), trimmed);
    }

    private static string? GuessLanguage(string text)
    {
        if (Regex.IsMatch(text, "\\b(def|import|self|print)\\b") && text.Contains(':')) return "python";
        if (Regex.IsMatch(text, "\\b(func|package|fmt\\.Print)\\b")) return "go";
        if (Regex.IsMatch(text, "\\b(public|private|class)\\s+\\w") && text.Contains(';')) return "csharp";
        if (Regex.IsMatch(text, "\\b(function|const|let|var)\\b") && text.Contains("=>")) return "javascript";
        if (Regex.IsMatch(text, "\\b(SELECT|FROM|WHERE|INSERT)\\b", RegexOptions.IgnoreCase)) return "sql";
        if (Regex.IsMatch(text, "<\\w+[^>]*>")) return "html";
        return null;
    }

    private static string? ClassifyUiLine(string line)
    {
        if (UiButtonRegex().IsMatch(line)) return "button";
        if (UiFieldRegex().IsMatch(line)) return "field";
        if (Regex.IsMatch(line, "^(File|Edit|View|Help|Tools|Window)$", RegexOptions.IgnoreCase)) return "menu";
        return null;
    }

    [GeneratedRegex("^([\\u2022\\u00b7\\u2023\\u25e6\\-\\*]\\s+|\\d{1,2}[\\.\\)]\\s+|\\([a-zA-Z]\\)\\s+|[a-zA-Z]\\)\\s+)")]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex("\\b(def|class|function|return|import|public|private|protected|namespace|using|var|let|const|async|await|interface|struct|fn|func|impl|trait|module|package|void)\\b")]
    private static partial Regex CodeKeywordRegex();

    [GeneratedRegex("^(Submit|Cancel|OK|Okay|Save|Edit|Delete|Login|Sign\\s*in|Sign\\s*up|Sign\\s*out|Logout|Settings|Help|Search|Next|Previous|Continue|Close|Apply|Reset|Yes|No|Send|Reply|Forward)$", RegexOptions.IgnoreCase)]
    private static partial Regex UiButtonRegex();

    [GeneratedRegex("^(Username|Password|Email|Email\\s*address|First\\s*name|Last\\s*name|Phone|Address|City|State|Country|Zip|Postal\\s*code|Search\\.\\.\\.|Type\\s*here)$", RegexOptions.IgnoreCase)]
    private static partial Regex UiFieldRegex();
}
