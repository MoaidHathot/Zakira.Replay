using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public static partial class TranscriptNormalizer
{
    private const double DuplicateWindowSeconds = 2;

    public static IReadOnlyList<TranscriptSegment> Normalize(IReadOnlyList<TranscriptSegment> segments)
    {
        return NormalizeWithReport(segments).Segments;
    }

    public static TranscriptNormalizationResult NormalizeWithReport(IReadOnlyList<TranscriptSegment> segments)
    {
        var normalized = new List<TranscriptSegment>();
        var merges = new List<TranscriptNormalizationMerge>();
        foreach (var segment in segments.Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).OrderBy(segment => segment.StartSeconds ?? double.MaxValue))
        {
            var current = segment with { Text = WhitespaceRegex().Replace(segment.Text, " ").Trim() };
            if (normalized.Count == 0)
            {
                normalized.Add(current);
                continue;
            }

            var previous = normalized[^1];
            if (!IsNearDuplicateWindow(previous, current))
            {
                normalized.Add(current);
                continue;
            }

            var previousTokens = Tokenize(previous.Text).ToArray();
            var currentTokens = Tokenize(current.Text).ToArray();
            if (previousTokens.Length == 0 || currentTokens.Length == 0)
            {
                normalized.Add(current);
                continue;
            }

            if (TokensEqual(previousTokens, currentTokens))
            {
                normalized[^1] = MergeAndReport(merges, "exact-duplicate", previous, current, previous.Text);
                continue;
            }

            if (ContainsTokens(previousTokens, currentTokens))
            {
                normalized[^1] = MergeAndReport(merges, "contained-fragment", previous, current, previous.Text);
                continue;
            }

            if (StartsWithTokens(currentTokens, previousTokens))
            {
                normalized[^1] = MergeAndReport(merges, "growing-caption", previous, current, current.Text);
                continue;
            }

            var overlap = SuffixPrefixOverlap(previousTokens, currentTokens);
            if (IsUsefulOverlap(overlap, previousTokens, currentTokens))
            {
                normalized[^1] = MergeAndReport(merges, "overlapping-continuation", previous, current, MergeOverlappingText(previous.Text, current.Text, overlap));
                continue;
            }

            normalized.Add(current);
        }

        var report = new TranscriptNormalizationReport(
            SchemaVersion: "0.1",
            CreatedAt: DateTimeOffset.UtcNow,
            RawSegmentCount: segments.Count,
            NormalizedSegmentCount: normalized.Count,
            MergeCount: merges.Count,
            Merges: merges);
        return new TranscriptNormalizationResult(normalized, report);
    }

    public static string ToMarkdown(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            var timestamp = segment.Timestamp ?? FormatTimestampRange(segment);
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                builder.Append(segment.Text.Trim()).Append('\n');
            }
            else
            {
                builder.Append("**[").Append(timestamp).Append("]** ").Append(segment.Text.Trim()).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static bool IsNearDuplicateWindow(TranscriptSegment previous, TranscriptSegment current)
    {
        if (previous.StartSeconds is null || current.StartSeconds is null)
        {
            return true;
        }

        var previousReference = previous.EndSeconds ?? previous.StartSeconds.Value;
        return current.StartSeconds.Value - previousReference <= DuplicateWindowSeconds;
    }

    private static string? FormatTimestampRange(TranscriptSegment segment)
    {
        if (segment.StartSeconds is null)
        {
            return null;
        }

        var start = Timestamp.Format(segment.StartSeconds.Value);
        return segment.EndSeconds is null ? start : $"{start} - {Timestamp.Format(segment.EndSeconds.Value)}";
    }

    private static TranscriptSegment MergeTiming(TranscriptSegment previous, TranscriptSegment current, string text)
    {
        var currentEnd = current.EndSeconds ?? current.StartSeconds;
        var endSeconds = previous.EndSeconds;
        if (currentEnd is not null)
        {
            endSeconds = endSeconds is null ? currentEnd : Math.Max(endSeconds.Value, currentEnd.Value);
        }

        return previous with
        {
            EndSeconds = endSeconds,
            Timestamp = FormatTimestampRange(previous with { EndSeconds = endSeconds }),
            Text = text
        };
    }

    private static TranscriptSegment MergeAndReport(List<TranscriptNormalizationMerge> merges, string reason, TranscriptSegment previous, TranscriptSegment current, string text)
    {
        var result = MergeTiming(previous, current, text);
        merges.Add(new TranscriptNormalizationMerge(reason, previous, current, result));
        return result;
    }

    private static string MergeOverlappingText(string previous, string current, int overlap)
    {
        var currentWordMatches = WordRegex().Matches(current).Cast<Match>().ToArray();
        if (overlap >= currentWordMatches.Length)
        {
            return previous;
        }

        var appendStart = currentWordMatches[overlap].Index;
        return WhitespaceRegex().Replace(previous.TrimEnd() + " " + current[appendStart..].TrimStart(), " ").Trim();
    }

    private static bool TokensEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static bool ContainsTokens(IReadOnlyList<string> container, IReadOnlyList<string> candidate)
    {
        if (candidate.Count < 3 || candidate.Count > container.Count)
        {
            return false;
        }

        for (var i = 0; i <= container.Count - candidate.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < candidate.Count; j++)
            {
                if (!container[i + j].Equals(candidate[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithTokens(IReadOnlyList<string> container, IReadOnlyList<string> candidate)
    {
        if (candidate.Count < 3 || candidate.Count > container.Count)
        {
            return false;
        }

        for (var i = 0; i < candidate.Count; i++)
        {
            if (!container[i].Equals(candidate[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int SuffixPrefixOverlap(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var max = Math.Min(previous.Count, current.Count);
        for (var length = max; length >= 1; length--)
        {
            var matched = true;
            for (var i = 0; i < length; i++)
            {
                if (!previous[previous.Count - length + i].Equals(current[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return length;
            }
        }

        return 0;
    }

    private static bool IsUsefulOverlap(int overlap, IReadOnlyList<string> previousTokens, IReadOnlyList<string> currentTokens)
    {
        if (overlap >= 2)
        {
            return true;
        }

        return overlap == 1
            && previousTokens.Count <= 5
            && currentTokens.Count <= 5
            && previousTokens[^1].Length >= 3
            && !StopWords.Contains(previousTokens[^1]);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in WordRegex().Matches(text.ToLowerInvariant()))
        {
            yield return match.Value;
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"
    };

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record TranscriptNormalizationResult(IReadOnlyList<TranscriptSegment> Segments, TranscriptNormalizationReport Report);

public sealed record TranscriptNormalizationReport(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    int RawSegmentCount,
    int NormalizedSegmentCount,
    int MergeCount,
    IReadOnlyList<TranscriptNormalizationMerge> Merges);

public sealed record TranscriptNormalizationMerge(
    string Reason,
    TranscriptSegment Previous,
    TranscriptSegment Current,
    TranscriptSegment Result);
