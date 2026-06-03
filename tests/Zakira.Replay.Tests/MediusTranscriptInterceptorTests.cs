using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class MediusTranscriptInterceptorTests
{
    [Theory]
    // Canonical Medius/Studios embed pages.
    [InlineData("https://medius.microsoft.com/Embed/video-nc/abc123", true)]
    [InlineData("https://medius.studios.ms/Embed/video-nc/KEY01", true)]
    [InlineData("https://medius.studios.ms/embed/video-nc/KEY01", true)]
    // Microsoft Events host variant (medius*.event.microsoft.com) on an Embed path.
    [InlineData("https://mediusprod.event.microsoft.com/Embed/video-7534294", true)]
    // The DL CDN that serves the actual .vtt blobs is NOT an embed page.
    [InlineData("https://mediusdl.event.microsoft.com/video-7534294/Caption_en-US.vtt?sv=x&sig=y", false)]
    // Right host, but not an Embed/player path.
    [InlineData("https://medius.microsoft.com/static/app.js", false)]
    // Unrelated hosts.
    [InlineData("https://build.microsoft.com/en-US/sessions/KEY01", false)]
    [InlineData("https://example.com/Embed/video", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void IsMediusEmbedUrlMatchesEmbedPages(string url, bool expected)
    {
        Assert.Equal(expected, MediusTranscriptInterceptor.IsMediusEmbedUrl(url));
    }

    [Theory]
    [InlineData("https://mediusdl.event.microsoft.com/video-7534294/Caption_en-US.vtt?sv=2021&sig=abc", "en-US")]
    [InlineData("https://cdn/video/Caption_zh-Hans.vtt?token=x", "zh-Hans")]
    [InlineData("https://cdn/video/Caption_fr.vtt", "fr")]
    [InlineData("https://cdn/video/Caption_pt-BR.srt?sas=y", "pt-BR")]
    [InlineData("https://cdn/video/transcript.vtt", null)]
    [InlineData("https://cdn/video/Caption_.vtt", null)]
    public void InferLanguageFromSrcReadsFileNameTag(string src, string? expected)
    {
        Assert.Equal(expected, MediusTranscriptInterceptor.InferLanguageFromSrc(src));
    }

    [Fact]
    public void TryExtractCaptionConfigParsesInlineLanguageList()
    {
        // Mirrors the real embed-page inline block: a JS assignment with a trailing semicolon,
        // SAS URLs containing '&' and '=', plus a non-languageList property after it.
        var html = """
        <html><head><script>
          window.foo = 1;
          const captionsConfiguration = {
            "languageList": [
              { "src": "https://mediusdl.event.microsoft.com/video-7534294/Caption_en-US.vtt?sv=2021-08-06&sr=c&sig=AAA%2FBBB&se=2031-01-01&sp=r", "srclang": "en", "kind": "subtitles", "label": "english" },
              { "src": "https://mediusdl.event.microsoft.com/video-7534294/Caption_es-ES.vtt?sv=2021-08-06&sr=c&sig=CCC&se=2031-01-01&sp=r", "srclang": "es", "kind": "subtitles", "label": "spanish" }
            ],
            "defaultLanguage": "Off"
          };
          const other = { "x": 2 };
        </script></head></html>
        """;

        var captions = MediusTranscriptInterceptor.TryExtractCaptionConfig(html);

        Assert.Equal(2, captions.Count);
        Assert.Equal("en-US", captions[0].Language);
        Assert.Equal("en", captions[0].SrcLang);
        Assert.Equal("english", captions[0].Label);
        Assert.Contains("Caption_en-US.vtt", captions[0].Src);
        Assert.Equal("es-ES", captions[1].Language);
    }

    [Fact]
    public void TryExtractCaptionConfigPrefersFileNameTagOverSrclang()
    {
        // Medius sometimes sets srclang to non-standard codes (e.g. "bd" for Bangla); the file
        // name tag must win.
        var html = """
        const captionsConfiguration = {
          "languageList": [
            { "src": "https://cdn/Caption_bn-IN.vtt?sig=x", "srclang": "bd", "label": "bangla" }
          ]
        };
        """;

        var captions = MediusTranscriptInterceptor.TryExtractCaptionConfig(html);

        var caption = Assert.Single(captions);
        Assert.Equal("bn-IN", caption.Language);
        Assert.Equal("bd", caption.SrcLang);
    }

    [Fact]
    public void TryExtractCaptionConfigSkipsEntriesWithoutSrc()
    {
        var html = """
        const captionsConfiguration = {
          "languageList": [
            { "srclang": "en", "label": "english" },
            { "src": "https://cdn/Caption_de-DE.vtt?sig=x", "srclang": "de" }
          ]
        };
        """;

        var captions = MediusTranscriptInterceptor.TryExtractCaptionConfig(html);

        var caption = Assert.Single(captions);
        Assert.Equal("de-DE", caption.Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>no caption config here</html>")]
    [InlineData("const captionsConfiguration = { malformed ")]
    [InlineData("""const captionsConfiguration = { "noLanguageList": [] };""")]
    public void TryExtractCaptionConfigReturnsEmptyForMissingOrMalformed(string html)
    {
        Assert.Empty(MediusTranscriptInterceptor.TryExtractCaptionConfig(html));
    }

    private static IReadOnlyList<MediusCaption> Sample() =>
    [
        new MediusCaption("https://cdn/Caption_en-US.vtt", "en", "english", "en-US"),
        new MediusCaption("https://cdn/Caption_es-ES.vtt", "es", "spanish", "es-ES"),
        new MediusCaption("https://cdn/Caption_fr-FR.vtt", "fr", "french", "fr-FR"),
    ];

    [Fact]
    public void SelectForDownloadMatchesPreferenceByPrimarySubtag()
    {
        var picks = MediusTranscriptInterceptor.SelectForDownload(Sample(), ["fr"]);

        var pick = Assert.Single(picks);
        Assert.Equal("fr-FR", pick.Language);
    }

    [Fact]
    public void SelectForDownloadHonoursPreferenceOrderAndDedupes()
    {
        var picks = MediusTranscriptInterceptor.SelectForDownload(Sample(), ["es", "fr", "es"]);

        Assert.Equal(2, picks.Count);
        Assert.Equal("es-ES", picks[0].Language);
        Assert.Equal("fr-FR", picks[1].Language);
    }

    [Fact]
    public void SelectForDownloadFallsBackToEnglishWhenNoPreferenceMatches()
    {
        var picks = MediusTranscriptInterceptor.SelectForDownload(Sample(), ["ja"]);

        var pick = Assert.Single(picks);
        Assert.Equal("en-US", pick.Language);
    }

    [Fact]
    public void SelectForDownloadFallsBackToFirstWhenNoEnglishAvailable()
    {
        IReadOnlyList<MediusCaption> available =
        [
            new MediusCaption("https://cdn/Caption_es-ES.vtt", "es", "spanish", "es-ES"),
            new MediusCaption("https://cdn/Caption_fr-FR.vtt", "fr", "french", "fr-FR"),
        ];

        var picks = MediusTranscriptInterceptor.SelectForDownload(available, []);

        var pick = Assert.Single(picks);
        Assert.Equal("es-ES", pick.Language);
    }

    [Fact]
    public void SelectForDownloadReturnsEmptyWhenNothingAvailable()
    {
        Assert.Empty(MediusTranscriptInterceptor.SelectForDownload([], ["en"]));
    }
}
