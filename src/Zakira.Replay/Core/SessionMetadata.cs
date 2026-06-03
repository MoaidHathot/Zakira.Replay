using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zakira.Replay.Core;

/// <summary>
/// Deterministic, source-page-derived metadata about a session (conference talk, training
/// recording, podcast episode, etc.). Populated by <see cref="SessionMetadataExtractor"/> when
/// the browser-capture path is in use; agents consume it for grounded, no-hallucination
/// summarisation and search.
/// </summary>
/// <remarks>
/// All fields are nullable / empty-by-default — the extractor never fabricates values. When a
/// field is present, <see cref="Sources"/> records which strategy supplied it (so agents can
/// audit provenance and tooling can prefer higher-fidelity sources next time).
/// </remarks>
public sealed record SessionMetadata(
    string? Title,
    string? Description,
    string? SessionCode,
    string? Track,
    string? Level,
    string? PublishedAt,
    IReadOnlyList<string>? Speakers,
    IReadOnlyList<string>? Products,
    IReadOnlyList<string>? Tags,
    string? SourceUrl,
    IReadOnlyList<SessionMetadataSource>? Sources);

/// <summary>
/// Provenance for an extracted field set — names the strategy that produced it (e.g.
/// <c>"json-ld"</c>, <c>"opengraph"</c>, <c>"html-title"</c>) so downstream tools can prefer
/// higher-fidelity sources or audit the merge.
/// </summary>
public sealed record SessionMetadataSource(string Strategy, IReadOnlyList<string> Fields);

/// <summary>
/// Pure HTML-parser for session metadata. Each strategy is a small, side-effect-free function
/// that takes the page HTML (+ source URL for provenance) and returns whatever fields it could
/// extract; the orchestrator merges them with first-non-null wins.
/// </summary>
/// <remarks>
/// <b>Order matters</b>: strategies registered earlier are tried first and their non-null
/// values win for conflicting fields. JSON-LD (most structured / most trustworthy) sits at the
/// top; OpenGraph below; plain HTML title last (least structured, easiest to be wrong).
/// </remarks>
public static partial class SessionMetadataExtractor
{
    /// <summary>
    /// Run every registered strategy against <paramref name="html"/> and merge the results.
    /// Returns null when no strategy produced any field at all (so the caller can omit the
    /// section entirely rather than emit an empty object).
    /// </summary>
    public static SessionMetadata? Extract(string html, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var partials = new List<(string Strategy, SessionMetadata Partial)>();
        foreach (var (name, fn) in Strategies)
        {
            try
            {
                var partial = fn(html, sourceUrl);
                if (partial is not null && !IsEmpty(partial))
                {
                    partials.Add((name, partial));
                }
            }
            catch
            {
                // Strategies are best-effort: malformed HTML / unexpected JSON shapes must
                // never fail the run. Failed strategies are silently skipped.
            }
        }

        return partials.Count == 0 ? null : Merge(partials, sourceUrl);
    }

    /// <summary>
    /// Strategy registry. Append a new (name, function) tuple to add a parser; the merge logic
    /// honours order automatically.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, Func<string, string?, SessionMetadata?> Fn)> Strategies =
    [
        ("json-ld", JsonLdStrategy),
        ("opengraph", OpenGraphStrategy),
        ("html-title", HtmlTitleStrategy),
    ];

    // ---- JSON-LD ---------------------------------------------------------------------------

    /// <summary>
    /// Parse every <c>&lt;script type="application/ld+json"&gt;</c> block and merge any
    /// VideoObject / LearningResource / CreativeWork / Event entries. Handles single objects,
    /// arrays, and <c>@graph</c> wrappers — the three shapes conferences actually use.
    /// </summary>
    internal static SessionMetadata? JsonLdStrategy(string html, string? sourceUrl)
    {
        var title = (string?)null;
        var description = (string?)null;
        var sessionCode = (string?)null;
        var track = (string?)null;
        var level = (string?)null;
        var publishedAt = (string?)null;
        var speakers = new List<string>();
        var products = new List<string>();
        var tags = new List<string>();

        foreach (Match m in JsonLdBlockRegex().Matches(html))
        {
            var raw = m.Groups[1].Value.Trim();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); } catch (JsonException) { continue; }
            using (doc)
            {
                foreach (var node in EnumerateJsonLdNodes(doc.RootElement))
                {
                    var nodeType = GetStringOrFirst(node, "@type");
                    if (nodeType is null) continue;
                    if (!LooksLikeContentNode(nodeType)) continue;

                    title ??= TryGetString(node, "name") ?? TryGetString(node, "headline");
                    description ??= TryGetString(node, "description") ?? TryGetString(node, "abstract");
                    publishedAt ??= TryGetString(node, "datePublished") ?? TryGetString(node, "uploadDate") ?? TryGetString(node, "startDate");
                    sessionCode ??= TryGetString(node, "identifier") ?? TryGetString(node, "sku");
                    track ??= TryGetString(node, "genre") ?? TryGetString(node, "about");
                    level ??= TryGetString(node, "educationalLevel") ?? TryGetString(node, "audience");

                    foreach (var person in EnumerateAsObjects(node, "author", "creator", "contributor", "performer", "speaker"))
                    {
                        var name = TryGetString(person, "name")
                            ?? (person.ValueKind == JsonValueKind.String ? person.GetString() : null);
                        if (!string.IsNullOrWhiteSpace(name) && !speakers.Contains(name!, StringComparer.OrdinalIgnoreCase))
                        {
                            speakers.Add(name!);
                        }
                    }

                    foreach (var keyword in EnumerateAsStrings(node, "keywords"))
                    {
                        var split = keyword.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var t in split)
                        {
                            if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
                        }
                    }

                    foreach (var product in EnumerateAsStrings(node, "isPartOf", "subjectOf"))
                    {
                        if (!products.Contains(product, StringComparer.OrdinalIgnoreCase)) products.Add(product);
                    }
                }
            }
        }

        if (title is null && description is null && speakers.Count == 0 && tags.Count == 0 && products.Count == 0 && sessionCode is null)
        {
            return null;
        }

        return new SessionMetadata(
            Title: title,
            Description: description,
            SessionCode: sessionCode,
            Track: track,
            Level: level,
            PublishedAt: publishedAt,
            Speakers: speakers.Count == 0 ? null : speakers,
            Products: products.Count == 0 ? null : products,
            Tags: tags.Count == 0 ? null : tags,
            SourceUrl: sourceUrl,
            Sources: null);
    }

    // ---- OpenGraph -------------------------------------------------------------------------

    /// <summary>
    /// Parse <c>og:*</c> / <c>twitter:*</c> meta tags. Coarser than JSON-LD but universally
    /// present on conference pages; fills holes the JSON-LD strategy left.
    /// </summary>
    internal static SessionMetadata? OpenGraphStrategy(string html, string? sourceUrl)
    {
        var props = ParseMetaProperties(html);
        if (props.Count == 0) return null;

        string? P(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (props.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        var title = P("og:title", "twitter:title");
        var description = P("og:description", "twitter:description", "description");
        var publishedAt = P("article:published_time", "og:article:published_time");
        var tagsRaw = P("article:tag", "keywords");
        var tags = tagsRaw is null
            ? null
            : tagsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (title is null && description is null && publishedAt is null && (tags is null || tags.Length == 0))
        {
            return null;
        }

        return new SessionMetadata(
            Title: title,
            Description: description,
            SessionCode: null,
            Track: null,
            Level: null,
            PublishedAt: publishedAt,
            Speakers: null,
            Products: null,
            Tags: tags,
            SourceUrl: sourceUrl,
            Sources: null);
    }

    // ---- HTML title + description ----------------------------------------------------------

    /// <summary>
    /// Last-resort plain-HTML parse: <c>&lt;title&gt;</c> and <c>&lt;meta name="description"&gt;</c>.
    /// Fills holes the structured strategies left.
    /// </summary>
    internal static SessionMetadata? HtmlTitleStrategy(string html, string? sourceUrl)
    {
        var titleMatch = TitleRegex().Match(html);
        var title = titleMatch.Success ? DecodeEntities(titleMatch.Groups[1].Value.Trim()) : null;

        var props = ParseMetaProperties(html);
        var description = props.TryGetValue("description", out var d) ? d : null;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description)) return null;

        return new SessionMetadata(
            Title: string.IsNullOrWhiteSpace(title) ? null : title,
            Description: description,
            SessionCode: null,
            Track: null,
            Level: null,
            PublishedAt: null,
            Speakers: null,
            Products: null,
            Tags: null,
            SourceUrl: sourceUrl,
            Sources: null);
    }

    // ---- Merge -----------------------------------------------------------------------------

    /// <summary>
    /// First-non-null wins per scalar field; list fields are unioned with case-insensitive
    /// dedup. <see cref="SessionMetadata.Sources"/> records every strategy that contributed
    /// (with the field list) so agents can audit which signal each value came from.
    /// </summary>
    private static SessionMetadata Merge(IReadOnlyList<(string Strategy, SessionMetadata Partial)> partials, string? sourceUrl)
    {
        string? title = null, description = null, sessionCode = null, track = null, level = null, publishedAt = null;
        List<string>? speakers = null, products = null, tags = null;
        var sources = new List<SessionMetadataSource>();

        foreach (var (name, p) in partials)
        {
            var fields = new List<string>();
            if (title is null && p.Title is not null) { title = p.Title; fields.Add("title"); }
            if (description is null && p.Description is not null) { description = p.Description; fields.Add("description"); }
            if (sessionCode is null && p.SessionCode is not null) { sessionCode = p.SessionCode; fields.Add("sessionCode"); }
            if (track is null && p.Track is not null) { track = p.Track; fields.Add("track"); }
            if (level is null && p.Level is not null) { level = p.Level; fields.Add("level"); }
            if (publishedAt is null && p.PublishedAt is not null) { publishedAt = p.PublishedAt; fields.Add("publishedAt"); }
            if (p.Speakers is { Count: > 0 })
            {
                speakers ??= [];
                var before = speakers.Count;
                foreach (var s in p.Speakers)
                {
                    if (!speakers.Contains(s, StringComparer.OrdinalIgnoreCase)) speakers.Add(s);
                }
                if (speakers.Count > before) fields.Add("speakers");
            }
            if (p.Products is { Count: > 0 })
            {
                products ??= [];
                var before = products.Count;
                foreach (var s in p.Products)
                {
                    if (!products.Contains(s, StringComparer.OrdinalIgnoreCase)) products.Add(s);
                }
                if (products.Count > before) fields.Add("products");
            }
            if (p.Tags is { Count: > 0 })
            {
                tags ??= [];
                var before = tags.Count;
                foreach (var s in p.Tags)
                {
                    if (!tags.Contains(s, StringComparer.OrdinalIgnoreCase)) tags.Add(s);
                }
                if (tags.Count > before) fields.Add("tags");
            }

            if (fields.Count > 0) sources.Add(new SessionMetadataSource(name, fields));
        }

        return new SessionMetadata(
            Title: title,
            Description: description,
            SessionCode: sessionCode,
            Track: track,
            Level: level,
            PublishedAt: publishedAt,
            Speakers: speakers,
            Products: products,
            Tags: tags,
            SourceUrl: sourceUrl,
            Sources: sources);
    }

    private static bool IsEmpty(SessionMetadata m)
        => m.Title is null && m.Description is null && m.SessionCode is null && m.Track is null
           && m.Level is null && m.PublishedAt is null
           && (m.Speakers is null || m.Speakers.Count == 0)
           && (m.Products is null || m.Products.Count == 0)
           && (m.Tags is null || m.Tags.Count == 0);

    // ---- JSON-LD helpers -------------------------------------------------------------------

    private static IEnumerable<JsonElement> EnumerateJsonLdNodes(JsonElement root)
    {
        // JSON-LD payloads come as: a single object, an array of objects, or an object with
        // an "@graph" array. Flatten all three shapes uniformly.
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var inner in EnumerateJsonLdNodes(item)) yield return inner;
            }
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object) yield break;

        if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in graph.EnumerateArray()) yield return item;
            yield break;
        }
        yield return root;
    }

    private static bool LooksLikeContentNode(string type)
    {
        // The schema.org types we trust to describe a video session. Substring match catches
        // both bare ("VideoObject") and namespaced ("http://schema.org/VideoObject") forms.
        var t = type.ToLowerInvariant();
        return t.Contains("videoobject")
            || t.Contains("learningresource")
            || t.Contains("creativework")
            || t.Contains("educationevent")
            || t.Contains("course")
            || t.Contains("article");
    }

    private static string? GetStringOrFirst(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array => v.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
                ? v.EnumerateArray().First().GetString()
                : null,
            _ => null,
        };
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static IEnumerable<JsonElement> EnumerateAsObjects(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Object) yield return v;
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray()) yield return item;
            }
        }
    }

    private static IEnumerable<string> EnumerateAsStrings(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) yield return s!;
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                    }
                }
            }
        }
    }

    // ---- HTML parsing helpers --------------------------------------------------------------

    /// <summary>
    /// Index every <c>&lt;meta name="..." content="..."&gt;</c> and <c>&lt;meta property="..." content="..."&gt;</c>
    /// by lowercased key. Handles both attribute orders. Last value wins on duplicates.
    /// </summary>
    private static Dictionary<string, string> ParseMetaProperties(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MetaTagRegex().Matches(html))
        {
            var tag = m.Value;
            // Group 1 is the quote char captured by the back-reference; Group 2 is the actual
            // value. Reading Group 1 here was the bug — it returned the quote, not the value.
            var name = AttrRegex("name").Match(tag).Groups[2].Value;
            if (string.IsNullOrEmpty(name)) name = AttrRegex("property").Match(tag).Groups[2].Value;
            var content = AttrRegex("content").Match(tag).Groups[2].Value;
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(content))
            {
                result[name.ToLowerInvariant()] = DecodeEntities(content);
            }
        }
        return result;
    }

    private static Regex AttrRegex(string attr) =>
        new($"\\b{attr}\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string DecodeEntities(string input)
        => System.Net.WebUtility.HtmlDecode(input);

    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex JsonLdBlockRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TitleRegex();
}
