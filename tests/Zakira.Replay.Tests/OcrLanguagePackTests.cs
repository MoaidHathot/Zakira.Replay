using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class OcrLanguagePackTests
{
    [Theory]
    [InlineData("latin")]
    [InlineData("chinese")]
    [InlineData("english")]
    [InlineData("korean")]
    [InlineData("cyrillic")]
    [InlineData("arabic")]
    [InlineData("devanagari")]
    [InlineData("greek")]
    [InlineData("telugu")]
    [InlineData("tamil")]
    public void AllCatalogPacksHaveDistinctRecognitionFiles(string name)
    {
        var pack = OcrLanguagePacks.Get(name);
        Assert.False(string.IsNullOrWhiteSpace(pack.RecognitionModelFile));
        Assert.False(string.IsNullOrWhiteSpace(pack.DictionaryFile));
        Assert.False(string.IsNullOrWhiteSpace(pack.RecognitionModelDirectory));
        Assert.False(string.IsNullOrWhiteSpace(pack.DisplayName));
    }

    [Fact]
    public void CatalogRecognitionFilesAreUnique()
    {
        var files = OcrLanguagePacks.All.Select(p => p.RecognitionModelFile).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("latin", OcrLanguagePacks.Latin)]
    [InlineData("LATIN", OcrLanguagePacks.Latin)]
    [InlineData(" latin ", OcrLanguagePacks.Latin)]
    [InlineData(null, OcrLanguagePacks.Latin)]
    [InlineData("", OcrLanguagePacks.Latin)]
    [InlineData("zh", OcrLanguagePacks.Chinese)]
    [InlineData("cn", OcrLanguagePacks.Chinese)]
    [InlineData("simplified-chinese", OcrLanguagePacks.Chinese)]
    [InlineData("ko", OcrLanguagePacks.Korean)]
    [InlineData("kr", OcrLanguagePacks.Korean)]
    [InlineData("hangul", OcrLanguagePacks.Korean)]
    [InlineData("ru", OcrLanguagePacks.Cyrillic)]
    [InlineData("russian", OcrLanguagePacks.Cyrillic)]
    [InlineData("sr", OcrLanguagePacks.Cyrillic)]
    [InlineData("ar", OcrLanguagePacks.Arabic)]
    [InlineData("persian", OcrLanguagePacks.Arabic)]
    [InlineData("urdu", OcrLanguagePacks.Arabic)]
    [InlineData("hi", OcrLanguagePacks.Devanagari)]
    [InlineData("hindi", OcrLanguagePacks.Devanagari)]
    [InlineData("sanskrit", OcrLanguagePacks.Devanagari)]
    [InlineData("el", OcrLanguagePacks.Greek)]
    [InlineData("gr", OcrLanguagePacks.Greek)]
    [InlineData("te", OcrLanguagePacks.Telugu)]
    [InlineData("ta", OcrLanguagePacks.Tamil)]
    public void NormalizeMapsAliasesAndPrimaryNamesToCanonical(string? input, string expected)
    {
        Assert.Equal(expected, OcrLanguagePacks.Normalize(input));
    }

    [Fact]
    public void NormalizeReturnsUnknownPackVerbatimSoCallerCanWarn()
    {
        // Future packs / typos should be returned verbatim (lowercased) so the caller can emit
        // a structured error pointing at the catalog.
        Assert.Equal("klingon", OcrLanguagePacks.Normalize("klingon"));
        Assert.Equal("nahuatl", OcrLanguagePacks.Normalize("NAHUATL"));
    }

    [Fact]
    public void TryGetReturnsTrueForKnownPacksAndFalseForUnknown()
    {
        Assert.True(OcrLanguagePacks.TryGet("chinese", out var pack));
        Assert.Equal(OcrLanguagePacks.Chinese, pack.Name);

        Assert.True(OcrLanguagePacks.TryGet("hindi", out pack));
        Assert.Equal(OcrLanguagePacks.Devanagari, pack.Name);

        Assert.False(OcrLanguagePacks.TryGet("klingon", out _));
    }

    [Fact]
    public void GetThrowsReplayExceptionForUnknownPack()
    {
        var ex = Assert.Throws<ReplayException>(() => OcrLanguagePacks.Get("klingon"));
        Assert.Contains("klingon", ex.Message);
        Assert.Contains("Known packs:", ex.Message);
        Assert.Contains("latin", ex.Message);
        Assert.Contains("chinese", ex.Message);
    }

    [Fact]
    public void DefaultConfigUsesLatinPack()
    {
        var config = ConfigStore.CreateDefaultConfig();
        Assert.Equal(OcrLanguagePacks.Latin, config.Ocr.Local.LanguagePack);
    }

    [Fact]
    public async Task ConfigStoreRoundTripsOcrLanguagePackKey()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        await store.SetAsync("ocr.local.languagePack", "chinese", CancellationToken.None);
        var read = await store.GetAsync("ocr.local.languagePack", CancellationToken.None);

        Assert.Equal(OcrLanguagePacks.Chinese, read);
    }

    [Fact]
    public async Task ConfigStoreAcceptsAliasesAndPersistsCanonical()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        // Setting `hi` (alias) must persist as `devanagari` so subsequent reads / installs see
        // the canonical pack name and don't have to redo the alias resolution.
        await store.SetAsync("ocr.local.language", "hi", CancellationToken.None);
        var read = await store.GetAsync("ocr.local.languagePack", CancellationToken.None);

        Assert.Equal(OcrLanguagePacks.Devanagari, read);
    }

    [Fact]
    public async Task ConfigStoreRejectsUnknownPackWithGuidance()
    {
        using var temp = new TestTempDirectory();
        var configPath = temp.GetPath("Zakira.Replay.json");
        var store = new ConfigStore(configPath);

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => store.SetAsync("ocr.local.languagePack", "klingon", CancellationToken.None));
        Assert.Contains("klingon", ex.Message);
        Assert.Contains("latin", ex.Message);
    }

    [Fact]
    public void ResolveDerivesPathsFromConfiguredPack()
    {
        const string envVar = "ZAKIRA_REPLAY_OCR_LANGUAGE_PACK";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            var config = new ReplayConfig
            {
                Ocr = new OcrConfig
                {
                    Local = new LocalOcrConfig
                    {
                        LanguagePack = OcrLanguagePacks.Chinese,
                        ModelDirectory = "/test/models"
                    }
                }
            };

            var paths = LocalOcrModelPaths.Resolve(config);

            Assert.Equal(OcrLanguagePacks.Chinese, paths.LanguagePack);
            Assert.EndsWith("ch_PP-OCRv5_rec_mobile.onnx", paths.RecognitionPath, StringComparison.Ordinal);
            Assert.EndsWith("ppocrv5_dict.txt", paths.DictionaryPath, StringComparison.Ordinal);
            // Detection and classification stay constant across packs (shared download).
            Assert.EndsWith("ch_PP-OCRv5_det_mobile.onnx", paths.DetectionPath, StringComparison.Ordinal);
            Assert.EndsWith("ch_ppocr_mobile_v2.0_cls_mobile.onnx", paths.ClassificationPath, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void ResolveHonoursEnvironmentVariableOverConfig()
    {
        const string envVar = "ZAKIRA_REPLAY_OCR_LANGUAGE_PACK";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "korean");
            var config = new ReplayConfig
            {
                Ocr = new OcrConfig
                {
                    Local = new LocalOcrConfig { LanguagePack = OcrLanguagePacks.Latin }
                }
            };

            var paths = LocalOcrModelPaths.Resolve(config);
            Assert.Equal(OcrLanguagePacks.Korean, paths.LanguagePack);
            Assert.EndsWith("korean_PP-OCRv5_rec_mobile.onnx", paths.RecognitionPath, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void ResolveHonoursExplicitFilePathOverPackDefault()
    {
        const string envVar = "ZAKIRA_REPLAY_OCR_LANGUAGE_PACK";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            // Even when a pack is configured, an explicit recognitionModelPath override wins.
            // Users with custom-trained models should be able to point at them directly.
            var config = new ReplayConfig
            {
                Ocr = new OcrConfig
                {
                    Local = new LocalOcrConfig
                    {
                        LanguagePack = OcrLanguagePacks.Chinese,
                        RecognitionModelPath = "/custom/my-rec.onnx",
                        DictionaryPath = "/custom/my-dict.txt"
                    }
                }
            };

            var paths = LocalOcrModelPaths.Resolve(config);
            Assert.Equal("/custom/my-rec.onnx", paths.RecognitionPath);
            Assert.Equal("/custom/my-dict.txt", paths.DictionaryPath);
            // Pack name is still recorded — orchestrators may want to know what dictionary
            // semantics to expect even when paths are overridden.
            Assert.Equal(OcrLanguagePacks.Chinese, paths.LanguagePack);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void CatalogContainsAllAdvertisedPacks()
    {
        // Sanity check matching the README / SKILL docs and the deps install help text. If a
        // pack is added or removed from the catalog, this test catches drift in the docs.
        var expected = new[]
        {
            OcrLanguagePacks.Latin,
            OcrLanguagePacks.Chinese,
            OcrLanguagePacks.English,
            OcrLanguagePacks.Korean,
            OcrLanguagePacks.Cyrillic,
            OcrLanguagePacks.Arabic,
            OcrLanguagePacks.Devanagari,
            OcrLanguagePacks.Greek,
            OcrLanguagePacks.Telugu,
            OcrLanguagePacks.Tamil
        };

        Assert.Equal(expected.Length, OcrLanguagePacks.All.Count);
        foreach (var name in expected)
        {
            Assert.Contains(OcrLanguagePacks.All, p => p.Name == name);
        }
    }
}
