namespace Zakira.Replay.Core;

/// <summary>
/// Joins diarization speaker turns to transcript segments. For each transcript segment, we
/// find the diarization segment with the most temporal overlap and assign that speaker — this
/// is the standard "DER-style" majority-vote attribution used by virtually every diarization
/// evaluator. When a transcript segment has no timing or there is no overlapping diarization
/// segment, the segment is returned unchanged (its <see cref="TranscriptSegment.SpeakerId"/>
/// stays null).
/// </summary>
/// <remarks>
/// The merger never invents speakers, never reorders or splits transcript segments, and never
/// alters segment text — it only fills in <c>SpeakerId</c> / <c>SpeakerDisplayName</c> fields.
/// Pre-existing speaker attribution (e.g. from VTT <c>&lt;v Speaker&gt;</c> tags) is preserved
/// when <see cref="MergeOptions.PreserveExistingSpeakers"/> is true (the default).
/// </remarks>
public static class DiarizationMerger
{
    /// <summary>
    /// Apply diarization to a transcript by populating each segment's <c>SpeakerId</c>.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Merge(
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<DiarizationSegment> diarization,
        MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(diarization);
        options ??= MergeOptions.Default;

        if (transcript.Count == 0 || diarization.Count == 0)
        {
            return transcript;
        }

        // Pre-cast diarization ranges to seconds so we don't repeatedly call TimeSpan.TotalSeconds
        // in the inner loop.
        var diarizationRanges = new (double Start, double End, string SpeakerId)[diarization.Count];
        for (var i = 0; i < diarization.Count; i++)
        {
            diarizationRanges[i] = (diarization[i].Start.TotalSeconds, diarization[i].End.TotalSeconds, diarization[i].SpeakerId);
        }

        var result = new List<TranscriptSegment>(transcript.Count);
        foreach (var segment in transcript)
        {
            if (segment is null)
            {
                continue;
            }

            // Preserve existing speaker attribution (e.g. from <v> tags in VTT) when asked.
            if (options.PreserveExistingSpeakers && !string.IsNullOrWhiteSpace(segment.SpeakerId))
            {
                result.Add(segment);
                continue;
            }

            if (segment.StartSeconds is not { } segStart || segment.EndSeconds is not { } segEnd || segEnd <= segStart)
            {
                result.Add(segment);
                continue;
            }

            var (bestSpeakerId, bestOverlap) = FindBestSpeaker(diarizationRanges, segStart, segEnd);
            if (bestSpeakerId is null || bestOverlap < options.MinOverlapSeconds)
            {
                result.Add(segment);
                continue;
            }

            result.Add(segment with
            {
                SpeakerId = bestSpeakerId,
                SpeakerDisplayName = bestSpeakerId
            });
        }

        return result;
    }

    /// <summary>
    /// Walk the markdown transcript a chunked-STT step produced and inject
    /// <c>[SPEAKER_NN]</c> prefixes ahead of each timestamped line, using the same majority-vote
    /// attribution as <see cref="Merge"/>. Lines without a parseable timestamp are passed
    /// through unchanged.
    /// </summary>
    public static string AnnotateMarkdown(
        string markdownTranscript,
        IReadOnlyList<DiarizationSegment> diarization,
        MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdownTranscript);
        ArgumentNullException.ThrowIfNull(diarization);
        options ??= MergeOptions.Default;

        if (string.IsNullOrWhiteSpace(markdownTranscript) || diarization.Count == 0)
        {
            return markdownTranscript;
        }

        var segments = TranscriptParser.ParseMarkdown(markdownTranscript.Split('\n'));
        var merged = Merge(segments, diarization, options);
        return RenderMarkdown(merged);
    }

    private static (string? SpeakerId, double OverlapSeconds) FindBestSpeaker(
        (double Start, double End, string SpeakerId)[] ranges,
        double segStart,
        double segEnd)
    {
        string? bestSpeaker = null;
        var bestOverlap = 0.0;
        foreach (var range in ranges)
        {
            if (range.End <= segStart)
            {
                continue;
            }

            if (range.Start >= segEnd)
            {
                // Diarization is sorted by Start, so subsequent ranges are also past the segment.
                break;
            }

            var overlapStart = Math.Max(range.Start, segStart);
            var overlapEnd = Math.Min(range.End, segEnd);
            var overlap = overlapEnd - overlapStart;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestSpeaker = range.SpeakerId;
            }
        }

        return (bestSpeaker, bestOverlap);
    }

    private static string RenderMarkdown(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(segment.Timestamp))
            {
                builder.Append("**[").Append(segment.Timestamp).Append("]** ");
            }

            if (!string.IsNullOrWhiteSpace(segment.SpeakerDisplayName))
            {
                builder.Append('[').Append(segment.SpeakerDisplayName).Append("] ");
            }

            builder.AppendLine(segment.Text);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Tunables for <see cref="DiarizationMerger.Merge"/>.
/// </summary>
public sealed record MergeOptions(bool PreserveExistingSpeakers = true, double MinOverlapSeconds = 0.05)
{
    public static MergeOptions Default { get; } = new();
}
