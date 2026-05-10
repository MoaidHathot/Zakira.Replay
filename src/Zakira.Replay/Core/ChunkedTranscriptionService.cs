using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Drives <see cref="ITranscriptionProvider"/> over a possibly-chunked audio input and stitches
/// the per-chunk responses into a single timestamped Markdown transcript.
/// </summary>
/// <remarks>
/// When the audio stays within the chunker's threshold, the provider is invoked once on the full
/// file and the output is returned verbatim. When chunking actually splits the audio, each chunk's
/// response is shifted by its <see cref="AudioChunk.StartSeconds"/> offset so downstream consumers
/// continue to see one timeline. Provider responses that already contain Markdown timestamp
/// headers (<c>**[hh:mm:ss]**</c> or <c>**[hh:mm:ss - hh:mm:ss]**</c>) are rewritten with shifted
/// timestamps; flat prose is wrapped with a single chunk-level header.
/// </remarks>
public sealed partial class ChunkedTranscriptionService
{
    private readonly ITranscriptionProvider transcriber;
    private readonly AudioChunker chunker;

    public ChunkedTranscriptionService(ITranscriptionProvider transcriber, AudioChunker chunker)
    {
        this.transcriber = transcriber;
        this.chunker = chunker;
    }

    public async Task<ChunkedTranscriptionResult> TranscribeAsync(string audioPath, VideoRun run, AudioChunkingOptions? options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var chunkingResult = await chunker.ChunkAsync(audioPath, run, options ?? new AudioChunkingOptions(), cancellationToken).ConfigureAwait(false);
        var chunks = chunkingResult.Chunks;
        if (chunks.Count == 0)
        {
            throw new ReplayException("Audio chunker produced no chunks.");
        }

        if (chunks.Count == 1)
        {
            progress?.Report("Transcribing audio (single chunk)...");
            var transcript = await transcriber.TranscribeAsync(audioPath, cancellationToken).ConfigureAwait(false);
            return new ChunkedTranscriptionResult(transcript, chunkingResult, ChunkedTranscriptionWarnings: []);
        }

        var builder = new StringBuilder();
        var chunkWarnings = new List<ReplayWarning>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Transcribing audio chunk {i + 1}/{chunks.Count}: {chunk.Id} ({Timestamp.Format(chunk.StartSeconds)} - {Timestamp.Format(chunk.StartSeconds + chunk.DurationSeconds)})...");
            string chunkTranscript;
            try
            {
                chunkTranscript = await transcriber.TranscribeAsync(chunk.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                chunkWarnings.Add(new ReplayWarning(
                    ReplayWarningCodes.SttChunkFailed,
                    $"Audio chunk {chunk.Id} ({Timestamp.Format(chunk.StartSeconds)} - {Timestamp.Format(chunk.StartSeconds + chunk.DurationSeconds)}) failed to transcribe: {ex.Message}",
                    Source: "stt",
                    Severity: ReplayWarningSeverities.Error));
                continue;
            }

            AppendChunkMarkdown(builder, chunk, chunkTranscript);
        }

        return new ChunkedTranscriptionResult(builder.ToString(), chunkingResult, chunkWarnings);
    }

    private static void AppendChunkMarkdown(StringBuilder builder, AudioChunk chunk, string chunkTranscript)
    {
        var trimmed = chunkTranscript?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        var hasTimestampedLines = false;
        foreach (var rawLine in trimmed.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = TimestampedLineRegex().Match(line);
            if (match.Success)
            {
                hasTimestampedLines = true;
                var shifted = ShiftTimestampRange(match.Groups[1].Value, chunk.StartSeconds);
                builder.Append("**[").Append(shifted).Append("]** ").AppendLine(match.Groups[2].Value.Trim());
            }
            else
            {
                if (!hasTimestampedLines)
                {
                    builder.Append("**[")
                        .Append(Timestamp.Format(chunk.StartSeconds))
                        .Append(" - ")
                        .Append(Timestamp.Format(chunk.StartSeconds + chunk.DurationSeconds))
                        .Append("]** ")
                        .AppendLine(line);
                    hasTimestampedLines = true;
                }
                else
                {
                    builder.AppendLine(line);
                }
            }
        }
    }

    private static string ShiftTimestampRange(string range, double offsetSeconds)
    {
        var parts = range.Split(['-', '\u2013'], 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            var shifted = ShiftSingleTimestamp(parts[0], offsetSeconds);
            return shifted ?? parts[0];
        }

        var shiftedStart = ShiftSingleTimestamp(parts[0], offsetSeconds);
        var shiftedEnd = ShiftSingleTimestamp(parts[1], offsetSeconds);
        if (shiftedStart is null || shiftedEnd is null)
        {
            return range;
        }

        return $"{shiftedStart} - {shiftedEnd}";
    }

    private static string? ShiftSingleTimestamp(string timestamp, double offsetSeconds)
    {
        var seconds = ParseTimestampSeconds(timestamp);
        if (seconds is null)
        {
            return null;
        }

        return Timestamp.Format(seconds.Value + offsetSeconds);
    }

    private static double? ParseTimestampSeconds(string timestamp)
    {
        var normalized = timestamp.Trim().Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
    }

    [GeneratedRegex("^\\*\\*\\[([^\\]]+)\\]\\*\\*\\s*(.*)$")]
    private static partial Regex TimestampedLineRegex();
}

/// <summary>
/// Aggregate result of <see cref="ChunkedTranscriptionService.TranscribeAsync"/>: stitched markdown
/// transcript plus chunk metadata and any per-chunk failures encoded as <see cref="ReplayWarning"/>s.
/// </summary>
public sealed record ChunkedTranscriptionResult(
    string MarkdownTranscript,
    AudioChunkingResult Chunks,
    IReadOnlyList<ReplayWarning> ChunkedTranscriptionWarnings);
