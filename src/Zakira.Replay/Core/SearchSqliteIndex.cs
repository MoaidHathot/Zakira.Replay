using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zakira.Replay.Core;

public sealed partial class SearchIndexService
{
    private const string SqliteIndexFileName = "index.sqlite";

    private async Task<SearchIndexBuildResult> BuildSqliteAsync(string runDirectory, SearchIndexBuildOptions options, CancellationToken cancellationToken)
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
        var provider = ResolveEmbeddingProvider(options, require: options.Backend == SearchBackends.SqliteOnnx);

        var indexDirectory = Path.Combine(runDirectory, "search");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, SqliteIndexFileName);
        if (File.Exists(indexPath))
        {
            File.Delete(indexPath);
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = indexPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await CreateSqliteSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var createdAt = DateTimeOffset.UtcNow;
        await InsertMetadataAsync(connection, transaction, "schemaVersion", "0.2", cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, transaction, "backend", options.Backend, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, transaction, "runId", evidence.RunId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, transaction, "createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, transaction, "documentCount", documents.Length.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, transaction, "embeddingProvider", provider?.Name ?? "none", cancellationToken).ConfigureAwait(false);
        // Persist the original session URL (WebpageUrl or Source) so QueryAsync can compose
        // deep links per match without re-reading evidence.json. Skipped when neither is set.
        var sourceUrl = string.IsNullOrWhiteSpace(evidence.WebpageUrl) ? evidence.Source : evidence.WebpageUrl;
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            await InsertMetadataAsync(connection, transaction, "sourceUrl", sourceUrl, cancellationToken).ConfigureAwait(false);
        }
        // 0.10.0: persist the embedding model identity so a later query against the same
        // index can detect cross-model vector incompatibility (vectors trained against
        // bge-small are not interchangeable with vectors trained against multilingual-e5
        // even when both produce 384-dim outputs).
        if (provider is not null)
        {
            await InsertMetadataAsync(connection, transaction, "embeddingModel", provider.ModelId, cancellationToken).ConfigureAwait(false);
            await InsertMetadataAsync(connection, transaction, "embeddingModelKind", provider.ModelKind, cancellationToken).ConfigureAwait(false);
            await InsertMetadataAsync(connection, transaction, "embeddingDimensions", provider.Dimensions.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }

        foreach (var document in documents)
        {
            var rowId = await InsertDocumentAsync(connection, transaction, document, cancellationToken).ConfigureAwait(false);
            if (provider is not null)
            {
                var vector = await provider.EmbedAsync(document.Text, SearchEmbeddingSide.Document, cancellationToken).ConfigureAwait(false);
                await InsertEmbeddingAsync(connection, transaction, rowId, vector, cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Commit();
        return new SearchIndexBuildResult(
            options.Backend,
            evidence.RunId,
            documents.Length,
            indexPath,
            createdAt,
            Manifest: null,
            EmbeddingModel: provider?.ModelId,
            EmbeddingModelKind: provider?.ModelKind,
            EmbeddingDimensions: provider?.Dimensions);
    }

    private async Task<SearchQueryResult> QuerySqliteAsync(string indexPathOrRunDirectory, string query, int top, SearchIndexQueryOptions options, CancellationToken cancellationToken)
    {
        var indexPath = GetSqliteIndexPath(indexPathOrRunDirectory);
        if (!File.Exists(indexPath))
        {
            throw new ReplayException($"Search index was not found: {indexPath}");
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = indexPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var limit = Math.Max(1, top);
        var lexicalScores = await QuerySqliteFtsAsync(connection, query, Math.Max(limit * 5, 25), cancellationToken).ConfigureAwait(false);
        var vectorScores = await QuerySqliteEmbeddingsAsync(connection, query, options, cancellationToken).ConfigureAwait(false);
        var rowIds = lexicalScores.Keys.Concat(vectorScores.Keys).Distinct().ToArray();
        if (rowIds.Length == 0)
        {
            return new SearchQueryResult(query, []);
        }

        var documents = await ReadSqliteDocumentsAsync(connection, rowIds, cancellationToken).ConfigureAwait(false);
        // Read source URL + run ID from the metadata table so per-match deep links can be
        // assembled. Both are nullable: a result without them still scores fine, just without
        // a deep link or cross-run attribution.
        var sourceUrl = await ReadMetadataAsync(connection, "sourceUrl", cancellationToken).ConfigureAwait(false);
        var runId = await ReadMetadataAsync(connection, "runId", cancellationToken).ConfigureAwait(false);
        var matches = documents
            .Select(document => new SearchMatch(
                document.Value.Id,
                document.Value.Kind,
                CombineScores(lexicalScores.GetValueOrDefault(document.Key), vectorScores.GetValueOrDefault(document.Key)),
                document.Value.Text,
                document.Value.StartSeconds,
                document.Value.EndSeconds,
                document.Value.Timestamp,
                document.Value.Path,
                DeepLink: DeepLink.For(sourceUrl, document.Value.StartSeconds ?? DeepLink.TryParseSeconds(document.Value.Timestamp) ?? 0),
                RunId: runId,
                SourceUrl: sourceUrl))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.StartSeconds ?? double.MaxValue)
            .Take(limit)
            .ToArray();

        return new SearchQueryResult(query, matches);
    }

    private static async Task CreateSqliteSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE chunks (
                rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                text TEXT NOT NULL,
                start_seconds REAL,
                end_seconds REAL,
                timestamp TEXT,
                path TEXT
            );
            CREATE VIRTUAL TABLE chunks_fts USING fts5(text, content='chunks', content_rowid='rowid', tokenize='unicode61');
            CREATE TABLE embeddings (
                chunk_rowid INTEGER PRIMARY KEY REFERENCES chunks(rowid) ON DELETE CASCADE,
                dimensions INTEGER NOT NULL,
                vector BLOB NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO metadata(key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> InsertDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, SearchSourceDocument document, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chunks(id, kind, text, start_seconds, end_seconds, timestamp, path)
            VALUES ($id, $kind, $text, $startSeconds, $endSeconds, $timestamp, $path);
            """;
        command.Parameters.AddWithValue("$id", document.Id);
        command.Parameters.AddWithValue("$kind", document.Kind);
        command.Parameters.AddWithValue("$text", document.Text);
        command.Parameters.AddWithValue("$startSeconds", ToDbValue(document.StartSeconds));
        command.Parameters.AddWithValue("$endSeconds", ToDbValue(document.EndSeconds));
        command.Parameters.AddWithValue("$timestamp", ToDbValue(document.Timestamp));
        command.Parameters.AddWithValue("$path", ToDbValue(document.Path));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var rowId = (long)(await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);

        using var ftsCommand = connection.CreateCommand();
        ftsCommand.Transaction = transaction;
        ftsCommand.CommandText = "INSERT INTO chunks_fts(rowid, text) VALUES ($rowid, $text);";
        ftsCommand.Parameters.AddWithValue("$rowid", rowId);
        ftsCommand.Parameters.AddWithValue("$text", document.Text);
        await ftsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowId;
    }

    private static async Task InsertEmbeddingAsync(SqliteConnection connection, SqliteTransaction transaction, long rowId, float[] vector, CancellationToken cancellationToken)
    {
        if (vector.Length == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO embeddings(chunk_rowid, dimensions, vector) VALUES ($rowid, $dimensions, $vector);";
        command.Parameters.AddWithValue("$rowid", rowId);
        command.Parameters.AddWithValue("$dimensions", vector.Length);
        command.Parameters.Add("$vector", SqliteType.Blob).Value = SerializeVector(vector);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<long, double>> QuerySqliteFtsAsync(SqliteConnection connection, string query, int limit, CancellationToken cancellationToken)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return [];
        }

        var scores = new Dictionary<long, double>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunks.rowid
            FROM chunks_fts
            JOIN chunks ON chunks.rowid = chunks_fts.rowid
            WHERE chunks_fts MATCH $query
            ORDER BY bm25(chunks_fts)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rank = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scores[reader.GetInt64(0)] = 1d / (++rank);
        }

        return scores;
    }

    private async Task<Dictionary<long, double>> QuerySqliteEmbeddingsAsync(SqliteConnection connection, string query, SearchIndexQueryOptions options, CancellationToken cancellationToken)
    {
        if (!await HasEmbeddingsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var provider = ResolveEmbeddingProvider(options, require: options.Backend == SearchBackends.SqliteOnnx);
        if (provider is null)
        {
            return [];
        }

        // 0.10.0: refuse to mix vectors across embedding models. The geometry is incomparable
        // even when dimensions match, so a query with the wrong model would return
        // plausible-looking but meaningless results. Caller fix is `index build --force`
        // (rebuild) or `--onnx-model <id>` (pin the index's model for this query).
        var indexedModel = await ReadMetadataAsync(connection, "embeddingModel", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(indexedModel)
            && !string.Equals(indexedModel, provider.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var indexedKind = await ReadMetadataAsync(connection, "embeddingModelKind", cancellationToken).ConfigureAwait(false);
            var indexedDims = await ReadMetadataAsync(connection, "embeddingDimensions", cancellationToken).ConfigureAwait(false);
            throw new SearchIndexEmbeddingMismatchException(
                indexedModel!,
                indexedKind ?? "unknown",
                provider.ModelId,
                provider.ModelKind,
                indexedDims);
        }

        var queryVector = await provider.EmbedAsync(query, SearchEmbeddingSide.Query, cancellationToken).ConfigureAwait(false);
        if (queryVector.Length == 0)
        {
            return [];
        }

        var scores = new Dictionary<long, double>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_rowid, vector FROM embeddings;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rowId = reader.GetInt64(0);
            var vector = DeserializeVector((byte[])reader[1]);
            var score = Cosine(queryVector, vector);
            if (score > 0)
            {
                scores[rowId] = score;
            }
        }

        return scores;
    }

    private static async Task<string?> ReadMetadataAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string text ? text : null;
    }

    private static async Task<bool> HasEmbeddingsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM embeddings LIMIT 1);";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<Dictionary<long, SqliteSearchDocument>> ReadSqliteDocumentsAsync(SqliteConnection connection, IReadOnlyCollection<long> rowIds, CancellationToken cancellationToken)
    {
        var documents = new Dictionary<long, SqliteSearchDocument>();
        foreach (var rowId in rowIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, text, start_seconds, end_seconds, timestamp, path
                FROM chunks
                WHERE rowid = $rowid;
                """;
            command.Parameters.AddWithValue("$rowid", rowId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documents[rowId] = new SqliteSearchDocument(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
            }
        }

        return documents;
    }

    private ISearchEmbeddingProvider? ResolveEmbeddingProvider(SearchIndexBuildOptions options, bool require)
    {
        if (embeddingProvider is not null)
        {
            return embeddingProvider;
        }

        var config = SearchEmbeddingConfig.From(options);
        if (config.HasOnnxConfiguration)
        {
            return new OnnxSearchEmbeddingProvider(
                config.ModelPath!,
                config.TokenizerPath!,
                config.MaxSequenceLength,
                config.EmbeddingDimensions,
                config.ModelKind,
                config.ModelId);
        }

        return require
            ? throw new ReplayException("The sqlite-onnx search backend requires an ONNX model path and tokenizer path. Set search.onnx.model to a known id (`bge-small-en-v1.5`, `snowflake-arctic-embed-s`, `multilingual-e5-small`) with `search.onnx.autoDownload=true`, or pass --onnx-model-path and --onnx-tokenizer-path, or set ZAKIRA_REPLAY_ONNX_MODEL_PATH and ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH explicitly.")
            : null;
    }

    private ISearchEmbeddingProvider? ResolveEmbeddingProvider(SearchIndexQueryOptions options, bool require)
    {
        if (embeddingProvider is not null)
        {
            return embeddingProvider;
        }

        var config = SearchEmbeddingConfig.From(options);
        if (config.HasOnnxConfiguration)
        {
            return new OnnxSearchEmbeddingProvider(
                config.ModelPath!,
                config.TokenizerPath!,
                config.MaxSequenceLength,
                config.EmbeddingDimensions,
                config.ModelKind,
                config.ModelId);
        }

        return require
            ? throw new ReplayException("The sqlite-onnx search backend requires an ONNX model path and tokenizer path for querying. Set search.onnx.model to a known id, or pass --onnx-model-path and --onnx-tokenizer-path, or set ZAKIRA_REPLAY_ONNX_MODEL_PATH and ZAKIRA_REPLAY_ONNX_TOKENIZER_PATH explicitly.")
            : null;
    }

    private static string DetectQueryBackend(string indexPathOrRunDirectory)
    {
        if (Directory.Exists(indexPathOrRunDirectory))
        {
            return File.Exists(Path.Combine(indexPathOrRunDirectory, "search", SqliteIndexFileName))
                ? SearchBackends.Sqlite
                : SearchBackends.Json;
        }

        var extension = Path.GetExtension(indexPathOrRunDirectory);
        return extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) || extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            ? SearchBackends.Sqlite
            : SearchBackends.Json;
    }

    private static string GetSqliteIndexPath(string indexPathOrRunDirectory)
    {
        return Directory.Exists(indexPathOrRunDirectory)
            ? Path.Combine(indexPathOrRunDirectory, "search", SqliteIndexFileName)
            : indexPathOrRunDirectory;
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = Tokenize(query).Take(16).ToArray();
        return tokens.Length == 0 ? string.Empty : string.Join(" OR ", tokens.Select(token => token + "*"));
    }

    private static double CombineScores(double lexicalScore, double vectorScore)
    {
        if (lexicalScore <= 0)
        {
            return vectorScore;
        }

        if (vectorScore <= 0)
        {
            return lexicalScore;
        }

        return (lexicalScore * 0.35) + (vectorScore * 0.65);
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            return [];
        }

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static double Cosine(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0 ? 0 : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static object ToDbValue<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object ToDbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private sealed record SqliteSearchDocument(
        string Id,
        string Kind,
        string Text,
        double? StartSeconds,
        double? EndSeconds,
        string? Timestamp,
        string? Path);
}
