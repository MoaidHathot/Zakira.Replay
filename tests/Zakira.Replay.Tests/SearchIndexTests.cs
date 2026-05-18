using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SearchIndexTests
{
    [Fact]
    public async Task SearchIndexBuildAndQueryReturnsRelevantEvidenceMatches()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        var service = new SearchIndexService();

        var manifest = await service.BuildAsync(runDirectory, CancellationToken.None);
        var result = await service.QueryAsync(runDirectory, "wireguard vpn", top: 3, CancellationToken.None);

        Assert.Equal(3, manifest.DocumentCount);
        Assert.NotEmpty(result.Matches);
        Assert.Contains(result.Matches, match => match.Text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.json")));
    }

    [Fact]
    public async Task SqliteSearchIndexBuildAndQueryReturnsFtsMatches()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        var service = new SearchIndexService();

        var build = await service.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.Sqlite), CancellationToken.None);
        var result = await service.QueryAsync(runDirectory, "wireguard vpn", top: 3, new SearchIndexQueryOptions(SearchBackends.Sqlite), CancellationToken.None);

        Assert.Equal(SearchBackends.Sqlite, build.Backend);
        Assert.Equal(3, build.DocumentCount);
        Assert.NotEmpty(result.Matches);
        Assert.Contains(result.Matches, match => match.Text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.sqlite")));
    }

    [Fact]
    public async Task SqliteOnnxSearchUsesEmbeddingProviderWhenConfigured()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        var service = new SearchIndexService(new FakeEmbeddingProvider("bge-small-en-v1.5", "bge"));

        await service.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.SqliteOnnx), CancellationToken.None);
        var result = await service.QueryAsync(runDirectory, "secure tunnel", top: 1, new SearchIndexQueryOptions(SearchBackends.SqliteOnnx), CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Contains("WireGuard", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqliteOnnxQueryThrowsWhenEmbeddingModelDiffersFromIndex()
    {
        // Build the index with one model and then query it with a different model. The
        // 0.10.0 mismatch guard must fire because cross-model vector spaces are not
        // interchangeable even at matching dimensions.
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        var indexService = new SearchIndexService(new FakeEmbeddingProvider("bge-small-en-v1.5", "bge"));
        await indexService.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.SqliteOnnx), CancellationToken.None);

        var queryService = new SearchIndexService(new FakeEmbeddingProvider("multilingual-e5-small", "e5"));
        var ex = await Assert.ThrowsAsync<SearchIndexEmbeddingMismatchException>(() => queryService.QueryAsync(runDirectory, "secure tunnel", 1, new SearchIndexQueryOptions(SearchBackends.SqliteOnnx), CancellationToken.None));

        Assert.Equal("bge-small-en-v1.5", ex.IndexedModelId);
        Assert.Equal("multilingual-e5-small", ex.RuntimeModelId);
        Assert.Contains(SearchIndexEmbeddingMismatchException.Code, ex.Message, StringComparison.Ordinal);
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteOnnxRebuildRotatesEmbeddingModelMetadata()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        var first = new SearchIndexService(new FakeEmbeddingProvider("bge-small-en-v1.5", "bge"));
        await first.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.SqliteOnnx), CancellationToken.None);

        // Rebuild in place with a different model and confirm the same provider can query
        // the rotated index without tripping the mismatch guard.
        var second = new SearchIndexService(new FakeEmbeddingProvider("multilingual-e5-small", "e5"));
        await second.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.SqliteOnnx), CancellationToken.None);
        var result = await second.QueryAsync(runDirectory, "secure tunnel", 1, new SearchIndexQueryOptions(SearchBackends.SqliteOnnx), CancellationToken.None);

        Assert.NotEmpty(result.Matches);
    }

    [Theory]
    [InlineData("bge-small-en-v1.5", SearchEmbeddingModelKind.Bge)]
    [InlineData("snowflake-arctic-embed-s", SearchEmbeddingModelKind.Bge)]
    [InlineData("multilingual-e5-small", SearchEmbeddingModelKind.E5)]
    [InlineData("e5-small-v2", SearchEmbeddingModelKind.E5)]
    [InlineData("all-MiniLM-L6-v2", SearchEmbeddingModelKind.Bert)]
    [InlineData(null, SearchEmbeddingModelKind.Bert)]
    public void ResolveKindMapsModelIdToCorrectScheme(string? modelId, SearchEmbeddingModelKind expected)
    {
        Assert.Equal(expected, OnnxSearchEmbeddingProvider.ResolveKind(modelId));
    }

    [Theory]
    [InlineData("bert", SearchEmbeddingModelKind.Bert)]
    [InlineData("BGE", SearchEmbeddingModelKind.Bge)]
    [InlineData(" e5 ", SearchEmbeddingModelKind.E5)]
    [InlineData("snowflake-arctic-embed-s", SearchEmbeddingModelKind.Bge)]
    public void ResolveKindRespectsExplicitOverride(string explicitKind, SearchEmbeddingModelKind expected)
    {
        Assert.Equal(expected, OnnxSearchEmbeddingProvider.ResolveKind(modelId: "some-custom-model", explicitKind: explicitKind));
    }

    [Fact]
    public void KnownSearchEmbeddingModelsRegistryContainsThreeEntries()
    {
        Assert.Contains(KnownSearchEmbeddingModels.BgeSmallEnV15, KnownSearchEmbeddingModels.Ids);
        Assert.Contains(KnownSearchEmbeddingModels.SnowflakeArcticEmbedS, KnownSearchEmbeddingModels.Ids);
        Assert.Contains(KnownSearchEmbeddingModels.MultilingualE5Small, KnownSearchEmbeddingModels.Ids);
        Assert.Equal(KnownSearchEmbeddingModels.BgeSmallEnV15, KnownSearchEmbeddingModels.DefaultModel);

        var bge = KnownSearchEmbeddingModels.Get(KnownSearchEmbeddingModels.BgeSmallEnV15);
        Assert.Equal(SearchEmbeddingModelKind.Bge, bge.ModelKind);
        Assert.Equal(384, bge.EmbeddingDimensions);
        Assert.Equal("vocab.txt", bge.TokenizerFileName);

        var e5 = KnownSearchEmbeddingModels.Get(KnownSearchEmbeddingModels.MultilingualE5Small);
        Assert.Equal(SearchEmbeddingModelKind.E5, e5.ModelKind);
        Assert.Equal("sentencepiece.bpe.model", e5.TokenizerFileName);
    }

    [Fact]
    public void KnownSearchEmbeddingModelsRejectsUnknownIdsWithActionableMessage()
    {
        var ex = Assert.Throws<ReplayException>(() => KnownSearchEmbeddingModels.Get("totally-made-up-model"));
        Assert.Contains("Known ids:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bge-small-en-v1.5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCliBuildAcceptsSqliteBackend()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Cli.CliApp.RunAsync(["index", "build", runDirectory, "--backend", "sqlite"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("with sqlite backend", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.sqlite")));
    }

    [Fact]
    public async Task SearchCliIndexBuildAcceptsOnnxModelFlag()
    {
        // System.CommandLine's --help handler writes to System.Console rather than the
        // injected TextWriter, so we verify the new --onnx-model flag is wired by running
        // a real `index build` with `--onnx-model bge-small-en-v1.5 --backend sqlite`
        // against a real run directory. Sqlite backend skips the actual ONNX provider so
        // no model files have to be on disk for this test; we only care that the parser
        // accepts the new flag without an "unrecognized option" error.
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Cli.CliApp.RunAsync(
            ["index", "build", runDirectory, "--backend", "sqlite", "--onnx-model", "bge-small-en-v1.5", "--onnx-model-kind", "bge"],
            stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(runDirectory, "search", "index.sqlite")));
    }

    private static async Task<string> CreateSearchRunAsync(TestTempDirectory temp)
    {
        var runDirectory = temp.GetPath("runs", "search-run");
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: "source.mp4",
            VisionInstruction: "test",

            OcrInstruction: "",
            RunId: "search-run",
            Title: "Search Fixture",
            WebpageUrl: null,
            DurationSeconds: 120,
            AudioPath: null,
            Transcript:
            [
                new TranscriptSegment(10, 20, "00:10", "The router supports WireGuard VPN performance testing."),
                new TranscriptSegment(30, 40, "00:30", "The speaker discusses travel bags and accessories.")
            ],
            Frames: [],
            Slides: [],
            Ocr: [new OcrFrameResult("frame-001", "frames/frame-001.jpg", 11, "00:11", "WireGuard throughput chart")],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        return runDirectory;
    }

    private sealed class FakeEmbeddingProvider : ISearchEmbeddingProvider
    {
        public FakeEmbeddingProvider(string modelId = "fake-model", string modelKind = "bert")
        {
            ModelId = modelId;
            ModelKind = modelKind;
        }

        public string Name => "fake";

        public string ModelId { get; }

        public string ModelKind { get; }

        public int Dimensions => 3;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var normalized = text.ToLowerInvariant();
            if (normalized.Contains("wireguard", StringComparison.Ordinal) || normalized.Contains("secure", StringComparison.Ordinal) || normalized.Contains("tunnel", StringComparison.Ordinal))
            {
                return Task.FromResult(new[] { 1f, 0f, 0f });
            }

            if (normalized.Contains("travel", StringComparison.Ordinal) || normalized.Contains("bags", StringComparison.Ordinal))
            {
                return Task.FromResult(new[] { 0f, 1f, 0f });
            }

            return Task.FromResult(new[] { 0f, 0f, 1f });
        }
    }
}
