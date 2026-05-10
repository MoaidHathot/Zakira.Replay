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
        var json = TryFindJson(rawResponse);
        if (json is null)
        {
            return new OcrFrameStructured(rawResponse?.Trim() ?? string.Empty, [], []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OcrFrameStructured(rawResponse.Trim(), [], []);
            }

            var freeText = TryGetString(root, "freeText") ?? rawResponse.Trim();
            var lines = ReadStringArray(root, "lines");
            var tables = ReadTables(root);
            return new OcrFrameStructured(freeText, lines, tables);
        }
        catch (JsonException)
        {
            return new OcrFrameStructured(rawResponse.Trim(), [], []);
        }
    }

    public static VisionFrameStructured ParseVision(string rawResponse)
    {
        var json = TryFindJson(rawResponse);
        if (json is null)
        {
            return new VisionFrameStructured("other", null, [], [], [], [], rawResponse?.Trim() ?? string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new VisionFrameStructured("other", null, [], [], [], [], rawResponse.Trim());
            }

            var kind = NormalizeKind(TryGetString(root, "kind"));
            var title = TryGetString(root, "title");
            var bullets = ReadStringArray(root, "bullets");
            var codeBlocks = ReadCodeBlocks(root);
            var charts = ReadCharts(root);
            var uiElements = ReadStringArray(root, "uiElements");
            var freeText = TryGetString(root, "freeText") ?? rawResponse.Trim();
            return new VisionFrameStructured(kind, title, bullets, codeBlocks, charts, uiElements, freeText);
        }
        catch (JsonException)
        {
            return new VisionFrameStructured("other", null, [], [], [], [], rawResponse.Trim());
        }
    }

    /// <summary>
    /// Returns true when the structured value still represents a model response that did not
    /// validate as JSON. Pipeline raises an <c>OCR_PARSE_FALLBACK</c> / <c>VISION_PARSE_FALLBACK</c>
    /// warning in that case so orchestrators can branch.
    /// </summary>
    public static bool IsTolerantFallback(OcrFrameStructured structured)
    {
        return structured.Lines.Count == 0 && structured.Tables.Count == 0;
    }

    public static bool IsTolerantFallback(VisionFrameStructured structured)
    {
        return structured.Title is null
            && structured.Bullets.Count == 0
            && structured.CodeBlocks.Count == 0
            && structured.Charts.Count == 0
            && structured.UiElements.Count == 0
            && string.Equals(structured.Kind, "other", StringComparison.OrdinalIgnoreCase);
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
