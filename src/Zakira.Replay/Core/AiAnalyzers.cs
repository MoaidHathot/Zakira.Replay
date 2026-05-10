using System.Globalization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
}

public interface IOcrProvider
{
    Task<string> ExtractTextAsync(string imagePath, string ocrInstruction, CancellationToken cancellationToken);
}

public interface IVisionProvider
{
    Task<string> DescribeAsync(string imagePath, string visionInstruction, CancellationToken cancellationToken);
}

public sealed class CopilotTranscriptionProvider : ITranscriptionProvider
{
    private readonly ILlmProvider llm;
    private readonly string model;

    public CopilotTranscriptionProvider(ILlmProvider llm, string model)
    {
        this.llm = llm;
        this.model = model;
    }

    public Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        return llm.CompleteAsync(new LlmRequest(
            Prompt: "Transcribe the attached audio. Return only a timestamped transcript in Markdown if timestamps are available; otherwise return plain paragraphs. Do not summarize.",
            AttachmentPaths: [audioPath],
            Model: model,
            SystemMessage: "You are a precise transcription engine. Do not add content that is not present in the audio.",
            Timeout: TimeSpan.FromMinutes(3)), cancellationToken);
    }
}

public sealed class CopilotOcrProvider : IOcrProvider
{
    private readonly ILlmProvider llm;
    private readonly string model;

    public CopilotOcrProvider(ILlmProvider llm, string model)
    {
        this.llm = llm;
        this.model = model;
    }

    public Task<string> ExtractTextAsync(string imagePath, string ocrInstruction, CancellationToken cancellationToken)
    {
        var focus = string.IsNullOrWhiteSpace(ocrInstruction)
            ? string.Empty
            : "\n\nAdditional focus from the orchestrator: " + ocrInstruction.Trim() + "\nUse this as a hint about which visible text aspects to enumerate first; never invent characters that are not visible.";

        return llm.CompleteAsync(new LlmRequest(
            Prompt: $$"""
                Extract every readable piece of text from the attached frame and return ONLY a JSON object with this exact shape:
                {
                  "freeText": "all extracted text concatenated, preserving line breaks",
                  "lines": ["line 1", "line 2", "..."],
                  "tables": [
                    { "headers": ["col 1", "col 2"], "rows": [["r1c1", "r1c2"], ["r2c1", "r2c2"]] }
                  ]
                }{{focus}}
                Rules:
                - Return only the JSON object, no commentary, no Markdown fences.
                - "freeText" is required and must contain every visible character (including punctuation), one occurrence each.
                - "lines" preserves visible line breaks; omit fully empty lines.
                - "tables" is an array; emit it only when actual tabular content is visible. Skip when no tables.
                - Never invent text that is not visible.
                """,
            AttachmentPaths: [imagePath],
            Model: model,
            SystemMessage: "You are an OCR engine for video frames. Return strict JSON containing exact visible text only.",
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
    }
}

public sealed class CopilotVisionProvider : IVisionProvider
{
    private readonly ILlmProvider llm;
    private readonly string model;

    public CopilotVisionProvider(ILlmProvider llm, string model)
    {
        this.llm = llm;
        this.model = model;
    }

    public Task<string> DescribeAsync(string imagePath, string visionInstruction, CancellationToken cancellationToken)
    {
        var focus = string.IsNullOrWhiteSpace(visionInstruction)
            ? string.Empty
            : "\n\nAdditional focus from the orchestrator: " + visionInstruction.Trim() + "\nTreat this as a hint about which visible aspects to enumerate first; never invent content to satisfy it.";

        return llm.CompleteAsync(new LlmRequest(
            Prompt: $$"""
                You analyze a single video frame as evidence. Extract every distinct piece of visible content: title text, bullets, body text, code blocks, chart titles/axes/series, UI controls and labels, captioned text, diagram annotations, and anything else readable on screen. Do not invent content that is not visible.{{focus}}

                Return ONLY a JSON object with this exact shape:
                {
                  "kind": "slide" | "ui" | "code" | "diagram" | "chart" | "dashboard" | "other",
                  "title": "optional title text or null",
                  "bullets": ["..."],
                  "codeBlocks": [{ "language": "csharp" | "python" | "..." | null, "text": "..." }],
                  "charts": [{ "title": "...", "axes": ["x label", "y label"], "series": ["series name 1", "..."] }],
                  "uiElements": ["button: Submit", "field: Email", "..."],
                  "freeText": "concise factual description of visible content"
                }
                Rules:
                - Return only the JSON object, no commentary, no Markdown fences.
                - "kind" is required; pick the closest category.
                - "freeText" is required and never empty; describe what is actually visible.
                - Omit array entries you cannot fill from the visible frame; do not invent.
                - Code blocks must contain text exactly as written on the screen.
                """,
            AttachmentPaths: [imagePath],
            Model: model,
            SystemMessage: "You analyze individual video frames as evidence. Return strict JSON. Do not infer unsupported facts.",
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
    }
}

public static partial class TranscriptParser
{
    public static async Task<IReadOnlyList<TranscriptSegment>> FromMarkdownFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseMarkdown(lines);
    }

    public static IReadOnlyList<TranscriptSegment> ParseMarkdown(IEnumerable<string> lines)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = TimestampedLineRegex().Match(line);
            if (match.Success)
            {
                var timestamp = match.Groups[1].Value;
                var (speakerDisplayName, body) = ExtractInlineSpeaker(match.Groups[2].Value);
                segments.Add(new TranscriptSegment(
                    StartSeconds: ParseTimestampStart(timestamp),
                    EndSeconds: ParseTimestampEnd(timestamp),
                    Timestamp: timestamp,
                    Text: body.Trim(),
                    SpeakerId: NormalizeSpeakerId(speakerDisplayName),
                    SpeakerDisplayName: speakerDisplayName));
                continue;
            }

            var bracketMatch = BracketTimestampLineRegex().Match(line);
            if (bracketMatch.Success)
            {
                var timestamp = bracketMatch.Groups[1].Value;
                var (speakerDisplayName, body) = ExtractInlineSpeaker(bracketMatch.Groups[2].Value);
                segments.Add(new TranscriptSegment(
                    StartSeconds: ParseTimestampStart(timestamp),
                    EndSeconds: ParseTimestampEnd(timestamp),
                    Timestamp: timestamp,
                    Text: body.Trim(),
                    SpeakerId: NormalizeSpeakerId(speakerDisplayName),
                    SpeakerDisplayName: speakerDisplayName));
            }
            else
            {
                segments.Add(new TranscriptSegment(
                    StartSeconds: null,
                    EndSeconds: null,
                    Timestamp: null,
                    Text: line.Trim()));
            }
        }

        return segments;
    }

    private static (string? SpeakerDisplayName, string Body) ExtractInlineSpeaker(string text)
    {
        var trimmed = text.Trim();
        var match = SpeakerPrefixRegex().Match(trimmed);
        if (!match.Success)
        {
            return (null, trimmed);
        }

        var name = match.Groups[1].Value.Trim();
        return (string.IsNullOrWhiteSpace(name) ? null : name, match.Groups[2].Value.Trim());
    }

    private static string? NormalizeSpeakerId(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var slug = Slug.Create(displayName, 80);
        return string.IsNullOrEmpty(slug) ? null : slug;
    }

    private static double? ParseTimestamp(string timestamp)
    {
        var normalized = timestamp.Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length < 2 || parts.Length > 3)
        {
            return null;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            return null;
        }

        return hours * 3600 + minutes * 60 + seconds;
    }

    private static double? ParseTimestampStart(string timestamp)
    {
        var parts = timestamp.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
        return ParseTimestamp(parts[0]);
    }

    private static double? ParseTimestampEnd(string timestamp)
    {
        var parts = timestamp.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? ParseTimestamp(parts[1]) : null;
    }

    [GeneratedRegex("^\\*\\*\\[([^\\]]+)\\]\\*\\*\\s*(.*)$")]
    private static partial Regex TimestampedLineRegex();

    [GeneratedRegex("^-?\\s*\\[([^\\]]+)\\]\\s*(.*)$")]
    private static partial Regex BracketTimestampLineRegex();

    [GeneratedRegex("^\\[([^\\]]{1,60})\\]\\s*(.+)$")]
    private static partial Regex SpeakerPrefixRegex();
}
