using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class ArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ArtifactStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public static string GetDefaultRootDirectory()
    {
        return Path.Combine(Environment.CurrentDirectory, "runs");
    }

    public VideoRun CreateRun(string source, string? requestedRunId = null)
    {
        var runId = string.IsNullOrWhiteSpace(requestedRunId)
            ? $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Slug.Create(source, 48)}"
            : Slug.Create(requestedRunId, 80);

        var directory = Path.Combine(RootDirectory, runId);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "captions"));
        Directory.CreateDirectory(Path.Combine(directory, "audio"));
        Directory.CreateDirectory(Path.Combine(directory, "media"));
        Directory.CreateDirectory(Path.Combine(directory, "frames"));
        Directory.CreateDirectory(Path.Combine(directory, "ocr"));
        Directory.CreateDirectory(Path.Combine(directory, "vision"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));

        return new VideoRun(runId, directory);
    }

    public bool TryGetExistingRun(string requestedRunId, out VideoRun run)
    {
        var runId = Slug.Create(requestedRunId, 80);
        var directory = Path.Combine(RootDirectory, runId);
        run = new VideoRun(runId, directory);
        return File.Exists(run.GetPath("manifest.json"));
    }

    public bool TryGetCachedRun(string cacheKey, out VideoRun run)
    {
        run = new VideoRun(string.Empty, string.Empty);
        var entryPath = GetCacheEntryPath(cacheKey);
        if (!File.Exists(entryPath))
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<ArtifactCacheEntry>(File.ReadAllText(entryPath), JsonOptions);
            if (entry is null || string.IsNullOrWhiteSpace(entry.RunId))
            {
                return false;
            }

            var directory = Path.Combine(RootDirectory, entry.RunId);
            var candidate = new VideoRun(entry.RunId, directory);
            if (!File.Exists(candidate.GetPath("manifest.json")))
            {
                return false;
            }

            run = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task WriteCacheEntryAsync(string cacheKey, VideoRun run, CancellationToken cancellationToken)
    {
        var entry = new ArtifactCacheEntry("0.1", cacheKey, run.Id, DateTimeOffset.UtcNow);
        var path = GetCacheEntryPath(cacheKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ReadJsonAsync<T>(VideoRun run, string relativePath, CancellationToken cancellationToken)
    {
        var path = run.GetPath(relativePath);
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteJsonAsync<T>(VideoRun run, string relativePath, T value, CancellationToken cancellationToken)
    {
        var path = run.GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteTextAsync(VideoRun run, string relativePath, string value, CancellationToken cancellationToken)
    {
        var path = run.GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    private string GetCacheEntryPath(string cacheKey) => Path.Combine(RootDirectory, ".cache", cacheKey + ".json");
}

public sealed record VideoRun(string Id, string Directory)
{
    public string GetPath(string relativePath) => Path.Combine(Directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
}

public static class Slug
{
    public static string Create(string value, int maxLength = 80)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c =>
            char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();

        var raw = new string(chars);
        while (raw.Contains("--", StringComparison.Ordinal))
        {
            raw = raw.Replace("--", "-", StringComparison.Ordinal);
        }

        raw = raw.Trim('-');
        if (raw.Length == 0)
        {
            raw = "video";
        }

        return raw.Length <= maxLength ? raw : raw[..maxLength].Trim('-');
    }
}

public sealed record ArtifactManifest(
    string SchemaVersion,
    string Source,
    string Instruction,
    DateTimeOffset CreatedAt,
    string RunId,
    string? Title,
    string? WebpageUrl,
    string? Duration,
    string? AudioPath,
    string? TranscriptPath,
    string? OcrPath,
    string? VisionPath,
    string? SummaryPath,
    string? EvidencePath,
    IReadOnlyList<FrameArtifact> Frames,
    IReadOnlyList<string> Warnings);

public sealed record FrameArtifact(
    string Path,
    double TimestampSeconds,
    string TimestampLabel);

public sealed record EvidenceDocument(
    string SchemaVersion,
    string Source,
    string Instruction,
    string RunId,
    string? Title,
    string? WebpageUrl,
    double? DurationSeconds,
    string? AudioPath,
    IReadOnlyList<TranscriptSegment> Transcript,
    IReadOnlyList<FrameArtifact> Frames,
    IReadOnlyList<OcrFrameResult> Ocr,
    IReadOnlyList<VisionFrameResult> Vision,
    string? Summary,
    IReadOnlyList<string> Warnings);

public sealed record TranscriptSegment(
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string Text);

public sealed record OcrFrameResult(
    string FramePath,
    double TimestampSeconds,
    string TimestampLabel,
    string Text);

public sealed record VisionFrameResult(
    string FramePath,
    double TimestampSeconds,
    string TimestampLabel,
    string Description);
