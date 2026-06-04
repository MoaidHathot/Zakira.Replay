using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class AutoplayPoliciesTests
{
    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("   ", "default")]
    [InlineData("default", "default")]
    [InlineData("Default", "default")]
    [InlineData("browser-default", "default")]
    [InlineData("no-user-gesture-required", "no-user-gesture-required")]
    [InlineData("no_user_gesture_required", "no-user-gesture-required")]   // underscores → hyphens
    [InlineData("no-gesture", "no-user-gesture-required")]                 // accepted alias
    [InlineData("allow", "no-user-gesture-required")]                      // accepted alias
    [InlineData("NO-USER-GESTURE-REQUIRED", "no-user-gesture-required")]
    [InlineData("garbage", "default")]                                      // unknown → safe
    public void NormalizeMapsAliasesAndUnknowns(string? input, string expected)
    {
        Assert.Equal(expected, AutoplayPolicies.Normalize(input));
    }

    [Theory]
    [InlineData("default", null)]                                            // default = no flag
    [InlineData("no-user-gesture-required", "--autoplay-policy=no-user-gesture-required")]
    [InlineData("garbage", null)]                                            // unknown = no flag
    public void ToChromiumArgEmitsExpectedFlag(string policy, string? expected)
    {
        Assert.Equal(expected, AutoplayPolicies.ToChromiumArg(policy));
    }

    [Fact]
    public void ResolveCliOverrideBeatsHostMapAndGlobalDefault()
    {
        var hostMap = new Dictionary<string, string>
        {
            { "*.event.microsoft.com", "no-user-gesture-required" },
        };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: "default",
            byHost: hostMap,
            globalDefault: "no-user-gesture-required",
            sourceUrl: "https://mediusprod.event.microsoft.com/Embed/x");

        // CLI explicitly says default; host map AND global want no-gesture; CLI wins.
        Assert.Equal("default", resolved);
    }

    [Fact]
    public void ResolveHostMapBeatsGlobalDefaultWhenCliUnset()
    {
        var hostMap = new Dictionary<string, string>
        {
            { "*.event.microsoft.com", "no-user-gesture-required" },
        };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: null,
            byHost: hostMap,
            globalDefault: "default",
            sourceUrl: "https://mediusprod.event.microsoft.com/Embed/x");

        Assert.Equal("no-user-gesture-required", resolved);
    }

    [Fact]
    public void ResolveExactHostMatchBeatsWildcardWhenMoreSpecific()
    {
        // Exact host match has priority over wildcard.
        var hostMap = new Dictionary<string, string>
        {
            { "mediusprod.event.microsoft.com", "default" },
            { "*.event.microsoft.com", "no-user-gesture-required" },
        };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: null,
            byHost: hostMap,
            globalDefault: "default",
            sourceUrl: "https://mediusprod.event.microsoft.com/Embed/x");

        // Exact match wins → "default", not the wildcard's "no-user-gesture-required".
        Assert.Equal("default", resolved);
    }

    [Fact]
    public void ResolveLongerWildcardSuffixWinsAmongWildcards()
    {
        var hostMap = new Dictionary<string, string>
        {
            { "*.microsoft.com", "default" },
            { "*.event.microsoft.com", "no-user-gesture-required" },
        };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: null,
            byHost: hostMap,
            globalDefault: "default",
            sourceUrl: "https://mediusprod.event.microsoft.com/Embed/x");

        // Longer match wins (".event.microsoft.com" is 20 chars vs ".microsoft.com" 14).
        Assert.Equal("no-user-gesture-required", resolved);
    }

    [Fact]
    public void ResolveFallsBackToGlobalDefaultWhenNoHostMatches()
    {
        var hostMap = new Dictionary<string, string>
        {
            { "*.event.microsoft.com", "no-user-gesture-required" },
        };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: null,
            byHost: hostMap,
            globalDefault: "no-user-gesture-required",
            sourceUrl: "https://youtube.com/watch?v=abc");

        Assert.Equal("no-user-gesture-required", resolved);
    }

    [Fact]
    public void ResolveReturnsGlobalDefaultWhenHostMapEmptyAndCliUnset()
    {
        Assert.Equal("default",
            AutoplayPolicies.Resolve(null, byHost: null, globalDefault: "default", sourceUrl: "https://x.test/"));
        Assert.Equal("no-user-gesture-required",
            AutoplayPolicies.Resolve(null, byHost: new Dictionary<string, string>(), globalDefault: "no-user-gesture-required", sourceUrl: "https://x.test/"));
    }

    [Fact]
    public void ResolveTolerantesUnparseableSourceUrl()
    {
        // Unparseable URL → host map can't be matched; falls through to global default.
        var hostMap = new Dictionary<string, string> { { "x", "no-user-gesture-required" } };

        var resolved = AutoplayPolicies.Resolve(
            cliOverride: null,
            byHost: hostMap,
            globalDefault: "default",
            sourceUrl: "not-a-url");

        Assert.Equal("default", resolved);
    }
}
