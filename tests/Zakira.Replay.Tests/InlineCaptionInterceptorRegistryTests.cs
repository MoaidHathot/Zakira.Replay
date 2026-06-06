using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class InlineCaptionInterceptorRegistryTests
{
    private static BrowserCaptureRequest Request(bool captureCaptions)
        => new(
            Url: "https://example.test",
            Run: new VideoRun("test", Path.GetTempPath()),
            FrameCount: 0,
            PlayButtonSelector: null,
            VideoElementSelector: "video",
            SeekWaitSeconds: 0,
            DurationProbeTimeoutSeconds: 1,
            JpegQuality: 90,
            CaptureCaptions: captureCaptions,
            MaxCaptionBytes: 5_000_000);

    [Fact]
    public void CreateForReturnsEmptyWhenCaptionsDisabled()
    {
        var warnings = new List<ReplayWarning>();

        var interceptors = InlineCaptionInterceptorRegistry.CreateFor(Request(captureCaptions: false), warnings);

        Assert.Empty(interceptors);
    }

    [Fact]
    public void CreateForIncludesMediusByDefault()
    {
        var warnings = new List<ReplayWarning>();

        var interceptors = InlineCaptionInterceptorRegistry.CreateFor(Request(captureCaptions: true), warnings);

        // The Medius profile must be present and identifiable by its stable Name.
        Assert.Contains(interceptors, i => i.Name == "medius");
    }

    [Fact]
    public void CreateForIncludesMediastreamByDefault()
    {
        // Newer profile for the mediastream.microsoft.com Shaka-player wrapper used by
        // Microsoft Build "InstaVOD" sessions (BRK247-style). Must ship alongside Medius so
        // the registry handles both player flavours out of the box.
        var warnings = new List<ReplayWarning>();

        var interceptors = InlineCaptionInterceptorRegistry.CreateFor(Request(captureCaptions: true), warnings);

        Assert.Contains(interceptors, i => i.Name == "mediastream");
    }

    [Fact]
    public void EveryRegisteredInterceptorExposesNonEmptyName()
    {
        // Names back log lines and warning sources; an empty/whitespace name would break tooling
        // that pivots on the field. Enforce the contract at the registry boundary so a future
        // profile with a forgotten Name fails this test instead of failing silently in prod.
        var warnings = new List<ReplayWarning>();

        var interceptors = InlineCaptionInterceptorRegistry.CreateFor(Request(captureCaptions: true), warnings);

        Assert.NotEmpty(interceptors);
        Assert.All(interceptors, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public void RegisteredInterceptorsAreUnique()
    {
        // Two profiles with the same Name would conflict in fallback ordering and tracing. The
        // registry should never ship duplicates; enforce it.
        var warnings = new List<ReplayWarning>();

        var interceptors = InlineCaptionInterceptorRegistry.CreateFor(Request(captureCaptions: true), warnings);

        var names = interceptors.Select(i => i.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
