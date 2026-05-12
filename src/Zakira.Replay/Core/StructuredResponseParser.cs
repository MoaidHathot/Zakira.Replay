using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Tolerant parser for the JSON-output OCR/vision prompts. Strips Markdown code fences, finds
/// the first balanced JSON object, and falls back to a freeText-only result when the model
/// returns prose. Never throws on malformed model output.
/// </summary>
public static partial class StructuredResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OcrFrameStructured ParseOcr(string rawResponse)
    {
        return ParseOcrWithMode(rawResponse).Structured;
    }

    /// <summary>
    /// Parses an OCR response and reports whether the parser found valid structured JSON
    /// (<see cref="ParsedOcrResult.IsFallback"/> == false) or fell back to free-text storage
    /// because the response was prose / malformed JSON. Prefer this over
    /// <see cref="ParseOcr(string)"/> + <see cref="IsTolerantFallback(OcrFrameStructured)"/>
    /// in callers that have the raw response: the heuristic <c>IsTolerantFallback</c> cannot
    /// tell a successful empty parse (e.g. local OCR found no text in the frame) from a true
    /// fallback (LLM returned prose).
    /// </summary>
    public static ParsedOcrResult ParseOcrWithMode(string rawResponse)
    {
        var json = TryFindJson(rawResponse);
        if (json is null)
        {
            return new ParsedOcrResult(new OcrFrameStructured(rawResponse?.Trim() ?? string.Empty, [], []), IsFallback: true);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedOcrResult(new OcrFrameStructured(rawResponse.Trim(), [], []), IsFallback: true);
            }

            var freeText = TryGetString(root, "freeText") ?? rawResponse.Trim();
            var lines = ReadStringArray(root, "lines");
            var tables = ReadTables(root);
            return new ParsedOcrResult(new OcrFrameStructured(freeText, lines, tables), IsFallback: false);
        }
        catch (JsonException)
        {
            return new ParsedOcrResult(new OcrFrameStructured(rawResponse.Trim(), [], []), IsFallback: true);
        }
    }

    public static VisionFrameStructured ParseVision(string rawResponse)
    {
        return ParseVisionWithMode(rawResponse).Structured;
    }

    /// <summary>
    /// Parses a vision response and reports whether the parser found valid structured JSON
    /// (<see cref="ParsedVisionResult.IsFallback"/> == false). See
    /// <see cref="ParseOcrWithMode(string)"/> for rationale.
    /// </summary>
    public static ParsedVisionResult ParseVisionWithMode(string rawResponse)
    {
        var json = TryFindJson(rawResponse);
        if (json is null)
        {
            return new ParsedVisionResult(new VisionFrameStructured("other", null, [], [], [], [], rawResponse?.Trim() ?? string.Empty), IsFallback: true);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedVisionResult(new VisionFrameStructured("other", null, [], [], [], [], rawResponse.Trim()), IsFallback: true);
            }

            var kind = NormalizeKind(TryGetString(root, "kind"));
            var title = TryGetString(root, "title");
            var bullets = ReadStringArray(root, "bullets");
            var codeBlocks = ReadCodeBlocks(root);
            var charts = ReadCharts(root);
            var uiElements = ReadStringArray(root, "uiElements");
            var freeText = TryGetString(root, "freeText") ?? rawResponse.Trim();
            return new ParsedVisionResult(new VisionFrameStructured(kind, title, bullets, codeBlocks, charts, uiElements, freeText), IsFallback: false);
        }
        catch (JsonException)
        {
            return new ParsedVisionResult(new VisionFrameStructured("other", null, [], [], [], [], rawResponse.Trim()), IsFallback: true);
        }
    }

    /// <summary>
    /// Returns true when a structured value <em>likely</em> represents a model response that
    /// did not validate as JSON. This is a backward-compat heuristic for orchestrators that
    /// have only the persisted artifact (no raw response) — pipeline code with access to the
    /// raw response should prefer <see cref="ParseOcrWithMode(string)"/> and read
    /// <see cref="ParsedOcrResult.IsFallback"/> directly.
    /// </summary>
    /// <remarks>
    /// The heuristic flags a structured value as a fallback when there are no <c>Lines</c> and
    /// no <c>Tables</c>, but ONLY when <c>FreeText</c> is non-empty — an entirely empty
    /// structured value (e.g. local OCR found no text in the frame) is a valid empty parse and
    /// not a fallback.
    /// </remarks>
    public static bool IsTolerantFallback(OcrFrameStructured structured)
    {
        return structured.Lines.Count == 0
            && structured.Tables.Count == 0
            && !string.IsNullOrEmpty(structured.FreeText);
    }

    /// <summary>
    /// Vision-side counterpart of <see cref="IsTolerantFallback(OcrFrameStructured)"/>.
    /// Heuristic: <c>kind == "other"</c> AND no structured fields populated. Pipeline code with
    /// the raw response should prefer <see cref="ParseVisionWithMode(string)"/>.
    /// </summary>
    public static bool IsTolerantFallback(VisionFrameStructured structured)
    {
        return structured.Title is null
            && structured.Bullets.Count == 0
            && structured.CodeBlocks.Count == 0
            && structured.Charts.Count == 0
            && structured.UiElements.Count == 0
            && string.Equals(structured.Kind, "other", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(structured.FreeText);
    }

    private static string NormalizeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "other";
        }

        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "slide" or "ui" or "code" or "diagram" or "chart" or "dashboard" or "other" => trimmed,
            _ => "other"
        };
    }

    private static string? TryGetString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<OcrTable> ReadTables(JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tables = new List<OcrTable>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var headers = ReadStringArray(entry, "headers");
            var rows = ReadRows(entry);
            if (headers.Count == 0 && rows.Count == 0)
            {
                continue;
            }

            tables.Add(new OcrTable(headers, rows));
        }

        return tables;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(JsonElement parent)
    {
        if (!parent.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<IReadOnlyList<string>>(array.GetArrayLength());
        foreach (var rowElement in array.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var cells = new List<string>(rowElement.GetArrayLength());
            foreach (var cell in rowElement.EnumerateArray())
            {
                cells.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.ToString());
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static IReadOnlyList<VisionCodeBlock> ReadCodeBlocks(JsonElement root)
    {
        if (!root.TryGetProperty("codeBlocks", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var blocks = new List<VisionCodeBlock>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var language = TryGetString(entry, "language");
            var text = TryGetString(entry, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            blocks.Add(new VisionCodeBlock(language, text!));
        }

        return blocks;
    }

    private static IReadOnlyList<VisionChart> ReadCharts(JsonElement root)
    {
        if (!root.TryGetProperty("charts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var charts = new List<VisionChart>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = TryGetString(entry, "title");
            var axes = ReadStringArray(entry, "axes");
            var series = ReadStringArray(entry, "series");
            if (title is null && axes.Count == 0 && series.Count == 0)
            {
                continue;
            }

            charts.Add(new VisionChart(title, axes, series));
        }

        return charts;
    }

    /// <summary>
    /// Pulls the first balanced JSON object out of the response. Strips Markdown code fences if
    /// present (<c>```json ... ```</c> or unlabelled <c>``` ... ```</c>), then walks the string
    /// counting braces to handle prefix/suffix prose.
    /// </summary>
    internal static string? TryFindJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var stripped = StripCodeFence(raw);
        var open = stripped.IndexOf('{');
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = open; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return stripped.Substring(open, i - open + 1);
                }
            }
        }

        return null;
    }

    private static string StripCodeFence(string raw)
    {
        var match = CodeFenceRegex().Match(raw);
        return match.Success ? match.Groups[1].Value : raw;
    }

    [GeneratedRegex("```(?:json|JSON)?\\s*\\n?([\\s\\S]*?)\\n?```", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFenceRegex();
}

/// <summary>
/// Output of <see cref="StructuredResponseParser.ParseOcrWithMode(string)"/>: the structured
/// value plus a precise flag telling callers whether the parser found valid JSON
/// (<see cref="IsFallback"/> = false) or fell back to free-text storage because the response
/// was prose / malformed.
/// </summary>
public sealed record ParsedOcrResult(OcrFrameStructured Structured, bool IsFallback);

/// <summary>
/// Vision-side counterpart of <see cref="ParsedOcrResult"/>.
/// </summary>
public sealed record ParsedVisionResult(VisionFrameStructured Structured, bool IsFallback);
