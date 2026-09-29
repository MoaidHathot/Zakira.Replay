using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// OCR ran once per slide but its text was never compared across slides, so a narration-free
/// video ("REST FOR 15 MINUTES", "FERMENT FOR 2-2.5 HOURS" as overlays) had no transcript
/// equivalent and slide decks repeated the same lines per frame. <see cref="OcrStepBuilder"/>
/// folds near-duplicate lines (OCR noise included) into one timestamped entry each.
/// </summary>
public sealed class OcrStepBuilderTests
{
    private static OcrFrameResult Frame(string id, double t, params string[] lines) => new(
        FrameId: id,
        FramePath: $"frames/{id}.jpg",
        TimestampSeconds: t,
        TimestampLabel: Timestamp.Format(t),
        Text: string.Join('\n', lines),
        Structured: new OcrFrameStructured(string.Join('\n', lines), lines, []));

    [Fact]
    public void OverlaysCollapseAcrossFramesWithNoiseAndReappearAsNewStepsAfterTheWindow()
    {
        var frames = new[]
        {
            Frame("frame-001", 10, "REST FOR 15 MINUTES"),
            Frame("frame-002", 15, "REST F0R 15 MINUTES"),          // OCR noise: 0 for O
            Frame("frame-003", 20, "REST FOR 15 MINUTES."),          // trailing punctuation
            Frame("frame-004", 25, "rest for 15 minutes", "ChainBaker"),
            Frame("frame-005", 60, "FERMENT FOR 2-2.5 HOURS", "ChainBaker"),
            Frame("frame-006", 65, "FERMENT FOR 2 - 2.5 HOURS"),
            Frame("frame-007", 400, "REST FOR 15 MINUTES"),          // > 90 s later: a new step
            Frame("frame-008", 405, "—", "ab"),                      // no letters / too short: dropped
        };

        var list = OcrStepBuilder.Build(frames);

        Assert.Equal(8, list.FrameCount);
        Assert.Equal(4, list.StepCount);
        var rest = list.Steps[0];
        Assert.Equal("REST FOR 15 MINUTES", rest.Text);
        Assert.Equal((10.0, 25.0, 4), (rest.FirstSeenSeconds, rest.LastSeenSeconds, rest.Occurrences));
        Assert.Equal(["frame-001", "frame-002", "frame-003", "frame-004"], rest.FrameIds);
        Assert.Equal("00:10", rest.FirstSeenLabel);

        var chainBaker = list.Steps[1];
        Assert.Equal("ChainBaker", chainBaker.Text);
        Assert.Equal(2, chainBaker.Occurrences);

        var ferment = list.Steps[2];
        Assert.StartsWith("FERMENT FOR 2", ferment.Text);
        Assert.Equal(2, ferment.Occurrences);

        var restAgain = list.Steps[3];
        Assert.Equal("REST FOR 15 MINUTES", restAgain.Text);
        Assert.Equal(400, restAgain.FirstSeenSeconds);
        Assert.Equal(1, restAgain.Occurrences);
        Assert.Equal(list.Steps.Count, list.Steps.Select(s => s.Index).Distinct().Count());
        Assert.Equal([1, 2, 3, 4], list.Steps.Select(s => s.Index).ToArray());
    }

    [Fact]
    public void RealisticOcrNoiseOnLongLinesStillMerges()
    {
        // Two frames of the same tweet; the second is cut off and mangled.
        var frames = new[]
        {
            Frame("frame-001", 4, "ThePrimeagen", "My continual disgust at the JS ecosystem feels vindicated practically daily at this point", "7:22 AM - May 12, 2026 - 103.7K Views"),
            Frame("frame-002", 14, "ThePrimeagen", "My continual disgust at the JS ecosystem feels vindicated practically daily at this p0int", "7:22 AM · May 12, 2026 · 103.7K Views"),
        };

        var list = OcrStepBuilder.Build(frames);

        Assert.Equal(3, list.StepCount);
        Assert.All(list.Steps, step => Assert.Equal(2, step.Occurrences));
        Assert.Contains(list.Steps, step => step.Text.StartsWith("My continual disgust", StringComparison.Ordinal) && step.Text.EndsWith("at this point", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentLinesStaySeparate()
    {
        var frames = new[]
        {
            Frame("frame-001", 1, "Add the flour", "Mix well"),
            Frame("frame-002", 2, "Add the water", "Knead for 10 minutes"),
        };

        var list = OcrStepBuilder.Build(frames);

        Assert.Equal(4, list.StepCount);
    }

    [Fact]
    public void FallsBackToFreeTextLinesWithoutStructuredOutput()
    {
        var frame = new OcrFrameResult("frame-001", "frames/frame-001.jpg", 3, "00:03", "line one\nline two\n\nline one");

        var list = OcrStepBuilder.Build([frame]);

        Assert.Equal(2, list.StepCount);
        Assert.Equal(3, list.LineCount);
        Assert.Equal(2, list.Steps[0].Occurrences);
    }

    [Fact]
    public void MarkdownListsStepsWithRanges()
    {
        var list = OcrStepBuilder.Build(
        [
            Frame("frame-001", 10, "REST FOR 15 MINUTES"),
            Frame("frame-002", 25, "REST FOR 15 MINUTES"),
            Frame("frame-003", 60, "FERMENT FOR 2-2.5 HOURS"),
        ]);

        var markdown = OcrStepBuilder.ToMarkdown(list);

        Assert.Contains("2 distinct line(s) from 3 OCR line(s) across 3 frame(s).", markdown);
        Assert.Contains("- **[00:10 - 00:25]** REST FOR 15 MINUTES", markdown);
        Assert.Contains("- **[01:00]** FERMENT FOR 2-2.5 HOURS", markdown);
    }

    [Theory]
    [InlineData("kitten", "sitting", 0.571)]
    [InlineData("same", "same", 1.0)]
    [InlineData("", "", 1.0)]
    [InlineData("abc", "", 0.0)]
    public void CharacterSimilarityIsNormalisedLevenshtein(string a, string b, double expected)
    {
        Assert.Equal(expected, Math.Round(OcrStepBuilder.CharacterSimilarity(a, b), 3));
    }

    [Fact]
    public void KeysStripCaseAndPunctuation()
    {
        Assert.Equal("rest for 15 minutes", OcrStepBuilder.Key(OcrStepBuilder.Normalize("  REST   FOR  15 MINUTES. ")));
        Assert.Equal("7 22 am may 12 2026 103 7k views", OcrStepBuilder.Key("7:22 AM · May 12, 2026 · 103.7K Views"));
    }
}
