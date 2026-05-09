namespace Zakira.Replay.Core;

public static class SourceLocator
{
    public static bool IsHttpUrl(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryGetLocalFilePath(string source, out string path)
    {
        path = string.Empty;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return File.Exists(path);
        }

        var candidate = Path.GetFullPath(source);
        if (File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        return false;
    }

    public static void ThrowIfMissingLocalPathLikeSource(string source)
    {
        if (IsHttpUrl(source))
        {
            return;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return;
        }

        var looksLocal = Path.IsPathFullyQualified(source)
            || source.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || source.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.HasExtension(source);

        if (looksLocal && !File.Exists(Path.GetFullPath(source)))
        {
            throw new ReplayException($"Source file does not exist: {source}");
        }
    }
}
