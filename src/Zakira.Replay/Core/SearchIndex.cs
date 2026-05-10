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
                weights));
        }

        var manifest = new SearchIndexManifest("0.1", evidence.RunId, DateTimeOffset.UtcNow, indexDocuments.Count, indexDocuments);
        var indexDirectory = Path.Combine(runDirectory, "search");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, "index.json");
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return new SearchIndexBuildResult(SearchBackends.Json, evidence.RunId, indexDocuments.Count, indexPath, manifest.CreatedAt, manifest);
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
            .Select(document => new SearchMatch(document.Id, document.Kind, Score(queryVector, document.Weights), document.Text, document.StartSeconds, document.EndSeconds, document.Timestamp, document.Path))
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
    IReadOnlyList<SearchIndexDocument> Documents);

public sealed record SearchIndexDocument(
    string Id,
    string Kind,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path,
    IReadOnlyDictionary<string, double> Weights);

public sealed record SearchQueryResult(string Query, IReadOnlyList<SearchMatch> Matches);

public sealed record SearchMatch(
    string Id,
    string Kind,
    double Score,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path);

internal sealed record SearchSourceDocument(
    string Id,
    string Kind,
    string Text,
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string? Path);
