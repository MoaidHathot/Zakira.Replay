using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

/// <summary>
/// Builds cross-modal alignment views from a completed run's evidence and chapters.
/// </summary>
/// <remarks>
/// Pure rearrangement: every fact emitted here already exists in <c>evidence.json</c> or
/// <c>chapters/chapters.json</c>. No model calls. Two views are written:
/// <list type="bullet">
///   <item><description><c>evidence-aligned/by-chapter.json</c> — one entry per chapter, joining slides, transcript segment IDs, and per-speaker statistics within the chapter window.</description></item>
///   <item><description><c>evidence-aligned/by-slide.json</c> — one entry per slide, joining the slide's frames, OCR and vision results, transcript segment IDs spoken while the slide was visible, per-speaker statistics over the slide window, and the chapters the slide overlaps.</description></item>
/// </list>
/// Slide visibility windows are extended to <c>[slide[i].firstSeenSeconds, slide[i+1].firstSeenSeconds)</c>
/// (with the last slide covering up to <c>evidence.durationSeconds</c>) to model "slide N is on
/// screen until slide N+1 appears". This is a deterministic projection, never inferred content.
/// </remarks>
public sealed class EvidenceAlignmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<EvidenceAlignmentResult> BuildAsync(string runDirectory, EvidenceAlignmentOptions options, CancellationToken cancellationToken)
    {
        var fullRunDirectory = Path.GetFullPath(runDirectory);
        var evidencePath = Path.Combine(fullRunDirectory, "evidence.json");
        if (!File.Exists(evidencePath))
        {
            throw new ReplayException($"evidence.json was not found: {evidencePath}");
        }

        await using var evidenceStream = File.OpenRead(evidencePath);
        var evidence = await JsonSerializer.DeserializeAsync<EvidenceDocument>(evidenceStream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ReplayException("evidence.json is empty or invalid.");

        var chapters = await TryLoadChaptersAsync(fullRunDirectory, cancellationToken).ConfigureAwait(false);
        var alignmentDirectory = Path.Combine(fullRunDirectory, "evidence-aligned");
        Directory.CreateDirectory(alignmentDirectory);

        var slideWindows = BuildSlideWindows(evidence);
        var byChapter = BuildByChapter(evidence, chapters, slideWindows);
        var bySlide = BuildBySlide(evidence, chapters, slideWindows);

        var byChapterDoc = new EvidenceAlignmentByChapterDocument(
            SchemaVersion: "0.7",
            RunId: evidence.RunId,
            CreatedAt: DateTimeOffset.UtcNow,
            View: "by-chapter",
            Chapters: byChapter);

        var bySlideDoc = new EvidenceAlignmentBySlideDocument(
            SchemaVersion: "0.7",
            RunId: evidence.RunId,
            CreatedAt: DateTimeOffset.UtcNow,
            View: "by-slide",
            Slides: bySlide);

        var byChapterPath = Path.Combine(alignmentDirectory, "by-chapter.json");
        var bySlidePath = Path.Combine(alignmentDirectory, "by-slide.json");
        await File.WriteAllTextAsync(byChapterPath, JsonSerializer.Serialize(byChapterDoc, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(bySlidePath, JsonSerializer.Serialize(bySlideDoc, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        return new EvidenceAlignmentResult(
            RunId: evidence.RunId,
            ByChapterPath: byChapterPath,
            BySlidePath: bySlidePath,
            ByChapter: byChapterDoc,
            BySlide: bySlideDoc,
            ChaptersLoaded: chapters is not null);
    }

    private static async Task<ChapterDocument?> TryLoadChaptersAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var chaptersPath = Path.Combine(runDirectory, "chapters", "chapters.json");
        if (!File.Exists(chaptersPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(chaptersPath);
            return await JsonSerializer.DeserializeAsync<ChapterDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds [start, end) windows for each slide based on the next slide's first-seen time so
    /// orchestrators can answer "which transcript segments were spoken while slide N was on screen".
    /// </summary>
    internal static IReadOnlyDictionary<string, (double Start, double End)> BuildSlideWindows(EvidenceDocument evidence)
    {
        var windows = new Dictionary<string, (double Start, double End)>(StringComparer.OrdinalIgnoreCase);
        var orderedSlides = evidence.Slides.OrderBy(slide => slide.FirstSeenSeconds).ThenBy(slide => slide.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (orderedSlides.Length == 0)
        {
            return windows;
        }

        var fallbackEnd = evidence.DurationSeconds ?? orderedSlides[^1].LastSeenSeconds;
        for (var i = 0; i < orderedSlides.Length; i++)
        {
            var slide = orderedSlides[i];
            var nextStart = i + 1 < orderedSlides.Length ? orderedSlides[i + 1].FirstSeenSeconds : Math.Max(slide.LastSeenSeconds, fallbackEnd);
            var end = Math.Max(slide.LastSeenSeconds, nextStart);
            windows[slide.Id] = (slide.FirstSeenSeconds, end);
        }

        return windows;
    }

    private static IReadOnlyList<EvidenceAlignmentByChapterEntry> BuildByChapter(EvidenceDocument evidence, ChapterDocument? chapters, IReadOnlyDictionary<string, (double Start, double End)> slideWindows)
    {
        if (chapters is null)
        {
            return [];
        }

        var entries = new List<EvidenceAlignmentByChapterEntry>(chapters.Chapters.Count);
        for (var i = 0; i < chapters.Chapters.Count; i++)
        {
            var chapter = chapters.Chapters[i];
            var transcriptSegments = evidence.Transcript
                .Where(segment => SegmentStartsInWindow(segment, chapter.StartSeconds, chapter.EndSeconds))
                .ToArray();
            var slideIds = evidence.Slides
                .Where(slide => slideWindows.TryGetValue(slide.Id, out var window) && IntersectsRange(window, chapter.StartSeconds, chapter.EndSeconds))
                .Select(slide => slide.Id)
                .ToArray();
            var speakers = SummarizeSpeakers(transcriptSegments);
            var ocrFrameIds = evidence.Ocr
                .Where(ocr => ocr.SlideId is not null && slideIds.Contains(ocr.SlideId, StringComparer.OrdinalIgnoreCase))
                .Select(ocr => ocr.FrameId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var visionFrameIds = evidence.Vision
                .Where(vision => vision.SlideId is not null && slideIds.Contains(vision.SlideId, StringComparer.OrdinalIgnoreCase))
                .Select(vision => vision.FrameId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            entries.Add(new EvidenceAlignmentByChapterEntry(
                ChapterIndex: i,
                StartSeconds: chapter.StartSeconds,
                EndSeconds: chapter.EndSeconds,
                Timestamp: chapter.Timestamp,
                EndTimestamp: chapter.EndTimestamp,
                SlideIds: slideIds,
                TranscriptSegmentIds: transcriptSegments
                    .Select(segment => segment.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToArray(),
                Speakers: speakers,
                OcrFrameIds: ocrFrameIds,
                VisionFrameIds: visionFrameIds));
        }

        return entries;
    }

    private static IReadOnlyList<EvidenceAlignmentBySlideEntry> BuildBySlide(EvidenceDocument evidence, ChapterDocument? chapters, IReadOnlyDictionary<string, (double Start, double End)> slideWindows)
    {
        var ocrBySlide = evidence.Ocr
            .Where(ocr => ocr.SlideId is not null)
            .ToDictionary(ocr => ocr.SlideId!, StringComparer.OrdinalIgnoreCase);
        var visionBySlide = evidence.Vision
            .Where(vision => vision.SlideId is not null)
            .ToDictionary(vision => vision.SlideId!, StringComparer.OrdinalIgnoreCase);

        var entries = new List<EvidenceAlignmentBySlideEntry>(evidence.Slides.Count);
        foreach (var slide in evidence.Slides)
        {
            if (!slideWindows.TryGetValue(slide.Id, out var window))
            {
                window = (slide.FirstSeenSeconds, slide.LastSeenSeconds);
            }

            var transcriptSegments = evidence.Transcript
                .Where(segment => SegmentIntersectsRange(segment, window.Start, window.End))
                .ToArray();
            var speakers = SummarizeSpeakers(transcriptSegments);
            var chapterIndices = chapters is null
                ? []
                : Enumerable.Range(0, chapters.Chapters.Count)
                    .Where(index => IntersectsRange(window, chapters.Chapters[index].StartSeconds, chapters.Chapters[index].EndSeconds))
                    .ToArray();

            entries.Add(new EvidenceAlignmentBySlideEntry(
                SlideId: slide.Id,
                FirstSeenSeconds: slide.FirstSeenSeconds,
                LastSeenSeconds: slide.LastSeenSeconds,
                WindowStartSeconds: window.Start,
                WindowEndSeconds: window.End,
                FirstSeenLabel: slide.FirstSeenLabel,
                LastSeenLabel: slide.LastSeenLabel,
                PrimaryFrameId: slide.PrimaryFrameId,
                FrameIds: slide.FrameIds,
                Ocr: ocrBySlide.GetValueOrDefault(slide.Id),
                Vision: visionBySlide.GetValueOrDefault(slide.Id),
                TranscriptSegmentIds: transcriptSegments
                    .Select(segment => segment.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToArray(),
                Speakers: speakers,
                ChapterIndices: chapterIndices));
        }

        return entries;
    }

    private static IReadOnlyList<EvidenceAlignmentSpeakerStat> SummarizeSpeakers(IReadOnlyList<TranscriptSegment> segments)
    {
        return segments
            .Where(segment => !string.IsNullOrEmpty(segment.SpeakerId))
            .GroupBy(segment => segment.SpeakerId!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                double total = 0;
                foreach (var segment in group)
                {
                    if (segment.StartSeconds is not null && segment.EndSeconds is not null)
                    {
                        total += Math.Max(0, segment.EndSeconds.Value - segment.StartSeconds.Value);
                    }
                }

                return new EvidenceAlignmentSpeakerStat(
                    SpeakerId: group.Key,
                    SegmentCount: group.Count(),
                    TotalSeconds: total);
            })
            .OrderByDescending(stat => stat.TotalSeconds)
            .ThenBy(stat => stat.SpeakerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool SegmentStartsInWindow(TranscriptSegment segment, double start, double end)
    {
        var s = segment.StartSeconds ?? start;
        return s >= start && s < end;
    }

    private static bool SegmentIntersectsRange(TranscriptSegment segment, double start, double end)
    {
        var segmentStart = segment.StartSeconds ?? start;
        var segmentEnd = segment.EndSeconds ?? segmentStart;
        return segmentEnd >= start && segmentStart <= end;
    }

    private static bool IntersectsRange((double Start, double End) window, double start, double end)
    {
        return window.End >= start && window.Start <= end;
    }
}

public sealed record EvidenceAlignmentOptions();

public sealed record EvidenceAlignmentResult(
    string RunId,
    string ByChapterPath,
    string BySlidePath,
    EvidenceAlignmentByChapterDocument ByChapter,
    EvidenceAlignmentBySlideDocument BySlide,
    bool ChaptersLoaded);

public sealed record EvidenceAlignmentByChapterDocument(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAt,
    string View,
    IReadOnlyList<EvidenceAlignmentByChapterEntry> Chapters);

public sealed record EvidenceAlignmentByChapterEntry(
    int ChapterIndex,
    double StartSeconds,
    double EndSeconds,
    string Timestamp,
    string EndTimestamp,
    IReadOnlyList<string> SlideIds,
    IReadOnlyList<string> TranscriptSegmentIds,
    IReadOnlyList<EvidenceAlignmentSpeakerStat> Speakers,
    IReadOnlyList<string> OcrFrameIds,
    IReadOnlyList<string> VisionFrameIds);

public sealed record EvidenceAlignmentBySlideDocument(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAt,
    string View,
    IReadOnlyList<EvidenceAlignmentBySlideEntry> Slides);

public sealed record EvidenceAlignmentBySlideEntry(
    string SlideId,
    double FirstSeenSeconds,
    double LastSeenSeconds,
    double WindowStartSeconds,
    double WindowEndSeconds,
    string FirstSeenLabel,
    string LastSeenLabel,
    string PrimaryFrameId,
    IReadOnlyList<string> FrameIds,
    OcrFrameResult? Ocr,
    VisionFrameResult? Vision,
    IReadOnlyList<string> TranscriptSegmentIds,
    IReadOnlyList<EvidenceAlignmentSpeakerStat> Speakers,
    IReadOnlyList<int> ChapterIndices);

public sealed record EvidenceAlignmentSpeakerStat(
    string SpeakerId,
    int SegmentCount,
    double TotalSeconds);
