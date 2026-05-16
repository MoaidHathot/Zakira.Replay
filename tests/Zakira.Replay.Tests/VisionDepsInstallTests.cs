using System.Net.Http;
using Zakira.Replay.Cli;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

/// <summary>
/// Integration tests for the vision-models install + clip-embeddings-generation path.
/// <para>
/// Three categories of test live here:
/// <list type="bullet">
///   <item><description>Pure tokenizer unit tests (always run).</description></item>
///   <item><description><see cref="SkippableFactAttribute"/> integration tests that need the
///   CLIP ONNX files on disk — skipped when models are absent so CI without network access
///   stays green.</description></item>
///   <item><description><see cref="SkippableFactAttribute"/> tests against a real Hugging Face
///   download — skipped by default to avoid hammering the mirror in regular test runs;
///   enabled by setting <c>ZAKIRA_REPLAY_TEST_NETWORK=1</c>.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class VisionDepsInstallTests
{
    [Fact]
    public void ClipBpeTokenizerProducesFixedLengthSequences()
    {
        var modelDir = ResolveVisionModelDirectory();
        var vocab = Path.Combine(modelDir, "clip-vocab.json");
        var merges = Path.Combine(modelDir, "clip-merges.txt");
        Skip.If(!File.Exists(vocab) || !File.Exists(merges),
            "CLIP tokenizer files not on disk; install with `zakira-replay deps install vision --mode clip`.");

        var tokenizer = ClipBpeTokenizer.FromFiles(vocab, merges);
        var ids = tokenizer.Tokenize("a photograph of a presentation slide");

        Assert.Equal(ClipBpeTokenizer.MaxSequenceLength, ids.Length);
        Assert.Equal(ClipBpeTokenizer.BosTokenId, ids[0]);
        Assert.Contains(ClipBpeTokenizer.EosTokenId, ids);
    }

    [Fact]
    public void ClipBpeTokenizerProducesIdenticalIdsForIdenticalInput()
    {
        var modelDir = ResolveVisionModelDirectory();
        var vocab = Path.Combine(modelDir, "clip-vocab.json");
        var merges = Path.Combine(modelDir, "clip-merges.txt");
        Skip.If(!File.Exists(vocab) || !File.Exists(merges),
            "CLIP tokenizer files not on disk; install with `zakira-replay deps install vision --mode clip`.");

        var tokenizer = ClipBpeTokenizer.FromFiles(vocab, merges);
        var a = tokenizer.Tokenize("a screenshot of source code");
        var b = tokenizer.Tokenize("a screenshot of source code");

        Assert.Equal(a, b);
    }

    [SkippableFact]
    public async Task GenerateClipEmbeddingsProducesExpectedBinarySize()
    {
        var modelDir = ResolveVisionModelDirectory();
        var textEncoder = Path.Combine(modelDir, "clip-text-encoder.onnx");
        var vocab = Path.Combine(modelDir, "clip-vocab.json");
        var merges = Path.Combine(modelDir, "clip-merges.txt");

        Skip.If(!File.Exists(textEncoder) || !File.Exists(vocab) || !File.Exists(merges),
            "CLIP model files not on disk; install with `zakira-replay deps install vision --mode clip`.");

        using var temp = new TestTempDirectory();
        var output = temp.GetPath("clip-kind-embeddings.bin");
        using var stdout = new StringWriter();

        var exitCode = await VisionGenerateClipEmbeddingsCommand.RunAsync(
            [
                "--text-encoder", textEncoder,
                "--vocab", vocab,
                "--merges", merges,
                "--out", output
            ],
            stdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        var bytes = await File.ReadAllBytesAsync(output);

        // CLIP ViT-B/32 produces 512-dim embeddings; 7 prompts × 512 floats × 4 bytes = 14336 bytes.
        // Different CLIP variants would change the dimension; this test pins the contract for the
        // canonical ViT-B/32 export.
        Assert.Equal(7 * 512 * sizeof(float), bytes.Length);
    }

    [SkippableFact]
    public async Task GenerateClipEmbeddingsProducesNormalisedVectors()
    {
        var modelDir = ResolveVisionModelDirectory();
        var textEncoder = Path.Combine(modelDir, "clip-text-encoder.onnx");
        var vocab = Path.Combine(modelDir, "clip-vocab.json");
        var merges = Path.Combine(modelDir, "clip-merges.txt");

        Skip.If(!File.Exists(textEncoder) || !File.Exists(vocab) || !File.Exists(merges),
            "CLIP model files not on disk; install with `zakira-replay deps install vision --mode clip`.");

        using var temp = new TestTempDirectory();
        var output = temp.GetPath("clip-kind-embeddings.bin");
        using var stdout = new StringWriter();

        await VisionGenerateClipEmbeddingsCommand.RunAsync(
            [
                "--text-encoder", textEncoder,
                "--vocab", vocab,
                "--merges", merges,
                "--out", output
            ],
            stdout,
            CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(output);
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);

        // Each of the 7 embeddings should be (close to) unit-length after L2 normalisation.
        for (var row = 0; row < 7; row++)
        {
            var sumSq = 0f;
            for (var i = 0; i < 512; i++)
            {
                var v = floats[row * 512 + i];
                sumSq += v * v;
            }

            Assert.InRange(MathF.Sqrt(sumSq), 0.95f, 1.05f);
        }
    }

    [SkippableFact]
    public async Task InstallVisionClipModeDownloadsAllFourFiles()
    {
        Skip.If(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_TEST_NETWORK") != "1",
            "Network-touching tests are disabled by default. Set ZAKIRA_REPLAY_TEST_NETWORK=1 to enable.");

        using var temp = new TestTempDirectory();
        var portableDir = temp.GetPath("portable");
        Directory.CreateDirectory(portableDir);

        var config = new ReplayConfig
        {
            Dependencies = new DependencyPathConfig { PortableDirectory = portableDir },
            Vision = new VisionConfig
            {
                Local = new LocalVisionConfig { ModelDirectory = Path.Combine(portableDir, "models", "vision") }
            }
        };

        using var httpClient = new HttpClient();
        var installer = new PortableDependencyInstaller(config, httpClient);
        var result = await installer.InstallAsync(
            targets: ["vision"],
            force: false,
            progress: null,
            cancellationToken: CancellationToken.None,
            whisperModelSize: null,
            ocrLanguagePack: null,
            visionMode: "clip");

        Assert.Equal(4, result.Items.Count);
        Assert.All(result.Items, item => Assert.True(File.Exists(item.Path), $"Expected file: {item.Path}"));

        // Smoke-check the encoder file size is reasonable (Xenova quantized vision encoder is ~85 MB).
        var imageEncoder = Path.Combine(result.VisionModelDirectory, LocalVisionOptions.ClipImageEncoderFile);
        Assert.True(new FileInfo(imageEncoder).Length > 10 * 1024 * 1024);
    }

    [Fact]
    public async Task GenerateClipEmbeddingsFailsClearlyWhenTextEncoderMissing()
    {
        using var temp = new TestTempDirectory();
        var bogus = temp.GetPath("not-there.onnx");
        using var stdout = new StringWriter();

        var ex = await Assert.ThrowsAsync<ReplayException>(() =>
            VisionGenerateClipEmbeddingsCommand.RunAsync(
                [
                    "--text-encoder", bogus,
                    "--vocab", temp.GetPath("v.json"),
                    "--merges", temp.GetPath("m.txt"),
                    "--out", temp.GetPath("out.bin")
                ],
                stdout,
                CancellationToken.None));

        Assert.Contains("CLIP text-encoder ONNX not found", ex.Message);
    }

    private static string ResolveVisionModelDirectory()
    {
        return new PortableDependencyInstaller().Layout.VisionModelDirectory;
    }
}
