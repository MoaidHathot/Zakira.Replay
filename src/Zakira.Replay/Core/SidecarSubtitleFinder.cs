namespace Zakira.Replay.Core;

public static class SidecarSubtitleFinder
{
    private static readonly string[] SubtitleExtensions = [".vtt", ".srt"];

    public static async Task<TranscriptArtifact?> TryConvertAsync(string mediaPath, VideoRun run, CancellationToken cancellationToken)
    {
        var sidecar = Find(mediaPath);
        if (sidecar is null)
        {
            return null;
        }

        var captionsDirectory = run.GetPath("captions");
        Directory.CreateDirectory(captionsDirectory);

        var targetCaptionPath = Path.Combine(captionsDirectory, Path.GetFileName(sidecar));
        File.Copy(sidecar, targetCaptionPath, overwrite: true);

        var segments = await SubtitleConverter.ParseSegmentsAsync(targetCaptionPath, cancellationToken).ConfigureAwait(false);
        var markdownPath = run.GetPath("transcript.md");
        await File.WriteAllTextAsync(markdownPath, SubtitleConverter.ToMarkdown(segments), cancellationToken).ConfigureAwait(false);

        return new TranscriptArtifact(targetCaptionPath, markdownPath, "sidecar-subtitle", segments);
    }

    private static string? Find(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(mediaPath);
        foreach (var extension in SubtitleExtensions)
        {
            var exact = Path.Combine(directory, fileNameWithoutExtension + extension);
            if (File.Exists(exact))
            {
                return exact;
            }
        }

        return Directory.EnumerateFiles(directory, fileNameWithoutExtension + ".*", SearchOption.TopDirectoryOnly)
            .Where(path => SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }
}
