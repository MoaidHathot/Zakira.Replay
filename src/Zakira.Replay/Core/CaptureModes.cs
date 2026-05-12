namespace Zakira.Replay.Core;

/// <summary>
/// Stable identifiers for frame-capture modes. Values are part of the public artifact contract
/// (recorded on <c>AnalyzeRequest.CaptureMode</c> and surfaced in MCP schemas); rename only with
/// a schema bump.
/// </summary>
public static class CaptureModes
{
    /// <summary>
    /// Try yt-dlp first; if it cannot resolve a direct media URL (404, unsupported site, auth
    /// wall, etc.), fall back to <see cref="Browser"/> and emit <c>CAPTURE_BROWSER_FALLBACK</c>.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Resolve a direct media URL with yt-dlp and extract frames with ffmpeg. This is the
    /// historical default and works for ~1000 sites yt-dlp supports plus local media files.
    /// </summary>
    public const string YtDlp = "ytdlp";

    /// <summary>
    /// Drive a Playwright-controlled Chromium (pinned to Edge via <c>edge.path</c>) to navigate
    /// the page, click play, poll <c>video.duration</c>, seek with <c>video.currentTime</c>, and
    /// screenshot the &lt;video&gt; element at the chosen timestamps. Use for sites yt-dlp can't
    /// reach (custom enterprise portals, Medius/Teams recordings, dynamic players).
    /// </summary>
    public const string Browser = "browser";

    /// <summary>
    /// Normalises a user-supplied capture-mode string. Unknown values are returned verbatim so
    /// the pipeline can emit <c>CAPTURE_UNKNOWN_MODE</c>.
    /// </summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return YtDlp;
        }

        return mode.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "" or "default" => YtDlp,
            "auto" => Auto,
            "ytdlp" or "yt-dlp" or "ffmpeg" => YtDlp,
            "browser" or "playwright" or "chromium" or "edge" or "page" => Browser,
            var value => value
        };
    }

    public static bool IsKnown(string mode) => mode is Auto or YtDlp or Browser;
}
