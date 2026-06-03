using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public sealed class ArtifactStore
{
    /// <summary>Environment variable that pins the runs output directory, overriding both
    /// the user config and the legacy <c>&lt;cwd&gt;/runs</c> default.</summary>
    public const string RunsDirectoryEnvironmentVariable = "ZAKIRA_REPLAY_RUNS_DIRECTORY";

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

    /// <summary>
    /// Resolves the runs output directory in the documented precedence order:
    /// <c>$ZAKIRA_REPLAY_RUNS_DIRECTORY</c> → <c>config.Runs.Directory</c> →
    /// <c>&lt;cwd&gt;/runs</c>. Environment variables inside the config value are expanded
    /// here so the stored config can stay portable across machines.
    /// </summary>
    public static string ResolveRootDirectory(ReplayConfig? config)
    {
        var envValue = Environment.GetEnvironmentVariable(RunsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(envValue.Trim().Trim('"')));
        }

        var configured = config?.Runs?.Directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
        }

        return Path.Combine(Environment.CurrentDirectory, "runs");
    }

    public static string GetDefaultRootDirectory()
    {
        return ResolveRootDirectory(config: null);
    }

    public VideoRun CreateRun(string source, string? requestedRunId = null)
    {
        var runId = string.IsNullOrWhiteSpace(requestedRunId)
            ? CreateDeterministicRunId(source)
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

    /// <summary>
    /// Builds a deterministic run identifier from the source URL or local path. The result is a
    /// slug of the source plus a short SHA-256 suffix so two sources that happen to slugify the
    /// same (e.g. case-only differences in URL paths) still land in distinct run directories
    /// while same-source reruns reuse the same folder for caching.
    /// </summary>
    public static string CreateDeterministicRunId(string source)
    {
        var trimmed = source?.Trim() ?? string.Empty;
        var slug = Slug.Create(trimmed, 60);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        var hashHex = Convert.ToHexString(hashBytes, 0, 4).ToLowerInvariant();
        return slug.Length == 0 ? hashHex : $"{slug}-{hashHex}";
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
        await WriteAllTextAtomicAsync(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
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
        // Atomic write: serialize to a sibling .tmp file then rename. Guarantees that
        // a Ctrl-C / crash mid-serialization never leaves a corrupt JSON behind for
        // downstream agents to read. Same-volume rename is atomic on every supported
        // filesystem (NTFS, ext4, APFS).
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task WriteTextAsync(VideoRun run, string relativePath, string value, CancellationToken cancellationToken)
    {
        var path = run.GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAllTextAtomicAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically by first
    /// writing to a sibling .tmp file and renaming it over the destination. Same-volume
    /// rename is atomic on every filesystem we support, so partial writes never become
    /// visible to readers (downstream agents) even after a Ctrl-C or process crash.
    /// </summary>
    private static async Task WriteAllTextAtomicAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, contents, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
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
    string VisionInstruction,
    string OcrInstruction,
    DateTimeOffset CreatedAt,
    string RunId,
    string? Title,
    string? WebpageUrl,
    string? Duration,
    string? AudioPath,
    string? TranscriptPath,
    string? OcrPath,
    string? VisionPath,
    string? EvidencePath,
    IReadOnlyList<FrameArtifact> Frames,
    IReadOnlyList<ReplayWarning> Warnings,
    RunTimingsArtifact? Timings = null,
    IReadOnlyList<SecondaryTranscriptArtifact>? SecondaryTranscripts = null,
    SessionMetadata? SessionMetadata = null);

public sealed record FrameArtifact(
    string Id,
    string Path,
    double TimestampSeconds,
    string TimestampLabel,
    string? PerceptualHash = null,
    int? Width = null,
    int? Height = null,
    FrameCropBox? Crop = null,
    string? OriginalPath = null);

/// <summary>
/// Rectangular crop applied to a frame during preprocessing (e.g. smart-crop of meeting-platform
/// UI chrome). Coordinates are pixels in the ORIGINAL (pre-crop) frame; <see cref="Width"/> and
/// <see cref="Height"/> are the dimensions of the resulting cropped image. <see cref="Source"/>
/// records the algorithm/profile that produced the crop (e.g. <c>smart-crop-auto</c>) so
/// orchestrators can audit which heuristic decided what.
/// </summary>
public sealed record FrameCropBox(int X, int Y, int Width, int Height, string Source);

public sealed record EvidenceDocument(
    string SchemaVersion,
    string Source,
    string VisionInstruction,
    string OcrInstruction,
    string RunId,
    string? Title,
    string? WebpageUrl,
    double? DurationSeconds,
    string? AudioPath,
    IReadOnlyList<TranscriptSegment> Transcript,
    IReadOnlyList<FrameArtifact> Frames,
    IReadOnlyList<SlideArtifact> Slides,
    IReadOnlyList<OcrFrameResult> Ocr,
    IReadOnlyList<VisionFrameResult> Vision,
    IReadOnlyList<SpeakerSummary> Speakers,
    IReadOnlyList<ReplayWarning> Warnings);

public sealed record TranscriptSegment(
    double? StartSeconds,
    double? EndSeconds,
    string? Timestamp,
    string Text,
    string? Id = null,
    string? SpeakerId = null,
    string? SpeakerDisplayName = null);

public sealed record SpeakerSummary(
    string Id,
    string? DisplayName,
    int SegmentCount,
    double TotalSeconds,
    double? FirstSeenSeconds,
    double? LastSeenSeconds);

/// <summary>
/// A run of perceptually-similar frames treated as a single visible "slide" or scene. Each slide
/// is identified by <see cref="Id"/> (e.g. <c>"slide-001"</c>) and carries the first/last
/// timestamps it was visible, the frames that map to it, and the primary frame chosen for OCR
/// and vision analysis. Slides are facts derived from perceptual hashing; they are emitted even
/// when grouping reduces to one frame per slide so the contract stays uniform.
/// </summary>
public sealed record SlideArtifact(
    string Id,
    double FirstSeenSeconds,
    double LastSeenSeconds,
    string FirstSeenLabel,
    string LastSeenLabel,
    string PrimaryFrameId,
    IReadOnlyList<string> FrameIds,
    string? PerceptualHash);

public sealed record OcrFrameResult(
    string FrameId,
    string FramePath,
    double TimestampSeconds,
    string TimestampLabel,
    string Text,
    string? SlideId = null,
    OcrFrameStructured? Structured = null,
    string? Provider = null);

/// <summary>
/// Structured OCR result extracted from a frame. <see cref="FreeText"/> always carries the full
/// raw response (or a tolerant-parsed fallback when the model returned non-JSON). Structured
/// fields are populated when the model output successfully parsed against the documented OCR
/// JSON shape.
/// </summary>
public sealed record OcrFrameStructured(
    string FreeText,
    IReadOnlyList<string> Lines,
    IReadOnlyList<OcrTable> Tables);

public sealed record OcrTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record VisionFrameResult(
    string FrameId,
    string FramePath,
    double TimestampSeconds,
    string TimestampLabel,
    string Description,
    string? SlideId = null,
    VisionFrameStructured? Structured = null,
    string? Provider = null);

/// <summary>
/// Structured vision analysis of a frame. <see cref="FreeText"/> always carries the full raw
/// response (or a tolerant-parsed fallback). <see cref="Kind"/> is one of <c>slide</c>,
/// <c>ui</c>, <c>code</c>, <c>diagram</c>, <c>chart</c>, <c>dashboard</c>, or <c>other</c>.
/// </summary>
public sealed record VisionFrameStructured(
    string Kind,
    string? Title,
    IReadOnlyList<string> Bullets,
    IReadOnlyList<VisionCodeBlock> CodeBlocks,
    IReadOnlyList<VisionChart> Charts,
    IReadOnlyList<string> UiElements,
    string FreeText);

public sealed record VisionCodeBlock(string? Language, string Text);

public sealed record VisionChart(
    string? Title,
    IReadOnlyList<string> Axes,
    IReadOnlyList<string> Series);
