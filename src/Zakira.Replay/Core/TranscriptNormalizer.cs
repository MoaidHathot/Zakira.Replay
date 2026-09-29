using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public static partial class TranscriptNormalizer
{
    private const double DuplicateWindowSeconds = 2;

    /// <summary>
    /// Segmentation policy for rolling captions (YouTube auto-subs, live captions): once the
    /// pairwise merges work, a continuous monologue would otherwise collapse into a single
    /// segment spanning the whole video, destroying the timestamp granularity chapters,
    /// alignment and search depend on. A continuation is therefore started as a new segment
    /// when the running segment already ends a sentence and exceeds <see cref="SoftMaxSegmentSeconds"/>
    /// (or <see cref="SoftMaxSegmentChars"/>), or unconditionally past <see cref="HardMaxSegmentSeconds"/>.
    /// The new segment starts at the continuation cue's own start time, which for rolling
    /// captions is exactly when its new words begin.
    /// </summary>
    public const double SoftMaxSegmentSeconds = 8;
    public const int SoftMaxSegmentChars = 400;
    public const double HardMaxSegmentSeconds = 20;
    public const int MaxCarriedWords = 12;

    public static IReadOnlyList<TranscriptSegment> Normalize(IReadOnlyList<TranscriptSegment> segments)
    {
        return NormalizeWithReport(segments).Segments;
    }

    public static TranscriptNormalizationResult NormalizeWithReport(IReadOnlyList<TranscriptSegment> segments)
    {
        var normalized = new List<TranscriptSegment>();
        var merges = new List<TranscriptNormalizationMerge>();
        var splitCount = 0;
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

            // Speaker changes are hard boundaries. Two segments attributed to different speakers
            // are facts about turn-taking; never collapse them, even when text overlaps.
            if (!SpeakerIdEquals(previous.SpeakerId, current.SpeakerId))
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
                if (TrySplitContinuation(previous, current, overlap, out var head, out var opened))
                {
                    normalized[^1] = head;
                    normalized.Add(opened);
                    splitCount++;
                    continue;
                }

                normalized[^1] = MergeAndReport(merges, "overlapping-continuation", previous, current, MergeOverlappingText(previous.Text, current.Text, overlap));
                continue;
            }

            normalized.Add(current);
        }

        normalized = AssignSegmentIds(normalized);

        var report = new TranscriptNormalizationReport(
            SchemaVersion: "0.2",
            CreatedAt: DateTimeOffset.UtcNow,
            RawSegmentCount: segments.Count,
            NormalizedSegmentCount: normalized.Count,
            MergeCount: merges.Count,
            Merges: merges,
            SplitCount: splitCount);
        return new TranscriptNormalizationResult(normalized, report);
    }

    /// <summary>
    /// Decides whether an overlapping continuation opens a new segment instead of extending
    /// <paramref name="previous"/>. Only considered when <paramref name="current"/> really starts
    /// later (a caption growing in place keeps its start and must keep merging).
    /// <list type="bullet">
    ///   <item>Past the soft budget, split at the last sentence terminator inside the running
    ///   segment: the words after it (at most <see cref="MaxCarriedWords"/>) move to the front of
    ///   the new segment, so segments align with sentences even though cue boundaries do not.</item>
    ///   <item>Past the hard budget, split at the cue boundary regardless of punctuation.</item>
    /// </list>
    /// The new segment takes the continuation cue's own timing; for rolling captions that is
    /// when its new words start. Carried words were spoken slightly earlier (within the previous
    /// cue), an error bounded by one cue.
    /// </summary>
    private static bool TrySplitContinuation(TranscriptSegment previous, TranscriptSegment current, int overlap, out TranscriptSegment head, out TranscriptSegment opened)
    {
        head = previous;
        opened = current;
        if (previous.StartSeconds is null || current.StartSeconds is null || current.StartSeconds.Value <= previous.StartSeconds.Value)
        {
            return false;
        }

        var tail = TextAfterWords(current.Text, overlap);
        if (tail.Length == 0)
        {
            return false;
        }

        var end = current.EndSeconds ?? current.StartSeconds.Value;
        var duration = end - previous.StartSeconds.Value;
        var overSoftBudget = duration > SoftMaxSegmentSeconds || previous.Text.Length > SoftMaxSegmentChars;
        if (!overSoftBudget)
        {
            return false;
        }

        var (sentenceHead, carry) = SplitAtLastSentenceEnd(previous.Text);
        if (sentenceHead is not null && CountWords(carry) <= MaxCarriedWords)
        {
            head = previous with { Text = sentenceHead };
            var openedText = carry.Length == 0 ? tail : carry + " " + tail;
            opened = current with { Text = openedText, Timestamp = FormatTimestampRange(current) };
            return true;
        }

        if (duration > HardMaxSegmentSeconds)
        {
            opened = current with { Text = tail, Timestamp = FormatTimestampRange(current) };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Splits <paramref name="text"/> after its last sentence terminator. Returns
    /// <c>(null, text)</c> when there is none, or when the terminator is the very last character
    /// (nothing to carry, the whole text is the head).
    /// </summary>
    private static (string? Head, string Carry) SplitAtLastSentenceEnd(string text)
    {
        var match = LastSentenceEndRegex().Match(text);
        if (!match.Success)
        {
            return (null, text);
        }

        var end = match.Groups["end"];
        var cut = end.Index + end.Length;
        var head = text[..cut].TrimEnd();
        var carry = text[cut..].Trim();
        return head.Length == 0 ? (null, text) : (head, carry);
    }

    private static int CountWords(string text) => WordRegex().Matches(text).Count;

    private static List<TranscriptSegment> AssignSegmentIds(IReadOnlyList<TranscriptSegment> segments)
    {
        var withIds = new List<TranscriptSegment>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var id = $"segment-{i + 1:0000}";
            withIds.Add(segments[i] with { Id = id });
        }

        return withIds;
    }

    private static bool SpeakerIdEquals(string? left, string? right)
    {
        // null == null (both unattributed) is allowed; otherwise speaker ids must match exactly.
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
        {
            return true;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
                AppendSpeakerPrefix(builder, segment);
                builder.Append(segment.Text.Trim()).Append('\n');
            }
            else
            {
                builder.Append("**[").Append(timestamp).Append("]** ");
                AppendSpeakerPrefix(builder, segment);
                builder.Append(segment.Text.Trim()).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static void AppendSpeakerPrefix(StringBuilder builder, TranscriptSegment segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerDisplayName))
        {
            builder.Append('[').Append(segment.SpeakerDisplayName).Append("] ");
        }
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
        var tail = TextAfterWords(current, overlap);
        if (tail.Length == 0)
        {
            return previous;
        }

        return WhitespaceRegex().Replace(previous.TrimEnd() + " " + tail, " ").Trim();
    }

    /// <summary>
    /// The part of <paramref name="text"/> after its first <paramref name="wordCount"/> words
    /// (empty when there is nothing left). The cut is made right after the last overlapped word
    /// and skips only closing punctuation attached to it, so opening brackets/quotes that belong
    /// to the next word (<c>[snorts]</c>, <c>"quoted</c>) survive the merge.
    /// </summary>
    private static string TextAfterWords(string text, int wordCount)
    {
        var wordMatches = WordRegex().Matches(text).Cast<Match>().ToArray();
        if (wordCount >= wordMatches.Length)
        {
            return string.Empty;
        }

        var start = wordCount == 0 ? 0 : wordMatches[wordCount - 1].Index + wordMatches[wordCount - 1].Length;
        var tail = text[start..].TrimStart();
        tail = tail.TrimStart('.', ',', ';', ':', '!', '?', '…', ')', ']', '}', '"', '\'', '”', '’', '»').TrimStart();
        return tail.Length == 0 ? text[wordMatches[wordCount].Index..].TrimStart() : tail;
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

    // Unicode letters/digits, case-insensitive. Tokenize() lowercases before matching, but
    // MergeOverlappingText() locates the append point on the ORIGINAL text; with the previous
    // ASCII lowercase-only pattern any capitalised word inside the overlap ("I", "Today",
    // "American") was skipped, shifting the index and dropping or truncating words at every
    // merge boundary ("I spent 3 weeks" -> "I 3 weeks", "Today" -> "oday"). Non-Latin scripts
    // produced no tokens at all, so Hebrew/Arabic captions were never normalized.
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    // Sentence terminator (Latin, CJK, Arabic) optionally followed by closing quotes/brackets,
    // that is followed by whitespace + an upper-case letter/digit/opening quote, or by the end of
    // the text. The greedy `^.*` prefix makes the single match land on the LAST such terminator.
    // Decimal points ("$5.99") and abbreviations followed by a lower-case word ("e.g. this")
    // are excluded by the lookahead.
    [GeneratedRegex(@"^.*(?<=\S)(?<end>[.!?…。؟]+[""'”’)\]]*)(?=\s+[\p{Lu}\p{N}""'“‘(\[]|\s*$)", RegexOptions.Singleline)]
    private static partial Regex LastSentenceEndRegex();

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
    IReadOnlyList<TranscriptNormalizationMerge> Merges,
    int SplitCount = 0);

public sealed record TranscriptNormalizationMerge(
    string Reason,
    TranscriptSegment Previous,
    TranscriptSegment Current,
    TranscriptSegment Result);
