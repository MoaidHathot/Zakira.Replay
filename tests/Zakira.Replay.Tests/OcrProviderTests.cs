using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class OcrProviderTests
{
    [Theory]
    [InlineData("copilot", OcrProviders.Copilot)]
    [InlineData("llm", OcrProviders.Copilot)]
    [InlineData("github-copilot", OcrProviders.Copilot)]
    [InlineData("openai", OcrProviders.Copilot)]
    [InlineData("azure-openai", OcrProviders.Copilot)]
    [InlineData("local", OcrProviders.Local)]
    [InlineData("rapidocr", OcrProviders.Local)]
    [InlineData("rapid-ocr", OcrProviders.Local)]
    [InlineData("rapid_ocr", OcrProviders.Local)]
    [InlineData("onnx", OcrProviders.Local)]
    [InlineData("paddleocr", OcrProviders.Local)]
    [InlineData("offline", OcrProviders.Local)]
    [InlineData(" LOCAL ", OcrProviders.Local)]
    [InlineData("", OcrProviders.Local)]
    [InlineData(null, OcrProviders.Local)]
    public void OcrProviderFactoryNormalizesKnownAliases(string? input, string expected)
    {
        Assert.Equal(expected, OcrProviderFactory.Normalize(input));
    }

    [Fact]
    public void OcrProviderFactoryReturnsUnknownVerbatimSoCallerCanWarn()
    {
        // Unknown values are returned as-is (lowercased) so the pipeline emits OCR_UNKNOWN_PROVIDER.
        Assert.Equal("tesseract", OcrProviderFactory.Normalize("tesseract"));
        Assert.Equal("magic-ocr", OcrProviderFactory.Normalize("magic-ocr"));
    }

    [Fact]
    public void GetConfiguredProviderPrefersEnvironmentVariableOverConfig()
    {
        const string envVar = "ZAKIRA_REPLAY_OCR_PROVIDER";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "local");
            var config = new ReplayConfig
            {
                Ocr = new OcrConfig { Provider = OcrProviders.Copilot }
            };

            Assert.Equal(OcrProviders.Local, OcrProviderFactory.GetConfiguredProvider(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void GetConfiguredProviderFallsBackToConfigWhenNoEnvironmentVariable()
    {
        const string envVar = "ZAKIRA_REPLAY_OCR_PROVIDER";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            var config = new ReplayConfig
            {
                Ocr = new OcrConfig { Provider = OcrProviders.Local }
            };

            Assert.Equal(OcrProviders.Local, OcrProviderFactory.GetConfiguredProvider(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void LocalOcrModelPathsReportsMissingFiles()
    {
        using var temp = new TestTempDirectory();
        var paths = new LocalOcrModelPaths(
            DetectionPath: temp.GetPath("det.onnx"),
            ClassificationPath: temp.GetPath("cls.onnx"),
            RecognitionPath: temp.GetPath("rec.onnx"),
            DictionaryPath: temp.GetPath("keys.txt"));

        var missing = paths.MissingFiles();

        Assert.Equal(4, missing.Count);
        Assert.Contains(paths.DetectionPath, missing);
        Assert.Contains(paths.DictionaryPath, missing);
    }

    [Fact]
    public async Task LocalOcrModelPathsReportsZeroMissingFilesWhenAllPresent()
    {
        using var temp = new TestTempDirectory();
        var det = temp.GetPath("det.onnx");
        var cls = temp.GetPath("cls.onnx");
        var rec = temp.GetPath("rec.onnx");
        var dict = temp.GetPath("keys.txt");
        foreach (var path in new[] { det, cls, rec, dict })
        {
            await File.WriteAllBytesAsync(path, [0], CancellationToken.None);
        }

        var paths = new LocalOcrModelPaths(det, cls, rec, dict);

        Assert.Empty(paths.MissingFiles());
    }

    [Fact]
    public void SerializeResultAsOcrJsonEmitsContractShape()
    {
        // Build a synthetic OcrResult with two text blocks in non-reading order; ensure the
        // provider sorts them top-to-bottom, left-to-right and emits the documented JSON shape.
        var topBlock = new TextBlock
        {
            BoxPoints = [new SKPointI(10, 10), new SKPointI(100, 10), new SKPointI(100, 30), new SKPointI(10, 30)],
            Chars = ["Hello"],
            CharScores = [1f]
        };
        var bottomBlock = new TextBlock
        {
            BoxPoints = [new SKPointI(10, 200), new SKPointI(100, 200), new SKPointI(100, 220), new SKPointI(10, 220)],
            Chars = ["World"],
            CharScores = [1f]
        };
        var result = new OcrResult
        {
            TextBlocks = [bottomBlock, topBlock],
            StrRes = string.Empty
        };

        var json = LocalOnnxOcrProvider.SerializeResultAsOcrJson(result);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var freeText = root.GetProperty("freeText").GetString();
        Assert.Equal("Hello\nWorld", freeText);
        var lines = root.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("Hello", lines[0].GetString());
        Assert.Equal("World", lines[1].GetString());
        Assert.Equal(0, root.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public void SerializeResultAsOcrJsonHandlesEmptyResult()
    {
        var json = LocalOnnxOcrProvider.SerializeResultAsOcrJson(new OcrResult { TextBlocks = [], StrRes = string.Empty });

        using var document = JsonDocument.Parse(json);
        Assert.Equal(string.Empty, document.RootElement.GetProperty("freeText").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public void SerializeResultAsOcrJsonFiltersBlankBlocks()
    {
        var blank = new TextBlock
        {
            BoxPoints = [new SKPointI(0, 0), new SKPointI(10, 0), new SKPointI(10, 10), new SKPointI(0, 10)],
            Chars = [" ", "\t"],
            CharScores = [1f, 1f]
        };
        var real = new TextBlock
        {
            BoxPoints = [new SKPointI(0, 20), new SKPointI(10, 20), new SKPointI(10, 30), new SKPointI(0, 30)],
            Chars = ["Hi"],
            CharScores = [1f]
        };

        var json = LocalOnnxOcrProvider.SerializeResultAsOcrJson(new OcrResult { TextBlocks = [blank, real], StrRes = string.Empty });

        using var document = JsonDocument.Parse(json);
        var lines = document.RootElement.GetProperty("lines");
        Assert.Equal(1, lines.GetArrayLength());
        Assert.Equal("Hi", lines[0].GetString());
    }
}
