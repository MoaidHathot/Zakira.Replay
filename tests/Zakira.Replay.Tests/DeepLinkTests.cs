using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class DeepLinkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void ForReturnsNullWhenBaseUrlIsUnusable(string? url)
    {
        Assert.Null(DeepLink.For(url, 42));
    }

    [Theory]
    [InlineData(83.7, 83)]   // floor to integer seconds
    [InlineData(0, 0)]
    [InlineData(-15, 0)]      // negative clamped to 0
    [InlineData(double.NaN, 0)] // NaN clamped to 0
    public void ForCoercesNonNegativeWholeSeconds(double input, long expectedSeconds)
    {
        var url = DeepLink.For("https://example.com/v", input);

        Assert.NotNull(url);
        Assert.EndsWith($"#t={expectedSeconds}", url);
    }

    [Fact]
    public void YouTubeFullUrlGetsTQueryParameter()
    {
        var link = DeepLink.For("https://www.youtube.com/watch?v=dQw4w9WgXcQ", 90);

        Assert.NotNull(link);
        Assert.Contains("v=dQw4w9WgXcQ", link);
        Assert.Contains("t=90s", link);
    }

    [Fact]
    public void YouTubeShortUrlGetsTQueryParameter()
    {
        var link = DeepLink.For("https://youtu.be/dQw4w9WgXcQ", 12);

        Assert.NotNull(link);
        Assert.Contains("t=12s", link);
    }

    [Fact]
    public void YouTubeReplacesExistingTParameter()
    {
        // Re-anchoring must not produce two t= parameters.
        var link = DeepLink.For("https://www.youtube.com/watch?v=abc&t=10s", 200);

        Assert.NotNull(link);
        var matches = System.Text.RegularExpressions.Regex.Matches(link!, @"\bt=");
        Assert.Single(matches);
        Assert.Contains("t=200s", link);
    }

    [Fact]
    public void VimeoUsesFragmentTimeAnchor()
    {
        var link = DeepLink.For("https://vimeo.com/123456", 45);

        Assert.NotNull(link);
        Assert.EndsWith("#t=45s", link);
    }

    [Fact]
    public void SharePointStreamUsesNavQueryParameter()
    {
        // 3725s = 1h02m05s, expecting ?nav=t=01h02m05s on the SharePoint host.
        var link = DeepLink.For("https://contoso-my.sharepoint.com/personal/x/_layouts/15/stream.aspx?id=abc", 3725);

        Assert.NotNull(link);
        Assert.Contains("nav=t=01h02m05s", link);
        // Pre-existing query parameter preserved.
        Assert.Contains("id=abc", link);
    }

    [Fact]
    public void GenericHostUsesW3cMediaFragments()
    {
        // Any host we don't have a profile for falls back to the W3C Media Fragments syntax
        // (#t=<seconds>) — the only choice that won't collide with site-specific query semantics.
        var link = DeepLink.For("https://build.microsoft.com/en-US/sessions/KEY01?source=sessions", 600);

        Assert.NotNull(link);
        Assert.EndsWith("#t=600", link);
        Assert.Contains("?source=sessions", link);
    }

    [Fact]
    public void GenericHostPreservesExistingFragmentByReplacingIt()
    {
        var link = DeepLink.For("https://example.com/v#chapter=intro", 30);

        Assert.NotNull(link);
        Assert.EndsWith("#t=30", link);
        // The old fragment is replaced (we own #t=).
        Assert.DoesNotContain("chapter=intro", link);
    }

    [Theory]
    [InlineData("00:01:23", 83.0)]
    [InlineData("00:01:23.500", 83.5)]
    [InlineData("01:00:00", 3600.0)]
    [InlineData("83.5", 83.5)]
    [InlineData("0", 0.0)]
    [InlineData("12:34", 754.0)]      // m:s
    public void TryParseSecondsHandlesCommonFormats(string input, double expected)
    {
        Assert.Equal(expected, DeepLink.TryParseSeconds(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("garbage:input:here")]
    public void TryParseSecondsReturnsNullForUnparseable(string? input)
    {
        Assert.Null(DeepLink.TryParseSeconds(input));
    }
}
