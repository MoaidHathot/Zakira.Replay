using System.Text.Json;
using Zakira.Replay.Cli;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Tests for the <c>--output-format json</c> contract on the long-running pipeline
/// commands (<c>analyze</c>, <c>transcribe</c>, <c>clip</c>, <c>batch run</c>,
/// <c>queue enqueue</c>, <c>queue run</c>). Pre-fix these commands silently ignored
/// the flag and dumped human-readable text on stdout; the new behaviour is to emit a
/// single JSON envelope on stdout (and route progress lines to stderr so stdout stays
/// parseable). See README.md "analyze --output-format json" for the documented shape.
/// </summary>
public sealed class CliJsonOutputTests
{
    /// <summary>
    /// Scoped override of <c>ZAKIRA_REPLAY_RUNS_DIRECTORY</c>. The CLI resolves the runs
    /// root in the precedence order env-var → config → <c>&lt;cwd&gt;/runs</c>; on dev
    /// machines with a real user config (XDG_CONFIG_HOME or AppData) the config wins, so
    /// just changing cwd is not enough to isolate test runs. Setting the env var pins the
    /// resolution to a fresh per-test temp directory and the dispose restores the prior
    /// value so we don't leak into the host machine's run history.
    /// </summary>
    private sealed class IsolatedRunsRoot : IDisposable
    {
        private readonly string? previous;

        public IsolatedRunsRoot(string runsDirectory)
        {
            previous = Environment.GetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable, runsDirectory);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ArtifactStore.RunsDirectoryEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// Unit-level check on the envelope shape. We bypass the full pipeline (which depends
    /// on yt-dlp/ffmpeg) by constructing a synthetic <see cref="AnalyzeResult"/> with every
    /// optional artifact path populated and a representative warning, then assert every
    /// documented property is present and absolute paths are resolved against the run
    /// directory.
    /// </summary>
    [Fact]
    public void BuildAnalyzeJsonEnvelopeResolvesAllPathsAndPreservesWarnings()
    {
        using var temp = new TestTempDirectory();
        var run = new VideoRun("synthetic-run-id", temp.Path);
        var manifest = new ArtifactManifest(
            SchemaVersion: "0.8",
            Source: "https://example.test/video",
            VisionInstruction: "instr-v",
            OcrInstruction: "instr-o",
            CreatedAt: DateTimeOffset.UnixEpoch,
            RunId: run.Id,
            Title: "Example Title",
            WebpageUrl: "https://example.test/video",
            Duration: "00:01:30",
            AudioPath: "audio/audio.wav",
            TranscriptPath: "transcript.md",
            OcrPath: "ocr/combined.md",
            VisionPath: "vision/combined.md",
            EvidencePath: "evidence.json",
            Frames: [new FrameArtifact("frame-001", "frames/frame-001.jpg", 1.5, "00:01")],
            Warnings: [new ReplayWarning(ReplayWarningCodes.OcrParseFallback, "fallback message", Source: "ocr", Severity: ReplayWarningSeverities.Warning)]);
        var result = new AnalyzeResult(run, manifest, Reused: false);

        // Round-trip through JSON to verify both the envelope shape and that
        // CliJson.Options can serialise the warnings/optional fields cleanly.
        var json = JsonSerializer.Serialize(CliApp.BuildAnalyzeJsonEnvelope(result), CliJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Every property in the documented contract must be present.
        Assert.Equal("synthetic-run-id", root.GetProperty("runId").GetString());
        Assert.False(root.GetProperty("reused").GetBoolean());
        Assert.Equal(run.Directory, root.GetProperty("artifactDirectory").GetString());
        Assert.Equal(run.GetPath("manifest.json"), root.GetProperty("manifestPath").GetString());
        Assert.Equal(run.GetPath("evidence.json"), root.GetProperty("evidencePath").GetString());
        Assert.Equal(run.GetPath("transcript.md"), root.GetProperty("transcriptPath").GetString());
        Assert.Equal(run.GetPath("audio/audio.wav"), root.GetProperty("audioPath").GetString());
        Assert.Equal(run.GetPath("ocr/combined.md"), root.GetProperty("ocrPath").GetString());
        Assert.Equal(run.GetPath("vision/combined.md"), root.GetProperty("visionPath").GetString());
        Assert.Equal(1, root.GetProperty("frameCount").GetInt32());
        Assert.Equal("Example Title", root.GetProperty("title").GetString());
        Assert.Equal("https://example.test/video", root.GetProperty("webpageUrl").GetString());
        Assert.Equal("00:01:30", root.GetProperty("duration").GetString());
        Assert.Equal("https://example.test/video", root.GetProperty("source").GetString());

        var warnings = root.GetProperty("warnings").EnumerateArray().ToArray();
        Assert.Single(warnings);
        Assert.Equal(ReplayWarningCodes.OcrParseFallback, warnings[0].GetProperty("code").GetString());
        Assert.Equal(ReplayWarningSeverities.Warning, warnings[0].GetProperty("severity").GetString());
    }

    /// <summary>
    /// Companion to the success-case check above: when artifact paths are null on the
    /// manifest (e.g. <c>--no-transcript</c> or no OCR was run), the envelope must
    /// preserve them as null rather than fabricate a synthetic path or omit the property.
    /// Stable property set matters for orchestrators that key off it.
    /// </summary>
    [Fact]
    public void BuildAnalyzeJsonEnvelopeKeepsNullPathsAsNull()
    {
        using var temp = new TestTempDirectory();
        var run = new VideoRun("synthetic-run-id", temp.Path);
        var manifest = new ArtifactManifest(
            SchemaVersion: "0.8",
            Source: "local.mp4",
            VisionInstruction: "",
            OcrInstruction: "",
            CreatedAt: DateTimeOffset.UnixEpoch,
            RunId: run.Id,
            Title: null,
            WebpageUrl: null,
            Duration: null,
            AudioPath: null,
            TranscriptPath: null,
            OcrPath: null,
            VisionPath: null,
            EvidencePath: null,
            Frames: [],
            Warnings: []);
        var result = new AnalyzeResult(run, manifest, Reused: true);

        var json = JsonSerializer.Serialize(CliApp.BuildAnalyzeJsonEnvelope(result), CliJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("reused").GetBoolean());
        Assert.Equal(0, root.GetProperty("frameCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("evidencePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("transcriptPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("audioPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ocrPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("visionPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("webpageUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("duration").ValueKind);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    /// <summary>
    /// End-to-end check for the <c>analyze</c> command's JSON output mode. Uses a local
    /// file source with <c>--frames 0 --frame-strategy interval</c> so the run skips
    /// yt-dlp/ffmpeg entirely. Pre-fix the same invocation would dump multiple plain-text
    /// lines on stdout and the asserted JSON.Parse would throw.
    /// </summary>
    [Fact]
    public async Task AnalyzeWithOutputFormatJsonEmitsSingleEnvelopeOnStdoutAndProgressOnStderr()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var runIdValue = "json-analyze-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(
            ["analyze", sourcePath, "--frames", "0", "--frame-strategy", "interval", "--run-id", runIdValue, "--output-format", "json"],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.True(exitCode == 0, $"Expected exit 0 but got {exitCode}. stdout=[{stdout}] stderr=[{stderr}]");

        // stdout must be a single, parseable JSON envelope — not text-with-progress.
        var stdoutText = stdout.ToString().Trim();
        using var document = JsonDocument.Parse(stdoutText);
        var root = document.RootElement;
        Assert.Equal(runIdValue, root.GetProperty("runId").GetString());
        Assert.True(root.TryGetProperty("artifactDirectory", out _));
        Assert.True(root.TryGetProperty("manifestPath", out _));
        Assert.True(root.TryGetProperty("frameCount", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(File.Exists(root.GetProperty("manifestPath").GetString()));
        // Artifact directory must sit under the isolated runs root, not leak into the
        // host machine's real runs directory.
        Assert.StartsWith(temp.Path, root.GetProperty("artifactDirectory").GetString(), StringComparison.OrdinalIgnoreCase);

        // Progress text from the pipeline (e.g. "Run directory: …") must land on stderr
        // in JSON mode so stdout can stay parseable for the orchestrator.
        Assert.Contains("Run directory", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Same shape as the analyze test above but for <c>transcribe</c> (which is the
    /// "frames=0" alias). Verifies the JSON envelope is emitted regardless of which
    /// frames-disabled entry-point the user picks.
    /// </summary>
    [Fact]
    public async Task TranscribeWithOutputFormatJsonEmitsSingleEnvelopeOnStdout()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var runIdValue = "json-transcribe-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(
            ["transcribe", sourcePath, "--frame-strategy", "interval", "--run-id", runIdValue, "--output-format", "json"],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.True(exitCode == 0, $"Expected exit 0 but got {exitCode}. stdout=[{stdout}] stderr=[{stderr}]");
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        Assert.Equal(runIdValue, document.RootElement.GetProperty("runId").GetString());
    }

    /// <summary>
    /// ndjson currently behaves identically to json for one-shot commands like analyze
    /// (single envelope on stdout). This pins that behaviour so we don't accidentally
    /// break it later: ndjson consumers should be able to use the same envelope shape
    /// until streaming progress events become a documented contract.
    /// </summary>
    [Fact]
    public async Task AnalyzeNdjsonModeIsTreatedAsJson()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var runIdValue = "ndjson-analyze-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(
            ["analyze", sourcePath, "--frames", "0", "--frame-strategy", "interval", "--run-id", runIdValue, "--output-format", "ndjson"],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        Assert.Equal(runIdValue, document.RootElement.GetProperty("runId").GetString());
    }

    /// <summary>
    /// Default text output still surfaces the run id, artifact directory, and manifest path
    /// for orchestrators that grep stdout. 0.14 replaced the verbose "Completed run: …" line
    /// with a compact "Done in &lt;elapsed&gt;. &lt;id&gt; …" header, but the run id and the
    /// "Artifacts:" / "Manifest:" labels remain so existing scripts keep working.
    /// </summary>
    [Fact]
    public async Task AnalyzeWithoutOutputFormatStillEmitsLegacyTextLines()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var runIdValue = "text-analyze-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(
            ["analyze", sourcePath, "--frames", "0", "--frame-strategy", "interval", "--run-id", runIdValue],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var stdoutText = stdout.ToString();
        // 0.14 changed the summary format: "Completed run: <id>" was replaced with the
        // compact "Done in <elapsed>. <id> [— pieces]" line. The run id, "Artifacts:", and
        // "Manifest:" labels are still present so scripts that grep these strings keep working.
        Assert.Contains("Done in", stdoutText, StringComparison.Ordinal);
        Assert.Contains(runIdValue, stdoutText, StringComparison.Ordinal);
        Assert.Contains("Artifacts:", stdoutText, StringComparison.Ordinal);
        Assert.Contains("Manifest:", stdoutText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>queue enqueue --output-format json</c> previously dropped the flag and printed
    /// three plain-text lines. New behaviour: a single JSON envelope on stdout exposing
    /// the queue id, job id, queue directory, and the persisted <see cref="AnalysisQueueJob"/>.
    /// </summary>
    [Fact]
    public async Task QueueEnqueueWithOutputFormatJsonEmitsEnvelope()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var jobIdValue = "cli-json-job-" + Guid.NewGuid().ToString("N");
        var queueIdValue = "cli-json-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApp.RunAsync(
            ["queue", "enqueue", sourcePath, "--queue-id", queueIdValue, "--job-id", jobIdValue, "--frames", "0", "--frame-strategy", "interval", "--no-transcript", "--output-format", "json"],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        var root = document.RootElement;
        Assert.Equal(queueIdValue, root.GetProperty("queueId").GetString());
        Assert.Equal(jobIdValue, root.GetProperty("jobId").GetString());
        Assert.True(root.TryGetProperty("queueDirectory", out _));
        Assert.True(root.TryGetProperty("job", out var job));
        Assert.Equal(jobIdValue, job.GetProperty("jobId").GetString());
    }

    /// <summary>
    /// <c>queue run --output-format json</c> emits the full <see cref="AnalysisQueueRunResult"/>
    /// on stdout (the same record that's already persisted to
    /// <c>last-run-result.json</c>) and routes pipeline progress to stderr so stdout
    /// stays parseable.
    /// </summary>
    [Fact]
    public async Task QueueRunWithOutputFormatJsonEmitsEnvelopeAndProgressOnStderr()
    {
        using var temp = new TestTempDirectory();
        using var runsRoot = new IsolatedRunsRoot(temp.GetPath("runs"));
        var sourcePath = temp.GetPath("source.mp4");
        await File.WriteAllTextAsync(sourcePath, "not real video", CancellationToken.None);
        var jobIdValue = "cli-run-json-job-" + Guid.NewGuid().ToString("N");
        var queueIdValue = "cli-run-json-" + Guid.NewGuid().ToString("N");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var enqueueExit = await CliApp.RunAsync(
            ["queue", "enqueue", sourcePath, "--queue-id", queueIdValue, "--job-id", jobIdValue, "--frames", "0", "--frame-strategy", "interval", "--no-transcript"],
            stdout,
            stderr,
            CancellationToken.None);
        Assert.Equal(0, enqueueExit);

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();

        var runExit = await CliApp.RunAsync(
            ["queue", "run", "--queue-id", queueIdValue, "--concurrency", "1", "--output-format", "json"],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, runExit);
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        var root = document.RootElement;
        Assert.Equal(queueIdValue, root.GetProperty("queueId").GetString());
        Assert.Equal(1, root.GetProperty("attempted").GetInt32());
        Assert.Equal(1, root.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.True(root.TryGetProperty("jobs", out var jobs));
        Assert.Single(jobs.EnumerateArray());
    }
}
