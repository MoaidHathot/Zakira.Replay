using System.Text;
using Zakira.Replay.Cli;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Covers the 0.14 "Just Works" UX changes: host-aware <c>auto</c> capture, default flag
/// flips, demoted warning severities, short session-coded run ids, and the
/// <c>--verbose</c> / <c>--quiet</c> verbosity rendering.
/// </summary>
public sealed class UxImprovementsTests
{
    // KnownHosts ----------------------------------------------------------------------------

    [Theory]
    [InlineData("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions", true)]
    [InlineData("https://build.microsoft.com/en-US/sessions/KEY01", true)]
    [InlineData("https://medius.studios.ms/Embed/video-nc/KEY01", true)]
    [InlineData("https://medius.microsoft.com/Embed/video/abc123", true)]
    [InlineData("https://mediaplatform.event.microsoft.com/...", false)] // wrong prefix
    [InlineData("https://medius2024.event.microsoft.com/Embed/video/x", true)]
    [InlineData("https://mediastream.microsoft.com/embed/foo", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://example.com/video.mp4", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void IsBrowserOnly_RecognisesKnownHosts(string source, bool expected)
    {
        Assert.Equal(expected, KnownHosts.IsBrowserOnly(source));
    }

    [Theory]
    [InlineData("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions", "brk230")]
    [InlineData("https://build.microsoft.com/en-US/sessions/KEY01", "key01")]
    [InlineData("https://build.microsoft.com/fr-FR/sessions/BRK101/", "brk101")]
    [InlineData("https://medius.studios.ms/Embed/video-nc/KEY01", "key01")]
    [InlineData("https://medius.microsoft.com/Embed/video/abcDEF", "abcdef")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dqw4w9wgxcq")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dqw4w9wgxcq")]
    [InlineData("https://example.com/video.mp4", null)]
    [InlineData("https://build.microsoft.com/", null)]
    [InlineData("", null)]
    public void TryExtractSessionCode_ReturnsExpectedSlug(string source, string? expected)
    {
        Assert.Equal(expected, KnownHosts.TryExtractSessionCode(source));
    }

    // ArtifactStore short run-id ------------------------------------------------------------

    [Fact]
    public void CreateDeterministicRunId_UsesSessionCodeForKnownHosts()
    {
        var id = ArtifactStore.CreateDeterministicRunId("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions");
        Assert.StartsWith("brk230-", id, StringComparison.Ordinal);
        // 8-byte SHA-256 prefix (4 bytes hex-encoded = 8 chars) appended after the dash.
        var hashPart = id["brk230-".Length..];
        Assert.Equal(8, hashPart.Length);
        Assert.Matches("^[0-9a-f]+$", hashPart);
    }

    [Fact]
    public void CreateDeterministicRunId_IsDeterministicAcrossCalls()
    {
        var a = ArtifactStore.CreateDeterministicRunId("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions");
        var b = ArtifactStore.CreateDeterministicRunId("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions");
        Assert.Equal(a, b);
    }

    [Fact]
    public void CreateDeterministicRunId_DifferentUrlsProduceDifferentIds()
    {
        var a = ArtifactStore.CreateDeterministicRunId("https://build.microsoft.com/en-US/sessions/BRK230?source=sessions");
        var b = ArtifactStore.CreateDeterministicRunId("https://build.microsoft.com/en-US/sessions/BRK231?source=sessions");
        Assert.NotEqual(a, b);
        Assert.StartsWith("brk230-", a, StringComparison.Ordinal);
        Assert.StartsWith("brk231-", b, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDeterministicRunId_FallsBackToSlugForUnknownHosts()
    {
        var id = ArtifactStore.CreateDeterministicRunId("https://example.com/some/long/path/to/a/video.mp4");
        // No session-code match → slug+hash form, max-40 slug, dash-separated 8-hex hash.
        Assert.Contains("example-com", id, StringComparison.Ordinal);
        Assert.Matches("-[0-9a-f]{8}$", id);
    }

    // Default value flips -------------------------------------------------------------------

    [Fact]
    public void CaptureConfig_DefaultModeIsAuto()
    {
        var config = new CaptureConfig();
        Assert.Equal(CaptureModes.Auto, config.Mode);
    }

    [Fact]
    public void AnalyzeRequest_DefaultFrameStrategyIsInterval()
    {
        var request = new AnalyzeRequest(
            Source: "https://example.com/video.mp4",
            VisionInstruction: string.Empty,
            IncludeTranscript: false,
            FrameCount: 5,
            RunId: null);
        Assert.Equal(FrameSelectionStrategies.Interval, request.FrameStrategy);
    }

    // Verbosity rendering -------------------------------------------------------------------

    [Theory]
    [InlineData(CliVerbosity.Verbose, "info", true)]
    [InlineData(CliVerbosity.Verbose, "warning", true)]
    [InlineData(CliVerbosity.Verbose, "error", true)]
    [InlineData(CliVerbosity.Default, "info", false)]
    [InlineData(CliVerbosity.Default, "warning", true)]
    [InlineData(CliVerbosity.Default, "error", true)]
    [InlineData(CliVerbosity.Quiet, "info", false)]
    [InlineData(CliVerbosity.Quiet, "warning", false)]
    [InlineData(CliVerbosity.Quiet, "error", true)]
    public void ShouldRender_FiltersBySeverityAndVerbosity(CliVerbosity verbosity, string severity, bool expected)
    {
        Assert.Equal(expected, CliVerbosityHelpers.ShouldRender(verbosity, severity));
    }

    [Fact]
    public void RenderWarnings_DefaultModeSuppressesInfoSeverities()
    {
        using var stdout = new StringWriter();
        var warnings = new List<ReplayWarning>
        {
            new(ReplayWarningCodes.MediaUrlUnresolved, "noisy info", Severity: ReplayWarningSeverities.Info),
            new(ReplayWarningCodes.CaptureBrowserMediaDownloadFailed, "real warning", Severity: ReplayWarningSeverities.Warning),
        };
        CliApp.RenderWarnings(stdout, warnings, CliVerbosity.Default);
        var output = stdout.ToString();
        Assert.DoesNotContain("noisy info", output, StringComparison.Ordinal);
        Assert.Contains("real warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderWarnings_VerboseShowsAllSeverities()
    {
        using var stdout = new StringWriter();
        var warnings = new List<ReplayWarning>
        {
            new(ReplayWarningCodes.MediaUrlUnresolved, "noisy info", Severity: ReplayWarningSeverities.Info),
            new(ReplayWarningCodes.CaptureBrowserMediaDownloadFailed, "real warning", Severity: ReplayWarningSeverities.Warning),
        };
        CliApp.RenderWarnings(stdout, warnings, CliVerbosity.Verbose);
        var output = stdout.ToString();
        Assert.Contains("noisy info", output, StringComparison.Ordinal);
        Assert.Contains("real warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderWarnings_QuietOnlyShowsErrors()
    {
        using var stdout = new StringWriter();
        var warnings = new List<ReplayWarning>
        {
            new(ReplayWarningCodes.MediaUrlUnresolved, "info-text", Severity: ReplayWarningSeverities.Info),
            new(ReplayWarningCodes.CaptureBrowserMediaDownloadFailed, "warn-text", Severity: ReplayWarningSeverities.Warning),
            new(ReplayWarningCodes.FramesNoMedia, "error-text", Severity: ReplayWarningSeverities.Error),
        };
        CliApp.RenderWarnings(stdout, warnings, CliVerbosity.Quiet);
        var output = stdout.ToString();
        Assert.DoesNotContain("info-text", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warn-text", output, StringComparison.Ordinal);
        Assert.Contains("error-text", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderWarnings_OmitsEntireBlockWhenAllSuppressed()
    {
        using var stdout = new StringWriter();
        var warnings = new List<ReplayWarning>
        {
            new(ReplayWarningCodes.MediaUrlUnresolved, "info-only", Severity: ReplayWarningSeverities.Info),
        };
        CliApp.RenderWarnings(stdout, warnings, CliVerbosity.Default);
        // No "Warnings:" header should leak through when nothing passes the filter.
        Assert.Empty(stdout.ToString());
    }

    // Summary line --------------------------------------------------------------------------

    [Fact]
    public void BuildPipelineSummaryLine_IncludesElapsedAndRunId()
    {
        var run = new VideoRun("brk230-1ccc2f93", @"C:\runs\brk230-1ccc2f93");
        var manifest = MakeManifest(
            frameCount: 15,
            transcriptPath: "transcript.md");
        var result = new AnalyzeResult(run, manifest, Reused: false);

        var line = CliApp.BuildPipelineSummaryLine(result, TimeSpan.FromSeconds(54));
        Assert.StartsWith("Done in", line, StringComparison.Ordinal);
        Assert.Contains("brk230-1ccc2f93", line, StringComparison.Ordinal);
        Assert.Contains("15 frames", line, StringComparison.Ordinal);
        Assert.Contains("transcript: transcript.md", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPipelineSummaryLine_ReusedRunSaysReused()
    {
        var run = new VideoRun("brk230-1ccc2f93", @"C:\runs\brk230-1ccc2f93");
        var result = new AnalyzeResult(run, MakeManifest(), Reused: true);

        var line = CliApp.BuildPipelineSummaryLine(result, TimeSpan.FromMilliseconds(120));
        Assert.StartsWith("Reused in", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPipelineSummaryLine_FormatsLongElapsedAsMinutes()
    {
        var run = new VideoRun("brk230-1ccc2f93", @"C:\runs\brk230-1ccc2f93");
        var result = new AnalyzeResult(run, MakeManifest(), Reused: false);

        var line = CliApp.BuildPipelineSummaryLine(result, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7));
        Assert.Contains("3m07s", line, StringComparison.Ordinal);
    }

    private static ArtifactManifest MakeManifest(int frameCount = 0, string? transcriptPath = null)
    {
        var frames = Enumerable.Range(0, frameCount)
            .Select(i => new FrameArtifact($"frame-{i:D3}", $"frames/frame-{i:D3}.jpg", i * 10, $"00:00:{i * 10:D2}"))
            .ToArray();
        return new ArtifactManifest(
            SchemaVersion: "1",
            Source: "test",
            VisionInstruction: string.Empty,
            OcrInstruction: string.Empty,
            CreatedAt: DateTimeOffset.UtcNow,
            RunId: "test-run",
            Title: null,
            WebpageUrl: null,
            Duration: null,
            AudioPath: null,
            TranscriptPath: transcriptPath,
            OcrPath: null,
            VisionPath: null,
            EvidencePath: null,
            Frames: frames,
            Warnings: Array.Empty<ReplayWarning>());
    }
}
