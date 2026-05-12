using System.Reflection;
using System.Text;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class BrowserCaptionInterceptorTests
{
    [Theory]
    [InlineData("https://example.com/captions/foo.vtt", true)]
    [InlineData("https://example.com/captions/foo.srt", true)]
    [InlineData("https://example.com/captions/foo.vtt?token=abc&lang=en", true)]
    [InlineData("https://example.com/captions/foo.SRT", true)]
    [InlineData("https://example.com/captions/foo.json", false)]
    [InlineData("https://example.com/captions/foo.vttx", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void IsCaptionUrlMatchesOnlyVttAndSrtPaths(string url, bool expected)
    {
        Assert.Equal(expected, InvokeIsCaptionUrl(url));
    }

    [Theory]
    [InlineData("https://mediusdl.event.microsoft.com/video-1234/Caption_en-US.vtt?token=abc", "en-US", "url-Caption_<lang>")]
    [InlineData("https://mediusdl.event.microsoft.com/video-1234/Caption_fr.vtt", "fr", "url-Caption_<lang>")]
    [InlineData("https://mediusdl.event.microsoft.com/video-1234/Caption_zh-Hans.vtt", "zh-Hans", "url-Caption_<lang>")]
    [InlineData("https://example.com/subs/subtitles_es-ES.vtt", "es-ES", "url-filename")]
    [InlineData("https://example.com/cdn/captions.fr-CA.vtt", "fr-CA", "url-filename")]
    [InlineData("https://example.com/cdn/it.vtt", "it", "url-filename")]
    [InlineData("https://example.com/captions/en/foo.vtt", "en", "url-path-segment")]
    [InlineData("https://example.com/lang/zh-Hans/foo.vtt", "zh-Hans", "url-path-segment")]
    [InlineData("https://example.com/sub/de/foo.vtt", "de", "url-path-segment")]
    [InlineData("https://example.com/captions.vtt?lang=ja", "ja", "url-query-lang")]
    [InlineData("https://example.com/captions.vtt?hl=ko", "ko", "url-query-hl")]
    [InlineData("https://example.com/captions.vtt?language=pt-BR", "pt-BR", "url-query-language")]
    [InlineData("https://example.com/captions.vtt?l=ru", "ru", "url-query-l")]
    [InlineData("https://example.com/captions.vtt?tlang=es", "es", "url-query-tlang")]
    public void InferLanguageFromUrlExtractsBcp47Codes(string url, string expectedLanguage, string expectedSource)
    {
        var (language, source) = InvokeInferLanguageFromUrl(url);
        Assert.Equal(expectedLanguage, language);
        Assert.Equal(expectedSource, source);
    }

    [Theory]
    [InlineData("https://example.com/cdn/no-language-here.vtt")]
    [InlineData("https://example.com/cdn/foo.bar.baz.vtt")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/captions.vtt?token=verylonghexstringnotalanguage")]
    public void InferLanguageFromUrlReturnsNullWhenNoSignal(string url)
    {
        var (language, source) = InvokeInferLanguageFromUrl(url);
        Assert.Null(language);
        Assert.Null(source);
    }

    [Fact]
    public void PickBestPrefersExactLanguageMatch()
    {
        var captions = new[]
        {
            MakeCaption(1, "fr"),
            MakeCaption(2, "en-US"),
            MakeCaption(3, "es")
        };

        var pick = InvokePickBest(captions, ["en", "fr"]);

        Assert.NotNull(pick);
        Assert.Equal(2, pick!.Ordinal);
    }

    [Fact]
    public void PickBestHonoursPreferenceOrder()
    {
        var captions = new[]
        {
            MakeCaption(1, "es"),
            MakeCaption(2, "en"),
            MakeCaption(3, "fr")
        };

        var pick = InvokePickBest(captions, ["fr", "en"]);

        Assert.NotNull(pick);
        Assert.Equal(3, pick!.Ordinal);
    }

    [Fact]
    public void PickBestFallsBackToFirstWithKnownLanguage()
    {
        var captions = new[]
        {
            MakeCaption(1, null),
            MakeCaption(2, "ja"),
            MakeCaption(3, null)
        };

        // No preference matches, but we still prefer a caption that has SOME language hint.
        var pick = InvokePickBest(captions, ["en", "fr"]);

        Assert.NotNull(pick);
        Assert.Equal(2, pick!.Ordinal);
    }

    [Fact]
    public void PickBestFallsBackToFirstCaptionWhenNothingMatches()
    {
        var captions = new[]
        {
            MakeCaption(1, null),
            MakeCaption(2, null)
        };

        var pick = InvokePickBest(captions, ["en"]);

        Assert.NotNull(pick);
        Assert.Equal(1, pick!.Ordinal);
    }

    [Fact]
    public void PickBestReturnsNullForEmptySet()
    {
        Assert.Null(InvokePickBest([], ["en"]));
    }

    [Fact]
    public void PickBestSingleCaptionShortCircuit()
    {
        var captions = new[] { MakeCaption(1, "en") };
        var pick = InvokePickBest(captions, ["fr"]);
        Assert.NotNull(pick);
        Assert.Equal(1, pick!.Ordinal);
    }

    [Theory]
    [InlineData("en", "en-US", true)]
    [InlineData("en-US", "en", true)]
    [InlineData("en-US", "en-GB", true)] // primary subtag matches
    [InlineData("zh-Hans", "zh-Hant", true)]
    [InlineData("fr", "es", false)]
    [InlineData(null, "en", false)]
    [InlineData("en", "auto", true)]
    [InlineData(null, "auto", false)]
    [InlineData("en", "", false)]
    public void LanguageMatchesUsesPrimarySubtagAndAuto(string? inferred, string preference, bool expected)
    {
        Assert.Equal(expected, InvokeLanguageMatches(inferred, preference));
    }

    private static BrowserCapturedCaption MakeCaption(int ordinal, string? language)
    {
        return new BrowserCapturedCaption(
            Ordinal: ordinal,
            Url: $"https://example.com/captions/{ordinal}.vtt",
            RelativePath: $"captions/browser-{ordinal:0000}.vtt",
            InferredLanguage: language,
            LanguageSource: language is null ? null : "test",
            ByteCount: 100,
            ContentSha256: $"deadbeef{ordinal:0000}",
            ContentType: "text/vtt");
    }

    // BrowserCaptionInterceptor is internal; reach into it via reflection so tests stay decoupled
    // from public surface changes.
    private static readonly Assembly CoreAssembly = typeof(AnalysisPipeline).Assembly;
    private static readonly Type InterceptorType =
        CoreAssembly.GetType("Zakira.Replay.Core.BrowserCaptionInterceptor", throwOnError: true)!;

    private static bool InvokeIsCaptionUrl(string url)
    {
        var method = InterceptorType.GetMethod("IsCaptionUrl", BindingFlags.Static | BindingFlags.Public)!;
        return (bool)method.Invoke(null, [url])!;
    }

    private static (string? Language, string? Source) InvokeInferLanguageFromUrl(string url)
    {
        var method = InterceptorType.GetMethod("InferLanguageFromUrl", BindingFlags.Static | BindingFlags.Public)!;
        var result = method.Invoke(null, [url])!;
        var language = (string?)result.GetType().GetField("Item1")!.GetValue(result);
        var source = (string?)result.GetType().GetField("Item2")!.GetValue(result);
        return (language, source);
    }

    private static BrowserCapturedCaption? InvokePickBest(BrowserCapturedCaption[] captions, string[] preferences)
    {
        var method = InterceptorType.GetMethod("PickBest", BindingFlags.Static | BindingFlags.Public)!;
        var captionsArg = (IReadOnlyList<BrowserCapturedCaption>)captions;
        var preferencesArg = (IReadOnlyList<string>)preferences;
        return (BrowserCapturedCaption?)method.Invoke(null, [captionsArg, preferencesArg]);
    }

    private static bool InvokeLanguageMatches(string? inferred, string preference)
    {
        var method = InterceptorType.GetMethod("LanguageMatches", BindingFlags.Static | BindingFlags.Public)!;
        return (bool)method.Invoke(null, [inferred, preference])!;
    }
}
