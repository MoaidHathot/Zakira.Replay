using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

public sealed partial class SearchIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISearchEmbeddingProvider? embeddingProvider;

    public SearchIndexService(ISearchEmbeddingProvider? embeddingProvider = null)
    {
        this.embeddingProvider = embeddingProvider;
    }

    public async Task<SearchIndexManifest> BuildAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var result = await BuildAsync(runDirectory, new SearchIndexBuildOptions(SearchBackends.Json), cancellationToken).ConfigureAwait(false);
        return result.Manifest ?? throw new ReplayException("JSON search index manifest was not produced.");
    }

    public async Task<SearchIndexBuildResult> BuildAsync(string runDirectory, SearchIndexBuildOptions options, CancellationToken cancellationToken)
    {
        var backend = SearchBackends.Normalize(options.Backend);
        return backend switch
        {
            SearchBackends.Json => await BuildJsonAsync(Path.GetFullPath(runDirectory), cancellationToken).ConfigureAwait(false),
            SearchBackends.Sqlite or SearchBackends.SqliteOnnx => await BuildSqliteAsync(Path.GetFullPath(runDirectory), options with { Backend = backend }, cancellationToken).ConfigureAwait(false),
            _ => throw new ReplayException($"Unknown search backend: {options.Backend}")
        };
    }

    private async Task<SearchIndexBuildResult> BuildJsonAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var evidencePath = Path.Combine(runDirectory, "evidence.json");
        if (!File.Exists(evidencePath))
        {
            throw new ReplayException($"evidence.json was not found: {evidencePath}");
        }

        await using var stream = File.OpenRead(evidencePath);
        var evidence = await JsonSerializer.DeserializeAsync<EvidenceDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ReplayException("evidence.json is empty or invalid.");
        var documents = BuildDocuments(evidence).Where(document => !string.IsNullOrWhiteSpace(document.Text)).ToArray();
        var documentTokens = documents.Select(document => Tokenize(document.Text).ToArray()).ToArray();
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tokens in documentTokens)
        {
            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var indexDocuments = new List<SearchIndexDocument>();
        for (var i = 0; i < documents.Length; i++)
        {
            var weights = BuildVector(documentTokens[i], documentFrequency, documents.Length);
            indexDocuments.Add(new SearchIndexDocument(
                documents[i].Id,
                documents[i].Kind,
                documents[i].Text,
                documents[i].StartSeconds,
                documents[i].EndSeconds,
                documents[i].Timestamp,
                documents[i].Path,
                weights,
                RunId: documents[i].RunId,
                SourceUrl: documents[i].SourceUrl));
        }

        var manifest = new SearchIndexManifest(
            "0.1",
            evidence.RunId,
            DateTimeOffset.UtcNow,
            indexDocuments.Count,
            indexDocuments,
            SourceUrl: string.IsNullOrWhiteSpace(evidence.WebpageUrl) ? evidence.Source : evidence.WebpageUrl);
        var indexDirectory = Path.Combine(runDirectory, "search");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, "index.json");
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return new SearchIndexBuildResult(SearchBackends.Json, evidence.RunId, indexDocuments.Count, indexPath, manifest.CreatedAt, manifest);
    }

    /// <summary>
    /// Aggregate <c>evidence.json</c> from every directory in <paramref name="runDirectories"/>
    /// into a single cross-run / conference index, persisted at
    /// <c>&lt;artifactRoot&gt;/.indexes/&lt;conferenceId&gt;/index.json</c>. Each indexed
    /// document carries its origin run's <see cref="SearchIndexDocument.RunId"/> and
    /// <see cref="SearchIndexDocument.SourceUrl"/> so query results can attribute hits to a
    /// specific session and produce a deep-link per match.
    /// </summary>
    /// <remarks>
    /// Document ids are namespaced with the run id (<c>&lt;runId&gt;:&lt;original-id&gt;</c>)
    /// to keep collisions impossible across runs that share the same per-kind sequence (every
    /// run starts at <c>transcript-0001</c>). TF-IDF document-frequency is computed across
    /// the merged corpus, not per-run — this is what makes cross-run scoring meaningful.
    /// Missing or malformed evidence files are skipped with an entry in
    /// <see cref="SearchIndexConferenceBuildResult.Skipped"/>.
    /// </remarks>
    public async Task<SearchIndexConferenceBuildResult> BuildConferenceAsync(
        string conferenceId,
        IReadOnlyList<string> runDirectories,
        string artifactRootDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conferenceId))
        {
            throw new ReplayException("conferenceId is required.");
        }
        if (runDirectories.Count == 0)
        {
            throw new ReplayException("BuildConferenceAsync requires at least one run directory.");
        }

        var slug = Slug.Create(conferenceId, 80);
        var allSources = new List<SearchSourceDocument>();
        var includedRuns = new List<string>();
        var skipped = new List<SearchIndexSkippedRun>();

        foreach (var dir in runDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidencePath = Path.Combine(dir, "evidence.json");
            if (!File.Exists(evidencePath))
            {
                skipped.Add(new SearchIndexSkippedRun(dir, "missing evidence.json"));
                continue;
            }

            EvidenceDocument? evidence;
            try
            {
                await using var stream = File.OpenRead(evidencePath);
                evidence = await JsonSerializer.DeserializeAsync<EvidenceDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                skipped.Add(new SearchIndexSkippedRun(dir, ex.Message));
                continue;
            }
            if (evidence is null)
            {
                skipped.Add(new SearchIndexSkippedRun(dir, "empty evidence.json"));
                continue;
            }

            var runUrl = string.IsNullOrWhiteSpace(evidence.WebpageUrl) ? evidence.Source : evidence.WebpageUrl;
            // Namespace ids with the run id so the merged corpus has globally-unique ids.
            // (Per-run document streams reset to "<kind>-0001" so cross-run collisions are
            // otherwise certain.)
            foreach (var doc in BuildDocuments(evidence).Where(d => !string.IsNullOrWhiteSpace(d.Text)))
            {
                allSources.Add(doc with
                {
                    Id = $"{evidence.RunId}:{doc.Id}",
                    RunId = evidence.RunId,
                    SourceUrl = runUrl,
                });
            }
            includedRuns.Add(evidence.RunId);
        }

        if (allSources.Count == 0)
        {
            throw new ReplayException(
                $"No usable evidence.json files were found among {runDirectories.Count} run director" +
                $"y(ies); cannot build conference '{conferenceId}'.");
        }

        // Cross-corpus TF-IDF: doc-frequency is the count over EVERY run's documents combined.
        // This is what makes a per-run rare term ("Foundry" in a single session) rank higher
        // than a globally-common term ("the") across the conference.
        var allTokens = allSources.Select(d => Tokenize(d.Text).ToArray()).ToArray();
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tokens in allTokens)
        {
            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var indexDocuments = new List<SearchIndexDocument>(allSources.Count);
        for (var i = 0; i < allSources.Count; i++)
        {
            var weights = BuildVector(allTokens[i], documentFrequency, allSources.Count);
            var src = allSources[i];
            indexDocuments.Add(new SearchIndexDocument(
                src.Id, src.Kind, src.Text, src.StartSeconds, src.EndSeconds, src.Timestamp, src.Path,
                weights,
                RunId: src.RunId,
                SourceUrl: src.SourceUrl));
        }

        var manifest = new SearchIndexManifest(
            "0.1",
            // Stamp the manifest's RunId field with the conference slug so a query against the
            // top-level field still identifies the index. Per-document RunId attributes hits to
            // the originating session.
            RunId: slug,
            DateTimeOffset.UtcNow,
            indexDocuments.Count,
            indexDocuments,
            SourceUrl: null);

        var conferenceDir = Path.Combine(artifactRootDirectory, ".indexes", slug);
        Directory.CreateDirectory(conferenceDir);
        var indexPath = Path.Combine(conferenceDir, "index.json");
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        return new SearchIndexConferenceBuildResult(
            ConferenceId: slug,
            IndexPath: indexPath,
            DocumentCount: indexDocuments.Count,
            IncludedRuns: includedRuns,
            Skipped: skipped,
            CreatedAt: manifest.CreatedAt);
    }

    /// <summary>
    /// Resolve a query target string to an index file path. Order:
    /// (1) literal file path; (2) run directory (looks for <c>search/index.json|sqlite</c>);
    /// (3) conference id (resolves to <c>&lt;artifactRoot&gt;/.indexes/&lt;slug&gt;/index.json</c>).
    /// Returns the resolved path, or null when nothing matches (so the caller can decide
    /// whether to throw or fall through).
    /// </summary>
    public static string? ResolveQueryTarget(string target, string artifactRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (File.Exists(target)) return target;

        if (Directory.Exists(target))
        {
            var perRunJson = Path.Combine(target, "search", "index.json");
            if (File.Exists(perRunJson)) return perRunJson;
            var perRunSqlite = Path.Combine(target, "search", "index.sqlite");
            if (File.Exists(perRunSqlite)) return perRunSqlite;
        }

        var slug = Slug.Create(target, 80);
        var conferenceJson = Path.Combine(artifactRootDirectory, ".indexes", slug, "index.json");
        if (File.Exists(conferenceJson)) return conferenceJson;

        return null;
    }

    public async Task<SearchQueryResult> QueryAsync(string indexPathOrRunDirectory, string query, int top, CancellationToken cancellationToken)
    {
        return await QueryJsonAsync(indexPathOrRunDirectory, query, top, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchQueryResult> QueryAsync(string indexPathOrRunDirectory, string query, int top, SearchIndexQueryOptions options, CancellationToken cancellationToken)
    {
        var backend = SearchBackends.Normalize(options.Backend);
        if (backend == SearchBackends.Auto)
        {
            backend = DetectQueryBackend(indexPathOrRunDirectory);
        }

        return backend switch
        {
            SearchBackends.Json => await QueryJsonAsync(indexPathOrRunDirectory, query, top, cancellationToken).ConfigureAwait(false),
            SearchBackends.Sqlite or SearchBackends.SqliteOnnx => await QuerySqliteAsync(indexPathOrRunDirectory, query, top, options with { Backend = backend }, cancellationToken).ConfigureAwait(false),
            _ => throw new ReplayException($"Unknown search backend: {options.Backend}")
        };
    }

    private async Task<SearchQueryResult> QueryJsonAsync(string indexPathOrRunDirectory, string query, int top, CancellationToken cancellationToken)
    {
        var indexPath = Directory.Exists(indexPathOrRunDirectory)
            ? Path.Combine(indexPathOrRunDirectory, "search", "index.json")
            : indexPathOrRunDirectory;
        if (!File.Exists(indexPath))
        {
            throw new ReplayException($"Search index was not found: {indexPath}");
        }

        await using var stream = File.OpenRead(indexPath);
        var manifest = await JsonSerializer.DeserializeAsync<SearchIndexManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ReplayException("Search index is empty or invalid.");
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in manifest.Documents)
        {
            foreach (var token in document.Weights.Keys)
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var queryVector = BuildVector(Tokenize(query).ToArray(), documentFrequency, Math.Max(1, manifest.Documents.Count));
        var matches = manifest.Documents
            .Select(document =>
            {
                // Per-document RunId/SourceUrl wins (set for cross-run / conference indexes);
                // otherwise fall back to the manifest-level value (single-run index).
                var docRunId = document.RunId ?? manifest.RunId;
                var docSourceUrl = document.SourceUrl ?? manifest.SourceUrl;
                return new SearchMatch(
                    document.Id,
                    document.Kind,
                    Score(queryVector, document.Weights),
                    document.Text,
                    document.StartSeconds,
                    document.EndSeconds,
                    document.Timestamp,
                    document.Path,
                    DeepLink: DeepLink.For(docSourceUrl, document.StartSeconds ?? DeepLink.TryParseSeconds(document.Timestamp) ?? 0),
                    RunId: docRunId,
                    SourceUrl: docSourceUrl);
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.StartSeconds ?? double.MaxValue)
            .Take(Math.Max(1, top))
            .ToArray();
        return new SearchQueryResult(query, matches);
    }

    private static IEnumerable<SearchSourceDocument> BuildDocuments(EvidenceDocument evidence)
    {
        var index = 0;
        foreach (var segment in evidence.Transcript)
        {
            yield return new SearchSourceDocument($"transcript-{++index:0000}", "transcript", segment.Text, segment.StartSeconds, segment.EndSeconds, segment.Timestamp, null);
        }

        foreach (var ocr in evidence.Ocr)
        {
            var text = ocr.Structured is { Lines.Count: > 0 } structured
                ? string.Join('\n', structured.Lines)
                : ocr.Text;
            yield return new SearchSourceDocument($"ocr-{++index:0000}", "ocr", text, ocr.TimestampSeconds, null, ocr.TimestampLabel, ocr.FramePath);
        }

        foreach (var vision in evidence.Vision)
        {
            var combined = new List<string> { vision.Description };
            if (vision.Structured is { } visionStructured)
            {
                if (!string.IsNullOrWhiteSpace(visionStructured.Title))
                {
                    combined.Add(visionStructured.Title);
                }

                combined.AddRange(visionStructured.Bullets);
                combined.AddRange(visionStructured.UiElements);
                combined.AddRange(visionStructured.CodeBlocks.Select(block => block.Text));
                combined.AddRange(visionStructured.Charts.Select(chart => chart.Title ?? string.Empty).Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            yield return new SearchSourceDocument($"vision-{++index:0000}", "vision", string.Join('\n', combined.Where(line => !string.IsNullOrWhiteSpace(line))), vision.TimestampSeconds, null, vision.TimestampLabel, vision.FramePath);
        }

        foreach (var warning in evidence.Warnings)
        {
            yield return new SearchSourceDocument($"warning-{++index:0000}", "warning", $"{warning.Code}: {warning.Message}", null, null, null, null);
        }
    }

    private static Dictionary<string, double> BuildVector(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, int> documentFrequency, int documentCount)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        var vector = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (token, count) in counts)
        {
            var tf = 1 + Math.Log(count);
            var idf = Math.Log((documentCount + 1.0) / (documentFrequency.GetValueOrDefault(token) + 1.0)) + 1;
            vector[token] = tf * idf;
        }

        return Normalize(vector);
    }

    private static Dictionary<string, double> Normalize(Dictionary<string, double> vector)
    {
        var norm = Math.Sqrt(vector.Values.Sum(value => value * value));
        if (norm <= 0)
        {
            return vector;
        }

        foreach (var key in vector.Keys.ToArray())
        {
            vector[key] /= norm;
        }

        return vector;
    }

    private static double Score(IReadOnlyDictionary<string, double> query, IReadOnlyDictionary<string, double> document)
    {
        var score = 0d;
        foreach (var (token, weight) in query)
        {
            if (document.TryGetValue(token, out var documentWeight))
            {
                score += weight * documentWeight;
            }
        }

        return score;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in SearchTokenRegex().Matches(text.ToLowerInvariant()))
        {
            var token = match.Value;
            if (token.Length >= 2 && !StopWords.Contains(token))
            {
                yield return token;
            }
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"
    };

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex SearchTokenRegex();
}

public sealed record SearchIndexManifest(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAt,
    int DocumentCount,
    IReadOnlyList<SearchIndexDocument> Documents,
    // Original session URL (evidence.WebpageUrl ?? evidence.Source) captured at build time so
    // QueryAsync can compose deep links per match without re-reading evidence.json. Null when
    // the source had no recognisable URL.
    string? SourceUrl = null);

public sealed record SearchIndexDocument(
    string Id,
    string Kind,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path,
    IReadOnlyDictionary<string, double> Weights,
    // Per-document run id + source URL. Populated for cross-run / conference indexes (where
    // each document originates from a different run); null/empty for single-run indexes where
    // the manifest-level fields apply uniformly.
    string? RunId = null,
    string? SourceUrl = null);

public sealed record SearchQueryResult(string Query, IReadOnlyList<SearchMatch> Matches);

public sealed record SearchMatch(
    string Id,
    string Kind,
    double Score,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path,
    string? DeepLink = null,
    string? RunId = null,
    string? SourceUrl = null);

internal sealed record SearchSourceDocument(
    string Id,
    string Kind,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path,
    string? RunId = null,
    string? SourceUrl = null);

/// <summary>
/// Result of <see cref="SearchIndexService.BuildConferenceAsync"/>: the persisted conference
/// index plus a per-run audit trail. Agents inspect <see cref="IncludedRuns"/> to know which
/// sessions were ingested and <see cref="Skipped"/> to surface ingestion failures.
/// </summary>
public sealed record SearchIndexConferenceBuildResult(
    string ConferenceId,
    string IndexPath,
    int DocumentCount,
    IReadOnlyList<string> IncludedRuns,
    IReadOnlyList<SearchIndexSkippedRun> Skipped,
    DateTimeOffset CreatedAt);

/// <summary>
/// One run that <see cref="SearchIndexService.BuildConferenceAsync"/> could not ingest, with
/// the reason. Non-fatal — the build still succeeds for the remaining runs.
/// </summary>
public sealed record SearchIndexSkippedRun(string RunDirectory, string Reason);
