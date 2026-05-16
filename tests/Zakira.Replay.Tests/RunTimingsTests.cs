using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class RunTimingsTests
{
    [Fact]
    public void MeasureBlockRecordsElapsedDurationForStage()
    {
        var timings = new RunTimings();
        using (timings.Measure(RunTimingStages.Stt))
        {
            Thread.Sleep(15);
        }

        var snapshot = timings.Stages;
        Assert.True(snapshot.ContainsKey(RunTimingStages.Stt));
        // 15ms minimum; can be much higher under CI load. Just assert non-zero.
        Assert.True(snapshot[RunTimingStages.Stt] >= 0.010, $"expected >= 0.010s, got {snapshot[RunTimingStages.Stt]}");
    }

    [Fact]
    public void MeasureBlockSumsRepeatedInvocationsOfSameStage()
    {
        // A stage that runs in a loop (e.g. per-chunk STT) accumulates total wall-clock.
        var timings = new RunTimings();
        for (var i = 0; i < 3; i++)
        {
            using (timings.Measure(RunTimingStages.Ocr))
            {
                Thread.Sleep(5);
            }
        }

        var snapshot = timings.Stages;
        Assert.Single(snapshot);
        Assert.True(snapshot[RunTimingStages.Ocr] >= 0.015, $"expected >= 0.015s across 3 invocations, got {snapshot[RunTimingStages.Ocr]}");
    }

    [Fact]
    public void AddRecordsPreMeasuredDurationsAndAccumulates()
    {
        var timings = new RunTimings();
        timings.Add("custom-stage", TimeSpan.FromSeconds(0.5));
        timings.Add("custom-stage", TimeSpan.FromSeconds(0.25));

        Assert.Equal(0.75, timings.Stages["custom-stage"], precision: 3);
    }

    [Fact]
    public void AddIgnoresEmptyStageNameSoCallersDoNotPolluteTheMap()
    {
        var timings = new RunTimings();
        timings.Add("", TimeSpan.FromSeconds(1));
        timings.Add("   ", TimeSpan.FromSeconds(2));

        Assert.Empty(timings.Stages);
    }

    [Fact]
    public void AddClampsNegativeDurationsToZero()
    {
        var timings = new RunTimings();
        timings.Add("weird", TimeSpan.FromSeconds(-3));

        Assert.Equal(0, timings.Stages["weird"]);
    }

    [Fact]
    public void StagesSnapshotIsADefensiveCopy()
    {
        // Snapshot must not change after we record more stages, so callers can iterate the
        // returned dict without worrying about concurrent producer mutations.
        var timings = new RunTimings();
        timings.Add("a", TimeSpan.FromSeconds(1));
        var snapshot = timings.Stages;

        timings.Add("b", TimeSpan.FromSeconds(1));

        Assert.Single(snapshot);
        Assert.Equal(2, timings.Stages.Count);
    }

    [Fact]
    public void ToArtifactRoundsStageDurationsToMilliseconds()
    {
        var timings = new RunTimings();
        timings.Add(RunTimingStages.Stt, TimeSpan.FromSeconds(1.2345678));
        timings.Add(RunTimingStages.Ocr, TimeSpan.FromSeconds(0.0009));

        var artifact = timings.ToArtifact();

        Assert.Equal(1.235, artifact.Stages[RunTimingStages.Stt]);
        Assert.Equal(0.001, artifact.Stages[RunTimingStages.Ocr]);
        Assert.True(artifact.TotalSeconds >= 0);
    }

    [Fact]
    public void ToArtifactSerialisesAsCamelCaseTotalSecondsPlusStagesMap()
    {
        // The manifest.timings schema says totalSeconds:number and stages:{string:number}.
        // Verify the JSON shape directly so a future System.Text.Json default change can't
        // silently break manifest validation.
        var timings = new RunTimings();
        timings.Add(RunTimingStages.Probe, TimeSpan.FromSeconds(0.5));

        var artifact = timings.ToArtifact();
        var json = JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("totalSeconds", out var total));
        Assert.True(total.GetDouble() >= 0);
        Assert.True(doc.RootElement.TryGetProperty("stages", out var stages));
        Assert.True(stages.TryGetProperty("probe", out var probeValue));
        Assert.Equal(0.5, probeValue.GetDouble(), precision: 3);
    }

    [Fact]
    public void TotalSecondsIsNonZeroAfterFirstMeasurement()
    {
        var timings = new RunTimings();
        Thread.Sleep(15);
        Assert.True(timings.TotalSeconds >= 0.010);
    }

    [Fact]
    public void StageNamesAreCaseInsensitive()
    {
        var timings = new RunTimings();
        timings.Add("STT", TimeSpan.FromSeconds(0.1));
        timings.Add("stt", TimeSpan.FromSeconds(0.1));
        timings.Add("Stt", TimeSpan.FromSeconds(0.1));

        // All three should accumulate into a single key (case-insensitive lookup).
        Assert.Single(timings.Stages);
        Assert.Equal(0.3, timings.Stages.Values.First(), precision: 3);
    }

    [Fact]
    public void DisposingMeasurementTwiceIsSafe()
    {
        var timings = new RunTimings();
        var scope = timings.Measure(RunTimingStages.Stt);
        scope.Dispose();
        scope.Dispose();  // second dispose must be a no-op

        Assert.Single(timings.Stages);
    }

    [Fact]
    public void RunTimingStagesCanonicalNamesAreLowercaseKebabAndStable()
    {
        // These strings are part of the public artifact contract — orchestrators may pattern
        // match against them. Lock them in so a refactor doesn't accidentally rename a stage.
        Assert.Equal("probe", RunTimingStages.Probe);
        Assert.Equal("captions", RunTimingStages.Captions);
        Assert.Equal("audio", RunTimingStages.Audio);
        Assert.Equal("stt", RunTimingStages.Stt);
        Assert.Equal("diarization", RunTimingStages.Diarization);
        Assert.Equal("frames", RunTimingStages.Frames);
        Assert.Equal("smart-crop", RunTimingStages.SmartCrop);
        Assert.Equal("slides", RunTimingStages.Slides);
        Assert.Equal("ocr", RunTimingStages.Ocr);
        Assert.Equal("vision", RunTimingStages.Vision);
        Assert.Equal("evidence", RunTimingStages.Evidence);
        Assert.Equal("manifest-write", RunTimingStages.ManifestWrite);
    }
}
