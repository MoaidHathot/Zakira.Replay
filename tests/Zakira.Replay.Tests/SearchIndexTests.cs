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
        var service = new SearchIndexService(new FakeEmbeddingProvider());

        await service.BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.SqliteOnnx), CancellationToken.None);
        var result = await service.QueryAsync(runDirectory, "secure tunnel", top: 1, new SearchIndexQueryOptions(SearchBackends.SqliteOnnx), CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Contains("WireGuard", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchCliBuildAcceptsSqliteBackend()
    {
        using var temp = new TestTempDirectory();
        var runDirectory = await CreateSearchRunAsync(temp);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Cli.CliApp.RunAsync(["search", "build", runDirectory, "--backend", "sqlite"], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("with sqlite backend", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
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
        public string Name => "fake";

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
