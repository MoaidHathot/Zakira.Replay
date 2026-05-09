using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public sealed partial class ChapterBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ChapterBuildResult> BuildAsync(string runDirectory, ChapterBuildOptions options, CancellationToken cancellationToken)
    {
        var fullRunDirectory = Path.GetFullPath(runDirectory);
        var evidencePath = Path.Combine(fullRunDirectory, "evidence.json");
        if (!File.Exists(evidencePath))
        {
            throw new ReplayException($"evidence.json was not found: {evidencePath}");
        }

        await using var stream = File.OpenRead(evidencePath);
        var evidence = await JsonSerializer.DeserializeAsync<EvidenceDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ReplayException("evidence.json is empty or invalid.");

        var transcript = evidence.Transcript
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartSeconds ?? 0)
            .ToArray();

        if (transcript.Length == 0)
        {
            throw new ReplayException("Chapter detection requires transcript segments in evidence.json.");
        }

        var minDuration = Math.Max(15, options.MinDurationSeconds);
        var maxDuration = Math.Max(minDuration, options.MaxDurationSeconds);
        var windows = BuildWindows(transcript, evidence.DurationSeconds, minDuration).ToArray();
        var boundaries = DetectBoundaries(windows, minDuration, maxDuration).ToArray();
        var chapters = BuildChapters(evidence, transcript, boundaries).ToArray();
        var document = new ChapterDocument("0.1", evidence.RunId, DateTimeOffset.UtcNow, "offline-lexical", chapters);

        var chapterDirectory = Path.Combine(fullRunDirectory, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        var jsonPath = Path.Combine(chapterDirectory, "chapters.json");
        var markdownPath = Path.Combine(chapterDirectory, "chapters.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, ToMarkdown(evidence, document), cancellationToken).ConfigureAwait(false);

        return new ChapterBuildResult(evidence.RunId, chapters.Length, jsonPath, markdownPath, document);
    }

    private static IEnumerable<ChapterWindow> BuildWindows(IReadOnlyList<TranscriptSegment> transcript, double? durationSeconds, double targetDurationSeconds)
    {
        var start = transcript[0].StartSeconds ?? 0;
        var text = new StringBuilder();
        double? end = null;
        var evidence = new List<ChapterEvidence>();

        foreach (var segment in transcript)
        {
            var segmentStart = segment.StartSeconds ?? end ?? start;
            var segmentEnd = segment.EndSeconds ?? segmentStart;
            if (text.Length > 0 && segmentStart - start >= targetDurationSeconds)
            {
                yield return new ChapterWindow(start, end ?? segmentStart, text.ToString(), evidence.ToArray());
                start = segmentStart;
                text.Clear();
                evidence.Clear();
            }

            AppendSentence(text, segment.Text);
            evidence.Add(new ChapterEvidence("transcript", segment.Timestamp ?? Timestamp.Format(segmentStart), segment.Text, null));
            end = segment.EndSeconds ?? segment.StartSeconds ?? end;
        }

        var finalEnd = end ?? durationSeconds ?? start;
        if (text.Length > 0)
        {
            yield return new ChapterWindow(start, Math.Max(start, finalEnd), text.ToString(), evidence.ToArray());
        }
    }

    private static IEnumerable<ChapterBoundary> DetectBoundaries(IReadOnlyList<ChapterWindow> windows, double minDurationSeconds, double maxDurationSeconds)
    {
        if (windows.Count == 0)
        {
            yield break;
        }

        var chapterStart = windows[0].StartSeconds;
        yield return new ChapterBoundary(0, chapterStart);
        for (var i = 1; i < windows.Count; i++)
        {
            var elapsed = windows[i].StartSeconds - chapterStart;
            var distance = 1 - LexicalSimilarity(windows[i - 1].Text, windows[i].Text);
            var shouldSplit = elapsed >= maxDurationSeconds || (elapsed >= minDurationSeconds && distance >= 0.72);
            if (!shouldSplit)
            {
                continue;
            }

            chapterStart = windows[i].StartSeconds;
            yield return new ChapterBoundary(i, chapterStart);
        }
    }

    private static IEnumerable<Chapter> BuildChapters(EvidenceDocument evidence, IReadOnlyList<TranscriptSegment> transcript, IReadOnlyList<ChapterBoundary> boundaries)
    {
        for (var i = 0; i < boundaries.Count; i++)
        {
            var start = boundaries[i].StartSeconds;
            var end = i + 1 < boundaries.Count
                ? boundaries[i + 1].StartSeconds
                : evidence.DurationSeconds ?? transcript.Last().EndSeconds ?? transcript.Last().StartSeconds ?? start;
            var segments = transcript
                .Where(segment => (segment.StartSeconds ?? start) >= start && (segment.StartSeconds ?? start) < end)
                .ToArray();
            if (segments.Length == 0)
            {
                continue;
            }

            var title = CreateTitle(segments);
            var summary = CreateSummary(segments);
            var chapterEvidence = segments.Take(4)
                .Select(segment => new ChapterEvidence("transcript", segment.Timestamp ?? Timestamp.Format(segment.StartSeconds ?? start), segment.Text, null))
                .Concat(FindFrameEvidence(evidence, start, end))
                .Take(6)
                .ToArray();
            yield return new Chapter(
                StartSeconds: start,
                EndSeconds: Math.Max(start, end),
                Timestamp: Timestamp.Format(start),
                EndTimestamp: Timestamp.Format(Math.Max(start, end)),
                Title: title,
                Summary: summary,
                Evidence: chapterEvidence);
        }
    }

    private static IEnumerable<ChapterEvidence> FindFrameEvidence(EvidenceDocument evidence, double start, double end)
    {
        foreach (var ocr in evidence.Ocr.Where(item => item.TimestampSeconds >= start && item.TimestampSeconds < end).Take(1))
        {
            yield return new ChapterEvidence("ocr", ocr.TimestampLabel, ocr.Text, ocr.FramePath);
        }

        foreach (var vision in evidence.Vision.Where(item => item.TimestampSeconds >= start && item.TimestampSeconds < end).Take(1))
        {
            yield return new ChapterEvidence("vision", vision.TimestampLabel, vision.Description, vision.FramePath);
        }

        foreach (var frame in evidence.Frames.Where(item => item.TimestampSeconds >= start && item.TimestampSeconds < end).Take(1))
        {
            yield return new ChapterEvidence("frame", frame.TimestampLabel, "Representative frame", frame.Path);
        }
    }

    private static string CreateTitle(IReadOnlyList<TranscriptSegment> segments)
    {
        var text = string.Join(" ", segments.Take(3).Select(segment => segment.Text.Trim()).Where(value => value.Length > 0));
        foreach (var candidate in text.Split(['.', '?', '!', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4))
        {
            var title = CreateTitleFromPhrase(candidate);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        var fallbackTerms = segments
            .SelectMany(segment => Tokenize(segment.Text))
            .GroupBy(token => token, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(6)
            .Select(group => group.Key.ToDisplayTitleWord())
            .ToArray();
        return fallbackTerms.Length == 0 ? "Chapter" : string.Join(" ", fallbackTerms);
    }

    private static string? CreateTitleFromPhrase(string phrase)
    {
        var words = TitleWordRegex().Matches(phrase.Replace(">>", string.Empty, StringComparison.Ordinal))
            .Select(match => match.Value.Trim('\'', '-'))
            .Where(word => word.Length >= 2)
            .Where(word => !TitleStopWords.Contains(word.ToLowerInvariant()))
            .Take(7)
            .Select(word => word.ToDisplayTitleWord())
            .ToArray();
        return words.Length < 2 ? null : string.Join(" ", words);
    }

    private static string CreateSummary(IReadOnlyList<TranscriptSegment> segments)
    {
        var text = string.Join(" ", segments.Select(segment => segment.Text.Trim()).Where(value => value.Length > 0));
        if (text.Length <= 260)
        {
            return text;
        }

        var split = text.LastIndexOfAny(['.', '?', '!'], 260);
        return split >= 120 ? text[..(split + 1)] : text[..260].TrimEnd() + "...";
    }

    private static double LexicalSimilarity(string left, string right)
    {
        var leftTokens = Tokenize(left).ToHashSet(StringComparer.Ordinal);
        var rightTokens = Tokenize(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3 && !StopWords.Contains(token));
    }

    private static string ToMarkdown(EvidenceDocument evidence, ChapterDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Chapters: {evidence.Title ?? evidence.RunId}");
        builder.AppendLine();
        builder.AppendLine($"Run ID: `{document.RunId}`");
        builder.AppendLine($"Method: `{document.Method}`");
        builder.AppendLine();

        foreach (var chapter in document.Chapters)
        {
            builder.AppendLine($"## {chapter.Timestamp}-{chapter.EndTimestamp} {chapter.Title}");
            builder.AppendLine();
            builder.AppendLine(chapter.Summary);
            builder.AppendLine();
            foreach (var evidenceItem in chapter.Evidence)
            {
                var path = string.IsNullOrWhiteSpace(evidenceItem.Path) ? string.Empty : $" (`{evidenceItem.Path}`)";
                builder.AppendLine($"- **[{evidenceItem.Timestamp}] {evidenceItem.Kind}**{path}: {evidenceItem.Text}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendSentence(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(text.Trim());
    }

    private static readonly char[] SearchSeparators = [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '|', '+', '=', '*', '&', '%', '$', '#', '@', '<', '>'];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "also", "and", "are", "because", "but", "can", "for", "from", "has", "have", "into", "not", "now", "one", "our", "out", "the", "then", "there", "this", "that", "they", "was", "were", "with", "you", "your"
    };

    private static readonly HashSet<string> TitleStopWords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "also", "and", "are", "because", "been", "being", "but", "can", "could", "for", "from", "had", "has", "have", "here", "into", "its", "let", "lets", "now", "okay", "our", "out", "she", "speaker", "that", "the", "their", "them", "there", "these", "they", "this", "those", "was", "were", "will", "with", "would", "yeah", "yes", "you", "your"
    };

    [GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}'-]*")]
    private static partial Regex TitleWordRegex();
}

public sealed record ChapterBuildOptions(double MinDurationSeconds = 60, double MaxDurationSeconds = 600);

public sealed record ChapterBuildResult(string RunId, int ChapterCount, string JsonPath, string MarkdownPath, ChapterDocument Document);

public sealed record ChapterDocument(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAt,
    string Method,
    IReadOnlyList<Chapter> Chapters);

public sealed record Chapter(
    double StartSeconds,
    double EndSeconds,
    string Timestamp,
    string EndTimestamp,
    string Title,
    string Summary,
    IReadOnlyList<ChapterEvidence> Evidence);

public sealed record ChapterEvidence(string Kind, string Timestamp, string Text, string? Path);

internal sealed record ChapterWindow(double StartSeconds, double EndSeconds, string Text, IReadOnlyList<ChapterEvidence> Evidence);

internal sealed record ChapterBoundary(int WindowIndex, double StartSeconds);

internal static class ChapterStringExtensions
{
    public static string ToDisplayTitleWord(this string value)
    {
        if (value.Length == 0 || value.Skip(1).Any(char.IsUpper) || value.Any(char.IsDigit))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
