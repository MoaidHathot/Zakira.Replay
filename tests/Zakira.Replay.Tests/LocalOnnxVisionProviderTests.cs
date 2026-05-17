using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class LocalOnnxVisionProviderTests
{
    [Fact]
    public async Task HeuristicModeProducesValidVisionJsonShape()
    {
        using var temp = new TestTempDirectory();
        var ffmpeg = new NullFfmpegClient();
        var provider = new LocalOnnxVisionProvider(BuildHeuristicOptions(temp.Path), ffmpeg);

        var ocr = new OcrFrameStructured(
            "Quarterly Goals\n- Ship release\n- Cut latency",
            ["Quarterly Goals", "- Ship release", "- Cut latency"],
            []);
        var ocrResult = new OcrFrameResult("frame-001", "frames/frame.jpg", 0, "00:00", "Quarterly Goals\n- Ship release\n- Cut latency", Structured: ocr);

        var raw = await provider.DescribeAsync(new VisionRequest(
            ImagePath: "irrelevant.jpg",
            Instruction: string.Empty,
            Frame: new FrameArtifact("frame-001", "frames/frame.jpg", 0, "00:00"),
            OcrContext: ocrResult), CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal("slide", root.GetProperty("kind").GetString());
        Assert.Equal("Quarterly Goals", root.GetProperty("title").GetString());
        var bullets = root.GetProperty("bullets").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Assert.Equal(new[] { "Ship release", "Cut latency" }, bullets);
        Assert.NotEmpty(root.GetProperty("freeText").GetString()!);
    }

    [Fact]
    public async Task HeuristicModeHandlesNullOcrContextGracefully()
    {
        using var temp = new TestTempDirectory();
        var ffmpeg = new NullFfmpegClient();
        var provider = new LocalOnnxVisionProvider(BuildHeuristicOptions(temp.Path), ffmpeg);

        var raw = await provider.DescribeAsync(new VisionRequest(
            ImagePath: "irrelevant.jpg",
            Instruction: string.Empty,
            Frame: new FrameArtifact("frame-001", "frames/frame.jpg", 0, "00:00"),
            OcrContext: null), CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        Assert.Equal("other", document.RootElement.GetProperty("kind").GetString());
        // Empty bullets/codeBlocks/uiElements when no OCR was provided.
        Assert.Empty(document.RootElement.GetProperty("bullets").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("codeBlocks").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("uiElements").EnumerateArray());
    }

    [Fact]
    public void ClipBlipModeDegradesToHeuristicWhenModelsMissing()
    {
        using var temp = new TestTempDirectory();
        var ffmpeg = new NullFfmpegClient();
        var options = BuildHeuristicOptions(temp.Path) with
        {
            Mode = LocalVisionMode.ClipCaption,
            ClipImageEncoderPath = temp.GetPath("missing-clip-image.onnx"),
            ClipKindEmbeddingsPath = temp.GetPath("missing-clip-embeddings.bin"),
            FlorenceVisionEncoderPath = temp.GetPath("missing-florence-vision.onnx"),
            FlorenceEncoderPath = temp.GetPath("missing-florence-encoder.onnx"),
            FlorenceDecoderPath = temp.GetPath("missing-florence-decoder.onnx"),
            FlorenceEmbedTokensPath = temp.GetPath("missing-florence-embed.onnx"),
            FlorenceVocabPath = temp.GetPath("missing-florence-vocab.json"),
            FlorenceMergesPath = temp.GetPath("missing-florence-merges.txt")
        };

        using var provider = new LocalOnnxVisionProvider(options, ffmpeg);
        provider.Initialise();

        Assert.Equal(LocalVisionMode.Heuristic, provider.EffectiveMode);
        Assert.NotEmpty(provider.InitializationWarnings);
        Assert.Contains(provider.InitializationWarnings, w => w.Contains("CLIP image-encoder", StringComparison.Ordinal));
    }

    [Fact]
    public void VisionProviderFactoryNormalisesCommonAliases()
    {
        Assert.Equal(VisionProviders.Copilot, VisionProviderFactory.Normalize(null));
        Assert.Equal(VisionProviders.Copilot, VisionProviderFactory.Normalize(""));
        Assert.Equal(VisionProviders.Copilot, VisionProviderFactory.Normalize("copilot"));
        Assert.Equal(VisionProviders.Copilot, VisionProviderFactory.Normalize("openai"));
        Assert.Equal(VisionProviders.Copilot, VisionProviderFactory.Normalize("OLLAMA"));
        Assert.Equal(VisionProviders.Local, VisionProviderFactory.Normalize("local"));
        Assert.Equal(VisionProviders.Local, VisionProviderFactory.Normalize("LOCAL_ONNX"));
        Assert.Equal(VisionProviders.Local, VisionProviderFactory.Normalize("offline"));
    }

    [Theory]
    [InlineData(null, LocalVisionMode.ClipCaption)]
    [InlineData("", LocalVisionMode.ClipCaption)]
    [InlineData("heuristic", LocalVisionMode.Heuristic)]
    [InlineData("HEURISTIC", LocalVisionMode.Heuristic)]
    [InlineData("ocr-only", LocalVisionMode.Heuristic)]
    [InlineData("clip", LocalVisionMode.Clip)]
    [InlineData("zero-shot", LocalVisionMode.Clip)]
    [InlineData("clip-blip", LocalVisionMode.ClipCaption)]
    [InlineData("clip+blip", LocalVisionMode.ClipCaption)]
    [InlineData("full", LocalVisionMode.ClipCaption)]
    public void VisionProviderFactoryNormalisesLocalVisionMode(string? input, LocalVisionMode expected)
    {
        Assert.Equal(expected, VisionProviderFactory.NormalizeMode(input));
    }

    [Theory]
    [InlineData(LocalVisionMode.Heuristic, "heuristic")]
    [InlineData(LocalVisionMode.Clip, "clip")]
    [InlineData(LocalVisionMode.ClipCaption, "clip-caption")]
    public void FormatModeReturnsCanonicalString(LocalVisionMode mode, string expected)
    {
        Assert.Equal(expected, VisionProviderFactory.FormatMode(mode));
    }

    [Fact]
    public void LocalVisionOptionsRequiredFilesEmptyForHeuristicMode()
    {
        var options = BuildHeuristicOptions("dummy");
        Assert.Empty(options.RequiredFilesFor(LocalVisionMode.Heuristic));
        Assert.Empty(options.MissingFilesFor(LocalVisionMode.Heuristic));
    }

    [Fact]
    public void LocalVisionOptionsMissingFilesListsAllUnresolvedPaths()
    {
        using var temp = new TestTempDirectory();
        var options = BuildHeuristicOptions(temp.Path) with
        {
            ClipImageEncoderPath = temp.GetPath("missing-clip-image.onnx"),
            ClipKindEmbeddingsPath = temp.GetPath("missing-embeddings.bin")
        };

        var missing = options.MissingFilesFor(LocalVisionMode.Clip);
        Assert.Equal(2, missing.Count);
    }

    private static LocalVisionOptions BuildHeuristicOptions(string baseDir)
    {
        return new LocalVisionOptions(
            Mode: LocalVisionMode.Heuristic,
            Quantization: LocalVisionOptions.DefaultQuantization,
            ClipImageEncoderPath: Path.Combine(baseDir, "clip-image-encoder.onnx"),
            ClipTextEncoderPath: Path.Combine(baseDir, "clip-text-encoder.onnx"),
            ClipKindEmbeddingsPath: Path.Combine(baseDir, "clip-kind-embeddings.bin"),
            FlorenceVisionEncoderPath: Path.Combine(baseDir, "florence-vision-encoder.onnx"),
            FlorenceEncoderPath: Path.Combine(baseDir, "florence-encoder.onnx"),
            FlorenceDecoderPath: Path.Combine(baseDir, "florence-decoder.onnx"),
            FlorenceEmbedTokensPath: Path.Combine(baseDir, "florence-embed-tokens.onnx"),
            FlorenceVocabPath: Path.Combine(baseDir, "florence-vocab.json"),
            FlorenceMergesPath: Path.Combine(baseDir, "florence-merges.txt"),
            FlorenceAddedTokensPath: Path.Combine(baseDir, "florence-added-tokens.json"),
            FlorenceMaxTokens: LocalVisionOptions.DefaultFlorenceMaxTokens,
            AutoDownload: false,
            ModelDirectory: baseDir);
    }

    private sealed class NullFfmpegClient : IFfmpegClient
    {
        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAsync(string mediaSource, VideoRun run, int count, double? durationSeconds, string strategy, int sceneSafetyCap, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractFramesAtAsync(string mediaSource, VideoRun run, IReadOnlyList<TimeSpan> timestamps, FrameCaptureOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<IReadOnlyList<FrameArtifact>> ExtractSceneFramesInRangeAsync(string mediaSource, VideoRun run, TimeSpan rangeStart, TimeSpan rangeEnd, int sceneSafetyCap, FrameCaptureOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FrameArtifact>>([]);

        public Task<string> ExtractAudioAsync(string mediaSource, VideoRun run, CancellationToken cancellationToken)
            => Task.FromResult("audio/audio.wav");

        public Task<string> ExtractClipAsync(string mediaSource, VideoRun run, TimeSpan start, TimeSpan end, string? outputName, CancellationToken cancellationToken)
            => Task.FromResult("clips/clip.mp4");

        public Task<double?> TryProbeDurationAsync(string mediaSource, CancellationToken cancellationToken)
            => Task.FromResult<double?>(null);

        public Task<IReadOnlyList<SilenceWindow>> DetectSilenceAsync(string mediaSource, SilenceDetectionOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SilenceWindow>>([]);

        public Task ExtractAudioRangeAsync(string mediaSource, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<byte[]?> PreprocessImageRgb24Async(string imagePath, int width, int height, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task<string?> ComputePerceptualHashAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>("0000000000000000");
    }
}
