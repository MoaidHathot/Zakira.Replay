using System.Diagnostics;

namespace Zakira.Replay.Core;

/// <summary>
/// Collects per-stage wall-clock timings for a single <see cref="AnalysisPipeline"/> run and
/// emits them as a stable artifact (<c>manifest.timings</c>) so orchestrators can branch on
/// "this stage took too long" without scraping log output. Thread-safe for concurrent stage
/// updates within a single run.
/// </summary>
/// <remarks>
/// Timings are wall-clock <see cref="TimeSpan"/>s measured between <see cref="Measure"/> entry
/// and <see cref="IDisposable.Dispose"/>. Multiple invocations of the same stage name are
/// summed — useful for stages that run in a loop (e.g. per-chunk STT, per-frame OCR). The
/// shape is intentionally additive: when a stage didn't run, it's simply absent from the map.
/// </remarks>
public sealed class RunTimings
{
    private readonly object lockObj = new();
    private readonly Dictionary<string, double> stages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch overall = Stopwatch.StartNew();

    /// <summary>
    /// Wall-clock seconds from the pipeline kick-off to the moment <see cref="ToArtifact"/> is
    /// called. Always emitted as <c>timings.totalSeconds</c>.
    /// </summary>
    public double TotalSeconds => overall.Elapsed.TotalSeconds;

    /// <summary>
    /// Begin measuring a stage. Use with <c>using var _ = timings.Measure("stt");</c> to record
    /// wall-clock until the using block exits. The same stage name can be measured repeatedly;
    /// values accumulate so a stage that runs N times reports the total wall-clock across
    /// invocations.
    /// </summary>
    public IDisposable Measure(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("Stage name must be non-empty.", nameof(stage));
        }

        return new StageMeasurement(this, stage, Stopwatch.StartNew());
    }

    /// <summary>
    /// Add a pre-measured duration for a stage. Useful for stages whose timings come from an
    /// external source (e.g. the per-chunk timings returned by Whisper.net).
    /// </summary>
    public void Add(string stage, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return;
        }

        var seconds = Math.Max(0, duration.TotalSeconds);
        lock (lockObj)
        {
            stages[stage] = stages.TryGetValue(stage, out var existing) ? existing + seconds : seconds;
        }
    }

    /// <summary>
    /// Snapshot of recorded stages. The returned dictionary is a defensive copy so callers can
    /// iterate without holding the internal lock.
    /// </summary>
    public IReadOnlyDictionary<string, double> Stages
    {
        get
        {
            lock (lockObj)
            {
                return new Dictionary<string, double>(stages, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Build the artifact-shaped record persisted on <c>manifest.timings</c>. Stage durations
    /// are rounded to milliseconds (3 decimal places) for stable serialisation.
    /// </summary>
    public RunTimingsArtifact ToArtifact()
    {
        IReadOnlyDictionary<string, double> snapshot;
        lock (lockObj)
        {
            snapshot = new Dictionary<string, double>(stages, StringComparer.OrdinalIgnoreCase);
        }

        var rounded = new Dictionary<string, double>(snapshot.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot)
        {
            rounded[entry.Key] = Math.Round(entry.Value, 3);
        }

        return new RunTimingsArtifact(
            TotalSeconds: Math.Round(overall.Elapsed.TotalSeconds, 3),
            Stages: rounded);
    }

    private sealed class StageMeasurement : IDisposable
    {
        private readonly RunTimings owner;
        private readonly string stage;
        private readonly Stopwatch watch;
        private bool stopped;

        public StageMeasurement(RunTimings owner, string stage, Stopwatch watch)
        {
            this.owner = owner;
            this.stage = stage;
            this.watch = watch;
        }

        public void Dispose()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            watch.Stop();
            owner.Add(stage, watch.Elapsed);
        }
    }
}

/// <summary>
/// Serialised shape of <see cref="RunTimings"/> embedded in <c>manifest.timings</c>. All values
/// are seconds. The <see cref="Stages"/> dictionary is intentionally open — adding a new stage
/// name does not bump <c>manifest.schemaVersion</c>; the JSON Schema admits any string key with
/// a non-negative number value.
/// </summary>
public sealed record RunTimingsArtifact(
    double TotalSeconds,
    IReadOnlyDictionary<string, double> Stages);

/// <summary>
/// Well-known stage identifiers used inside <see cref="AnalysisPipeline"/>. Exposed so
/// orchestrators and tests can match exact strings; values are part of the public artifact
/// contract.
/// </summary>
public static class RunTimingStages
{
    public const string Probe = "probe";
    public const string Captions = "captions";
    public const string Audio = "audio";
    public const string Stt = "stt";
    public const string Diarization = "diarization";
    public const string Frames = "frames";
    public const string SmartCrop = "smart-crop";
    public const string Slides = "slides";
    public const string Ocr = "ocr";
    public const string Vision = "vision";
    public const string Evidence = "evidence";
    public const string ManifestWrite = "manifest-write";
}
