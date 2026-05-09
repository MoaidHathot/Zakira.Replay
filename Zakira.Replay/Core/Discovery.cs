using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Zakira.Replay.Core;

public sealed partial class DiscoveryService
{
    private readonly DependencyResolver dependencies;
    private readonly ProcessRunner processRunner;
    private readonly HttpClient httpClient;

    public DiscoveryService(DependencyResolver dependencies, ProcessRunner processRunner, HttpClient? httpClient = null)
    {
        this.dependencies = dependencies;
        this.processRunner = processRunner;
        this.httpClient = httpClient ?? new HttpClient();
    }

    public async Task<DiscoveryResult> DiscoverAsync(string url, bool useBrowser, CancellationToken cancellationToken)
    {
        if (!SourceLocator.IsHttpUrl(url))
        {
            throw new ReplayException("Discovery currently requires an HTTP or HTTPS URL.");
        }

        var html = useBrowser
            ? await FetchWithEdgeAsync(url, cancellationToken).ConfigureAwait(false)
            : await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        var sources = ExtractVideoSources(url, html);
        return new DiscoveryResult(url, DateTimeOffset.UtcNow, sources);
    }

    private async Task<string> FetchWithEdgeAsync(string url, CancellationToken cancellationToken)
    {
        var edge = dependencies.RequireEdge("browser-backed discovery of dynamic pages");
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = edge,
            Headless = true,
            Args = ["--disable-gpu"]
        }).ConfigureAwait(false);

        var page = await browser.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 120_000
        }).ConfigureAwait(false);

        await page.WaitForTimeoutAsync(1_000).ConfigureAwait(false);
        return await page.ContentAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<DiscoveredVideoSource> ExtractVideoSources(string baseUrl, string html)
    {
        var found = new Dictionary<string, DiscoveredVideoSource>(StringComparer.OrdinalIgnoreCase);

        AddMatches(found, baseUrl, html, VideoTagRegex(), "video-src");
        AddMatches(found, baseUrl, html, SourceTagRegex(), "source-src");
        AddMatches(found, baseUrl, html, IframeRegex(), "iframe-src");
        AddMatches(found, baseUrl, html, OgVideoRegex(), "og-video");
        AddMatches(found, baseUrl, html, MediaUrlRegex(), "media-url");
        AddMatches(found, baseUrl, html, YouTubeUrlRegex(), "youtube-url");
        AddMatches(found, baseUrl, html, VimeoUrlRegex(), "vimeo-url");

        return found.Values.OrderBy(item => item.Source).ThenBy(item => item.Url).ToArray();
    }

    private static void AddMatches(
        Dictionary<string, DiscoveredVideoSource> found,
        string baseUrl,
        string html,
        Regex regex,
        string source)
    {
        foreach (Match match in regex.Matches(html))
        {
            var raw = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            var absolute = ToAbsoluteUrl(baseUrl, raw);
            if (absolute is null)
            {
                continue;
            }

            found.TryAdd(absolute, new DiscoveredVideoSource(absolute, source));
        }
    }

    private static string? ToAbsoluteUrl(string baseUrl, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        raw = JsonEncodedText.Decode(raw);
        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return Uri.TryCreate(new Uri(baseUrl), raw, out var relative) ? relative.ToString() : null;
    }

    [GeneratedRegex("<video[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VideoTagRegex();

    [GeneratedRegex("<source[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SourceTagRegex();

    [GeneratedRegex("<iframe[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IframeRegex();

    [GeneratedRegex("property=[\"']og:video(?::url)?[\"'][^>]+content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OgVideoRegex();

    [GeneratedRegex("https?:\\/\\/[^\"'\\s<>]+\\.(?:mp4|m3u8|webm|mov)(?:\\?[^\"'\\s<>]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MediaUrlRegex();

    [GeneratedRegex("https?:\\/\\/(?:www\\.)?(?:youtube\\.com|youtu\\.be)\\/[^\"'\\s<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex YouTubeUrlRegex();

    [GeneratedRegex("https?:\\/\\/(?:player\\.)?vimeo\\.com\\/[^\"'\\s<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VimeoUrlRegex();
}

public sealed record DiscoveryResult(string SourceUrl, DateTimeOffset DiscoveredAt, IReadOnlyList<DiscoveredVideoSource> Sources);

public sealed record DiscoveredVideoSource(string Url, string Source);

internal static class JsonEncodedText
{
    public static string Decode(string value)
    {
        return value.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
    }
}
