using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Turns per-frame OCR into a deduplicated, timestamped list of on-screen text: each distinct
/// line appears once with the first and last frame it was seen in. For videos without
/// narration (on-screen overlays such as "REST FOR 15 MINUTES", "FERMENT FOR 2-2.5 HOURS") this
/// is the transcript; for slide decks it is the text timeline without the same slide repeated
/// ten times.
///
/// Matching is fuzzy because consecutive frames of the same overlay differ by OCR noise
/// ("ecosystem feels vindicated" vs "ecosy ractically"): a line joins an existing step when the
/// normalised text is equal, one contains the other, the token Jaccard similarity is high, or the
/// character-level similarity is high - but only against steps last seen within
/// <see cref="RecencyWindowSeconds"/>, so the same overlay reappearing later is a new step.
/// </summary>
public static partial class OcrStepBuilder
{
    public const double RecencyWindowSeconds = 90;
    public const int MinLineLength = 3;
    public const double TokenJaccardThreshold = 0.6;
    public const double CharacterSimilarityThreshold = 0.78;
    private const int MaxComparableLength = 160;

    public static OcrStepList Build(IReadOnlyList<OcrFrameResult> results)
    {
        var steps = new List<MutableStep>();
        foreach (var frame in results.OrderBy(result => result.TimestampSeconds).ThenBy(result => result.FrameId, StringComparer.Ordinal))
        {
            foreach (var rawLine in EnumerateLines(frame))
            {
                var normalized = Normalize(rawLine);
                if (normalized.Length < MinLineLength || !normalized.Any(char.IsLetterOrDigit))
                {
                    continue;
                }

                var key = Key(normalized);
                var tokens = Tokenize(key);
                if (tokens.Count == 0)
                {
                    continue;
                }

                var match = FindMatch(steps, key, tokens, frame.TimestampSeconds);
                if (match is null)
                {
                    steps.Add(new MutableStep(normalized, key, tokens, frame));
                }
                else
                {
                    match.Absorb(normalized, key, tokens, frame);
                }
            }
        }

        var ordered = steps.OrderBy(step => step.FirstSeenSeconds).ThenBy(step => step.Order).ToList();
        var items = new List<OcrStep>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            items.Add(ordered[i].ToStep(i + 1));
        }

        return new OcrStepList(
            SchemaVersion: "0.1",
            CreatedAt: DateTimeOffset.UtcNow,
            FrameCount: results.Count,
            LineCount: steps.Sum(step => step.Occurrences),
            StepCount: items.Count,
            Steps: items);
    }

    public static string ToMarkdown(OcrStepList list)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# On-screen text timeline");
        builder.AppendLine();
        builder.AppendLine($"{list.StepCount} distinct line(s) from {list.LineCount} OCR line(s) across {list.FrameCount} frame(s).");
        builder.AppendLine();
        foreach (var step in list.Steps)
        {
            var range = Math.Abs(step.LastSeenSeconds - step.FirstSeenSeconds) < 0.5
                ? step.FirstSeenLabel
                : $"{step.FirstSeenLabel} - {step.LastSeenLabel}";
            builder.Append("- **[").Append(range).Append("]** ").AppendLine(step.Text);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateLines(OcrFrameResult frame)
    {
        if (frame.Structured?.Lines is { Count: > 0 } lines)
        {
            return lines;
        }

        return (frame.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static string Normalize(string line)
    {
        var collapsed = WhitespaceRegex().Replace(line, " ").Trim();
        return collapsed.TrimEnd('.', ',', ';', ':', '·', '•', '-', '–', '—').Trim();
    }

    /// <summary>Comparison key: lower-case letters/digits/spaces only.</summary>
    internal static string Key(string normalized)
    {
        var builder = new StringBuilder(normalized.Length);
        var lastSpace = true;
        foreach (var ch in normalized.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                builder.Append(' ');
                lastSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static HashSet<string> Tokenize(string key)
        => key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length > 1 || char.IsDigit(token[0])).ToHashSet(StringComparer.Ordinal);

    private static MutableStep? FindMatch(List<MutableStep> steps, string key, HashSet<string> tokens, double timestamp)
    {
        MutableStep? best = null;
        var bestScore = 0.0;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            if (timestamp - step.LastSeenSeconds > RecencyWindowSeconds)
            {
                // Steps are appended in time order, so everything earlier is even older.
                break;
            }

            var score = Similarity(step, key, tokens);
            if (score > bestScore)
            {
                bestScore = score;
                best = step;
            }
        }

        return bestScore >= 1.0 ? best : null;
    }

    /// <summary>1.0 when the line should join the step, otherwise 0; intermediate values rank ties.</summary>
    internal static double Similarity(MutableStep step, string key, HashSet<string> tokens)
    {
        if (step.Keys.Contains(key))
        {
            return 1.0;
        }

        foreach (var known in step.Keys)
        {
            if (IsContainment(known, key))
            {
                return 1.0;
            }
        }

        var intersection = tokens.Count(step.Tokens.Contains);
        var union = tokens.Count + step.Tokens.Count - intersection;
        var jaccard = union == 0 ? 0 : (double)intersection / union;
        if (jaccard >= TokenJaccardThreshold)
        {
            return 1.0;
        }

        // Character-level check only when the token overlap makes it plausible; keeps the
        // quadratic distance off the hot path.
        if (jaccard >= 0.25 || Math.Min(key.Length, step.Key.Length) <= 12)
        {
            var similarity = CharacterSimilarity(key, step.Key);
            if (similarity >= CharacterSimilarityThreshold)
            {
                return 1.0;
            }
        }

        return 0;
    }

    private static bool IsContainment(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 8 && shorter.Length >= longer.Length * 0.6 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>1 - Levenshtein distance / max length, over the first <see cref="MaxComparableLength"/> characters.</summary>
    internal static double CharacterSimilarity(string a, string b)
    {
        if (a.Length > MaxComparableLength)
        {
            a = a[..MaxComparableLength];
        }
        if (b.Length > MaxComparableLength)
        {
            b = b[..MaxComparableLength];
        }

        var max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / max;
    }

    internal sealed class MutableStep
    {
        private static int counter;

        public MutableStep(string text, string key, HashSet<string> tokens, OcrFrameResult frame)
        {
            Order = Interlocked.Increment(ref counter);
            Text = text;
            Key = key;
            Keys = [key];
            Tokens = new HashSet<string>(tokens, StringComparer.Ordinal);
            FirstSeenSeconds = frame.TimestampSeconds;
            FirstSeenLabel = frame.TimestampLabel;
            LastSeenSeconds = frame.TimestampSeconds;
            LastSeenLabel = frame.TimestampLabel;
            FrameIds = [frame.FrameId];
            Occurrences = 1;
        }

        public int Order { get; }
        public string Text { get; private set; }
        public string Key { get; private set; }
        public HashSet<string> Keys { get; }
        public HashSet<string> Tokens { get; }
        public double FirstSeenSeconds { get; }
        public string FirstSeenLabel { get; }
        public double LastSeenSeconds { get; private set; }
        public string LastSeenLabel { get; private set; }
        public List<string> FrameIds { get; }
        public int Occurrences { get; private set; }

        public void Absorb(string text, string key, HashSet<string> tokens, OcrFrameResult frame)
        {
            Occurrences++;
            Keys.Add(key);
            Tokens.UnionWith(tokens);
            if (frame.TimestampSeconds >= LastSeenSeconds)
            {
                LastSeenSeconds = frame.TimestampSeconds;
                LastSeenLabel = frame.TimestampLabel;
            }
            if (!FrameIds.Contains(frame.FrameId))
            {
                FrameIds.Add(frame.FrameId);
            }

            // Keep the cleanest variant: more letters/digits, fewer stray symbols.
            if (Quality(text) > Quality(Text))
            {
                Text = text;
                Key = key;
            }
        }

        private static double Quality(string text)
        {
            var alnum = text.Count(char.IsLetterOrDigit);
            var symbols = text.Count(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ch is not ('.' or ',' or '\'' or '-' or ':' or '%' or '/' or '(' or ')'));
            return alnum - 2 * symbols;
        }

        public OcrStep ToStep(int index) => new(
            Index: index,
            Text: Text,
            FirstSeenSeconds: FirstSeenSeconds,
            FirstSeenLabel: FirstSeenLabel,
            LastSeenSeconds: LastSeenSeconds,
            LastSeenLabel: LastSeenLabel,
            Occurrences: Occurrences,
            FrameIds: FrameIds.ToArray());
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record OcrStepList(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    int FrameCount,
    int LineCount,
    int StepCount,
    IReadOnlyList<OcrStep> Steps);

public sealed record OcrStep(
    int Index,
    string Text,
    double FirstSeenSeconds,
    string FirstSeenLabel,
    double LastSeenSeconds,
    string LastSeenLabel,
    int Occurrences,
    IReadOnlyList<string> FrameIds);
