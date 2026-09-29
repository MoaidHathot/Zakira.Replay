using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Shared rules for deciding whether a caption prefix is a speaker label. Two parsers rely on
/// it: <see cref="SubtitleConverter"/> (VTT/SRT: <c>[Name] text</c> and <c>Name: text</c>) and
/// <see cref="TranscriptParser"/> (our own <c>transcript.md</c>: <c>[Name] text</c>).
///
/// Evidence for the rules (real runs under <c>runs/</c> before this class existed): YouTube
/// auto-captions produced phantom speakers such as <c>yogurt has two ingredients</c>,
/// <c>requires two things</c>, <c>differently. It affects two things</c> (colon heuristic firing
/// on ordinary prose) and <c>snorts</c> (bracketed sound annotation), which polluted
/// <c>evidence.json</c>'s speaker registry and blocked normalizer merges because speaker changes
/// are hard boundaries. Genuine labels (<c>Speaker A</c>, <c>Ohad Schneider</c>, <c>SPEAKER_01</c>,
/// <c>@1</c>) are short, capitalised, and recur.
/// </summary>
public static partial class SpeakerLabelHeuristics
{
    public const int MaxWords = 4;
    public const int MaxLength = 60;

    /// <summary>Bracketed captions that annotate sound, never a speaker: <c>[Music]</c>, <c>[laughter]</c>.</summary>
    private static readonly HashSet<string> NonSpeechAnnotations = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "applause", "laughter", "laughs", "laughing", "laugh", "cheering", "cheers", "inaudible",
        "crosstalk", "cross-talk", "silence", "noise", "foreign", "unintelligible", "indistinct", "singing",
        "chuckles", "chuckling", "sighs", "sigh", "snorts", "coughs", "coughing", "gasps", "pause", "sound",
        "sounds", "static", "beep", "bleep", "blank_audio", "blank audio", "background music", "upbeat music",
        "dramatic music", "soft music", "gentle music", "intro music", "outro music", "music playing",
        "phone ringing", "door closes", "footsteps", "typing", "clears throat", "sniffs", "groans", "grunts",
        "screaming", "shouting", "whispering", "mumbling", "no audio", "no speech", "speaking foreign language",
    };

    /// <summary>
    /// Words that open captions/sections rather than name a speaker. Checked against the first
    /// word of a candidate so <c>Step 1:</c>, <c>Note:</c>, <c>Ingredients:</c> stay prose.
    /// </summary>
    private static readonly HashSet<string> SectionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "notes", "warning", "tip", "tips", "step", "steps", "example", "examples", "summary",
        "question", "questions", "answer", "answers", "remember", "today", "result", "results", "update",
        "bonus", "important", "disclaimer", "ingredients", "ingredient", "instructions", "instruction",
        "method", "recipe", "part", "chapter", "section", "lesson", "episode", "intro", "outro",
        "conclusion", "translation", "transcript", "caption", "captions", "subtitle", "subtitles", "title",
        "topic", "agenda", "goal", "goals", "key", "pro", "pros", "con", "cons", "source", "sources",
        "credit", "credits", "thanks", "hint", "reminder", "recap", "preview", "spoiler", "spoilers",
        "narrator", "voiceover", "voice-over", "text", "caption", "screen", "onscreen", "on-screen", "link",
        "links", "website", "email", "phone", "address", "price", "total", "time", "date", "day", "week",
        "month", "year", "first", "second", "third", "next", "finally", "then", "now", "so", "but", "and",
    };

    /// <summary>Lower-case particles that legitimately appear inside names (<c>Ludwig van Beethoven</c>).</summary>
    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal)
    {
        "van", "von", "de", "del", "della", "di", "da", "du", "der", "den", "la", "le", "bin", "ibn", "al",
        "el", "y", "e", "of", "the",
    };

    /// <summary>True for bracketed sound annotations such as <c>[music]</c> or <c>[Applause]</c>.</summary>
    public static bool IsNonSpeechAnnotation(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim().Trim('.', '!', '…');
        return NonSpeechAnnotations.Contains(trimmed);
    }

    /// <summary>
    /// Shape rule for a speaker label: 1–<see cref="MaxWords"/> words, at most
    /// <see cref="MaxLength"/> characters, every word starting with an upper-case letter, a digit,
    /// or <c>@</c> (name particles such as <c>van</c> excepted), not all digits, not a sound
    /// annotation, and not opening with a section word. Applies to labels from explicit markup
    /// (<c>[Name]</c>) and to colon prefixes alike; the colon form additionally needs the
    /// document-level recurrence gate in <see cref="AcceptColonLabels"/>.
    /// </summary>
    public static bool LooksLikeSpeakerLabel(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (trimmed.Length is 0 or > MaxLength || IsNonSpeechAnnotation(trimmed))
        {
            return false;
        }

        if (!LabelShapeRegex().IsMatch(trimmed))
        {
            return false;
        }

        var words = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is 0 or > MaxWords)
        {
            return false;
        }

        if (words.All(word => word.All(ch => char.IsDigit(ch) || ch == '.' || ch == '-')))
        {
            return false;
        }

        if (SectionWords.Contains(words[0].TrimEnd('.', ':')))
        {
            return false;
        }

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var first = word[0];
            if (char.IsUpper(first) || char.IsDigit(first) || first == '@' || first == '#')
            {
                continue;
            }

            if (i > 0 && NameParticles.Contains(word))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Document-level gate for <c>Name: text</c> prefixes. Real speaker-labelled transcripts have
    /// at least one label that recurs; prose that happens to contain a colon (<c>yogurt has two
    /// ingredients: milk</c>) does not. When no candidate recurs, every colon candidate in the
    /// document is treated as ordinary text.
    /// </summary>
    public static bool AcceptColonLabels(IEnumerable<string?> colonCandidates)
    {
        return colonCandidates
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .GroupBy(label => label!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() >= 2);
    }

    // Letters/digits/@/#/_/'/-/./space only; must start and end on a word-ish character.
    [GeneratedRegex(@"^[\p{L}\p{N}@#][\p{L}\p{N}@#_' \-\.]*[\p{L}\p{N}\.]$|^[\p{L}\p{N}@#]$")]
    private static partial Regex LabelShapeRegex();
}
