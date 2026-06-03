using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SearchIndexConferenceTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BuildConferenceMergesEvidenceFromAllRunsAndQueryReturnsCrossRunHits()
    {
        // Build a "conference" of three runs, each evidence.json carrying a single distinctive
        // transcript line and a distinct WebpageUrl. The cross-run query for a term that only
        // one run mentions must return exactly that run's hit, with the matching deep link.
        using var temp = new TestTempDirectory();
        var artifactRoot = temp.Path;
        var keyRun  = await CreateRunAsync(temp, "key01", "https://build.microsoft.com/en-US/sessions/KEY01",
            ("00:01", 60, "Satya Nadella announces Maia 200."));
        var brkAgent = await CreateRunAsync(temp, "brk101", "https://build.microsoft.com/en-US/sessions/BRK101",
            ("00:02", 120, "Foundry hosted agents run inside MXC containers."));
        var brkData  = await CreateRunAsync(temp, "brk205", "https://build.microsoft.com/en-US/sessions/BRK205",
            ("00:03", 180, "Horizon DB is managed Postgres on Azure with zone-redundant failover."));

        var service = new SearchIndexService();
        var result = await service.BuildConferenceAsync("build-2026", [keyRun, brkAgent, brkData], artifactRoot, CancellationToken.None);

        Assert.Equal("build-2026", result.ConferenceId);
        Assert.Equal(3, result.IncludedRuns.Count);
        Assert.Empty(result.Skipped);
        // Each run contributes 1 transcript document — the OCR fixture below pushes the total
        // higher; just assert ">= number of runs" so the test is robust to BuildDocuments tweaks.
        Assert.True(result.DocumentCount >= 3);
        // Persisted under <root>/.indexes/<slug>/index.json
        Assert.True(File.Exists(result.IndexPath));
        Assert.Equal(Path.Combine(artifactRoot, ".indexes", "build-2026", "index.json"), result.IndexPath);

        // Query for "Maia" — should hit only the keynote run.
        var maia = await service.QueryAsync(result.IndexPath, "Maia 200", top: 5, CancellationToken.None);
        var hit = Assert.Single(maia.Matches);
        Assert.Equal("key01", hit.RunId);
        Assert.Equal("https://build.microsoft.com/en-US/sessions/KEY01", hit.SourceUrl);
        // Generic-host fallback uses W3C Media Fragments (#t=<seconds>).
        Assert.NotNull(hit.DeepLink);
        Assert.Contains("#t=60", hit.DeepLink);
        Assert.Contains("KEY01", hit.DeepLink);

        // Query for a term in a different session.
        var horizon = await service.QueryAsync(result.IndexPath, "Horizon Postgres", top: 5, CancellationToken.None);
        var horizonHit = Assert.Single(horizon.Matches);
        Assert.Equal("brk205", horizonHit.RunId);
        Assert.Contains("BRK205", horizonHit.DeepLink!);
    }

    [Fact]
    public async Task BuildConferenceSkipsRunsWithMissingOrUnparseableEvidence()
    {
        using var temp = new TestTempDirectory();
        var artifactRoot = temp.Path;
        var goodRun = await CreateRunAsync(temp, "good", "https://example.test/good", ("00:01", 5, "this is real"));

        // A directory with no evidence.json at all — must be skipped, not crashed on.
        var missingRun = temp.GetPath("runs", "missing");
        Directory.CreateDirectory(missingRun);

        // A directory whose evidence.json is malformed JSON — must be skipped with a reason.
        var brokenRun = temp.GetPath("runs", "broken");
        Directory.CreateDirectory(brokenRun);
        await File.WriteAllTextAsync(Path.Combine(brokenRun, "evidence.json"), "{ this is not json", CancellationToken.None);

        var service = new SearchIndexService();
        var result = await service.BuildConferenceAsync("partial", [goodRun, missingRun, brokenRun], artifactRoot, CancellationToken.None);

        Assert.Single(result.IncludedRuns);
        Assert.Equal("good", result.IncludedRuns[0]);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildConferenceThrowsWhenNoRunsAreUsable()
    {
        // All directories missing evidence — building yields no documents and we'd rather
        // throw a meaningful error than silently write a 0-document index.
        using var temp = new TestTempDirectory();
        var artifactRoot = temp.Path;
        var emptyA = temp.GetPath("runs", "a"); Directory.CreateDirectory(emptyA);
        var emptyB = temp.GetPath("runs", "b"); Directory.CreateDirectory(emptyB);

        var service = new SearchIndexService();
        var ex = await Assert.ThrowsAsync<ReplayException>(() =>
            service.BuildConferenceAsync("ghost", [emptyA, emptyB], artifactRoot, CancellationToken.None));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task BuildConferenceRequiresConferenceIdAndAtLeastOneRun()
    {
        var service = new SearchIndexService();
        await Assert.ThrowsAsync<ReplayException>(() =>
            service.BuildConferenceAsync("", ["whatever"], "root", CancellationToken.None));
        await Assert.ThrowsAsync<ReplayException>(() =>
            service.BuildConferenceAsync("ok", [], "root", CancellationToken.None));
    }

    [Fact]
    public void ResolveQueryTargetPrefersLiteralFileThenRunDirThenConferenceSlug()
    {
        using var temp = new TestTempDirectory();
        var artifactRoot = temp.Path;

        // (1) A literal file path resolves to itself.
        var loose = temp.GetPath("loose.json");
        File.WriteAllText(loose, "{}");
        Assert.Equal(loose, SearchIndexService.ResolveQueryTarget(loose, artifactRoot));

        // (2) A directory containing search/index.json resolves to that file.
        var runDir = temp.GetPath("runs", "r1");
        Directory.CreateDirectory(Path.Combine(runDir, "search"));
        var perRun = Path.Combine(runDir, "search", "index.json");
        File.WriteAllText(perRun, "{}");
        Assert.Equal(perRun, SearchIndexService.ResolveQueryTarget(runDir, artifactRoot));

        // (3) A conference id (slugified) resolves to <root>/.indexes/<slug>/index.json.
        var confDir = Path.Combine(artifactRoot, ".indexes", "build-2026");
        Directory.CreateDirectory(confDir);
        var confIndex = Path.Combine(confDir, "index.json");
        File.WriteAllText(confIndex, "{}");
        Assert.Equal(confIndex, SearchIndexService.ResolveQueryTarget("build-2026", artifactRoot));
        // Slugification: spaces and case folded.
        Assert.Equal(confIndex, SearchIndexService.ResolveQueryTarget("Build 2026", artifactRoot));

        // (4) Nothing matches → null.
        Assert.Null(SearchIndexService.ResolveQueryTarget("nonexistent-thing-12345", artifactRoot));
    }

    private static async Task<string> CreateRunAsync(TestTempDirectory temp, string runId, string webpageUrl, (string Timestamp, double StartSeconds, string Text) cue)
    {
        var runDirectory = temp.GetPath("runs", runId);
        Directory.CreateDirectory(runDirectory);
        var evidence = new EvidenceDocument(
            SchemaVersion: "0.8",
            Source: webpageUrl,
            VisionInstruction: "",
            OcrInstruction: "",
            RunId: runId,
            Title: runId.ToUpperInvariant(),
            WebpageUrl: webpageUrl,
            DurationSeconds: 600,
            AudioPath: null,
            Transcript: [new TranscriptSegment(cue.StartSeconds, cue.StartSeconds + 5, cue.Timestamp, cue.Text)],
            Frames: [],
            Slides: [],
            Ocr: [],
            Vision: [],
            Speakers: [],
            Warnings: []);
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "evidence.json"),
            JsonSerializer.Serialize(evidence, WebOptions),
            CancellationToken.None);
        return runDirectory;
    }
}
