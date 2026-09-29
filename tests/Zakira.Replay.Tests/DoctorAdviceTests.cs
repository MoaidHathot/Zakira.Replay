using Zakira.Replay.Cli;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// doctor used to reduce yt-dlp to "found / runnable within 15 s": the version string was thrown
/// away, a stale release (the #1 real-world cause of YouTube failures) looked healthy, the
/// JavaScript runtime yt-dlp needs for YouTube challenges was never checked, and a binary blocked
/// by an application-control policy showed only the first line of a two-line error.
/// </summary>
public sealed class DoctorAdviceTests
{
    [Theory]
    [InlineData("yt-dlp", "2026.08.19", "2026.08.19")]
    [InlineData("yt-dlp", "2026.08.19.232456", "2026.08.19.232456")]
    [InlineData("ffmpeg", "ffmpeg version N-124279-g0f6ba39122-20260430 Copyright (c) 2000-2026 the FFmpeg developers", "N-124279-g0f6ba39122-20260430")]
    [InlineData("ffprobe", "ffprobe version 7.1.1-essentials_build-www.gyan.dev Copyright (c) 2007-2025 the FFmpeg developers", "7.1.1-essentials_build-www.gyan.dev")]
    [InlineData("ffmpeg", "", null)]
    public void VersionLinesAreReducedToTheVersion(string dependency, string line, string? expected)
    {
        Assert.Equal(expected, DoctorAdvice.ParseVersionLine(dependency, line));
    }

    [Fact]
    public void StaleYtDlpGetsUpdateAdviceFreshOneDoesNot()
    {
        var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(DoctorAdvice.ForYtDlpVersion("2026.08.19", now));
        Assert.Null(DoctorAdvice.ForYtDlpVersion("2026.08.01", now));
        var stale = DoctorAdvice.ForYtDlpVersion("2026.05.10", now);
        Assert.NotNull(stale);
        Assert.Contains("143 days old", stale);
        Assert.Contains("deps install yt-dlp --force", stale);
        Assert.Null(DoctorAdvice.ForYtDlpVersion("not-a-version", now));
        Assert.Null(DoctorAdvice.ForYtDlpVersion(null, now));
        Assert.Equal(new DateOnly(2024, 8, 6), DoctorAdvice.ParseYtDlpReleaseDate("2024.08.06.232355"));
    }

    [Fact]
    public void KnownStartupFailuresGetConcreteFixes()
    {
        var blocked = DoctorAdvice.ForRunnableError("yt-dlp", "[PYI-58304:ERROR] Failed to load Python DLL 'C:\\Users\\x\\AppData\\Local\\Temp\\_MEI1113842\\python310.dll'. | LoadLibrary: An Application Control policy has blocked this file.");
        Assert.NotNull(blocked);
        Assert.Contains("application-control policy", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pip install yt-dlp", blocked);

        var dll = DoctorAdvice.ForRunnableError("yt-dlp", "[PYI-1:ERROR] Failed to load Python DLL 'x.dll'.");
        Assert.NotNull(dll);
        Assert.Contains("pip install yt-dlp", dll);

        var slow = DoctorAdvice.ForRunnableError("yt-dlp", "did not answer `--version` within 60s (...)");
        Assert.NotNull(slow);
        Assert.Contains("timed out rather than failed", slow);

        Assert.Null(DoctorAdvice.ForRunnableError("ffmpeg", "some other error"));
        Assert.Null(DoctorAdvice.ForRunnableError("ffmpeg", null));
    }

    [Fact]
    public void RenderedReportShowsVersionsAdviceAndTheJsRuntimeRow()
    {
        var report = new DoctorReport(DateTimeOffset.UtcNow,
        [
            new DoctorDependencyReport("yt-dlp", true, true, @"C:\tools\yt-dlp.exe", "PATH", null, null, "2026.03.01", "yt-dlp 2026.03.01 is 213 days old. Update it."),
            new DoctorDependencyReport("ffmpeg", true, true, @"C:\tools\ffmpeg.exe", "PATH", null, null, "7.1.1", null),
            new DoctorDependencyReport("js-runtime", false, false, null, "PATH", "No JavaScript runtime (deno, node or bun) found on PATH.", null, null, DoctorAdvice.InstallJsRuntime),
            new DoctorDependencyReport("copilot", true, false, @"C:\tools\copilot.exe", "PATH", null, "Failed to load Python DLL", null, "Try pip."),
        ]);

        using var writer = new StringWriter();
        CliDiagnostics.RenderDoctorText(report, writer, color: false);
        var text = writer.ToString();

        Assert.Contains("ok       yt-dlp      2026.03.01", text);
        Assert.Contains("→ yt-dlp 2026.03.01 is 213 days old", text);
        Assert.Contains("ok       ffmpeg      7.1.1", text);
        Assert.Contains("missing  js-runtime", text);
        Assert.Contains("fix: Install deno", text);
        Assert.Contains("degraded copilot", text);
        Assert.Contains("fix: Try pip.", text);
    }

    [Fact]
    public async Task DoctorReportIncludesTheJsRuntimeRowAndVersions()
    {
        using var temp = new TestTempDirectory();
        var configStore = new ConfigStore(temp.GetPath("config.json"));
        var service = new DiagnosticsService(configStore, new ProcessRunner());

        var report = await service.BuildDoctorReportAsync(CancellationToken.None);

        var js = Assert.Single(report.Dependencies, d => d.Name == "js-runtime");
        if (js.IsFound)
        {
            Assert.Contains(DoctorAdvice.JsRuntimes, runtime => js.Message!.StartsWith(runtime, StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(DoctorAdvice.InstallJsRuntime, js.Advice);
        }

        var ffmpeg = report.Dependencies.Single(d => d.Name == "ffmpeg");
        if (ffmpeg.IsFound && ffmpeg.IsRunnable)
        {
            Assert.False(string.IsNullOrWhiteSpace(ffmpeg.Version), "a runnable ffmpeg must report its version");
        }
    }
}
