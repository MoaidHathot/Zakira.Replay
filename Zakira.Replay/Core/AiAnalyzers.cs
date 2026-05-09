using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
}

public interface IOcrProvider
{
    Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken);
}

public interface IVisionProvider
{
    Task<string> DescribeAsync(string imagePath, string instruction, CancellationToken cancellationToken);
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

    public Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        return llm.CompleteAsync(new LlmRequest(
            Prompt: "Extract all readable text from the attached frame. Preserve line breaks where useful. Return only extracted text; do not describe the image.",
            AttachmentPaths: [imagePath],
            Model: model,
            SystemMessage: "You are an OCR engine for video frames. Return exact visible text only.",
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

    public Task<string> DescribeAsync(string imagePath, string instruction, CancellationToken cancellationToken)
    {
        return llm.CompleteAsync(new LlmRequest(
            Prompt: $"Analyze the attached video frame for this task: {instruction}\n\nDescribe visible slides, diagrams, UI, charts, code, objects, and any important visual evidence. Keep it factual and concise.",
            AttachmentPaths: [imagePath],
            Model: model,
            SystemMessage: "You analyze individual video frames as evidence. Do not infer unsupported facts.",
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);
    }
}

public sealed class VideoSummaryService
{
    private readonly ILlmProvider llm;
    private readonly string model;

    public VideoSummaryService(ILlmProvider llm, string model)
    {
        this.llm = llm;
        this.model = model;
    }

    public Task<string> SummarizeAsync(EvidenceDocument evidence, string runDirectory, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Summarize this video evidence according to the user instruction.");
        prompt.AppendLine();
        prompt.AppendLine($"Instruction: {evidence.Instruction}");
        prompt.AppendLine($"Title: {evidence.Title}");
        prompt.AppendLine($"Source: {evidence.Source}");
        prompt.AppendLine();

        if (evidence.Transcript.Count > 0)
        {
            prompt.AppendLine("Transcript:");
            foreach (var segment in evidence.Transcript.Take(500))
            {
                prompt.AppendLine($"[{segment.Timestamp ?? FormatSeconds(segment.StartSeconds)}] {segment.Text}");
            }
            prompt.AppendLine();
        }

        if (evidence.Ocr.Count > 0)
        {
            prompt.AppendLine("OCR from frames:");
            foreach (var item in evidence.Ocr)
            {
                prompt.AppendLine($"[{item.TimestampLabel}] {item.Text}");
            }
            prompt.AppendLine();
        }

        if (evidence.Vision.Count > 0)
        {
            prompt.AppendLine("Visual frame descriptions:");
            foreach (var item in evidence.Vision)
            {
                prompt.AppendLine($"[{item.TimestampLabel}] {item.Description}");
            }
            prompt.AppendLine();
        }

        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Use only the supplied evidence.");
        prompt.AppendLine("- Include timestamps for important claims when available.");
        prompt.AppendLine("- Say what evidence is missing if the request cannot be fully answered.");

        return llm.CompleteAsync(new LlmRequest(
            Prompt: prompt.ToString(),
            AttachmentPaths: [],
            Model: model,
            SystemMessage: "You summarize video evidence accurately for downstream agents.",
            WorkingDirectory: runDirectory,
            Timeout: TimeSpan.FromMinutes(3)), cancellationToken);
    }

    private static string? FormatSeconds(double? seconds)
    {
        return seconds is null ? null : Timestamp.Format(seconds.Value);
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
                segments.Add(new TranscriptSegment(ParseTimestampStart(timestamp), ParseTimestampEnd(timestamp), timestamp, match.Groups[2].Value.Trim()));
                continue;
            }

            var bracketMatch = BracketTimestampLineRegex().Match(line);
            if (bracketMatch.Success)
            {
                var timestamp = bracketMatch.Groups[1].Value;
                segments.Add(new TranscriptSegment(ParseTimestampStart(timestamp), ParseTimestampEnd(timestamp), timestamp, bracketMatch.Groups[2].Value.Trim()));
            }
            else
            {
                segments.Add(new TranscriptSegment(null, null, null, line.Trim()));
            }
        }

        return segments;
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
}
