using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SessionMetadataExtractorTests
{
    [Fact]
    public void ExtractsVideoObjectJsonLd()
    {
        var html = """
        <html><head>
          <script type="application/ld+json">
          {
            "@context": "https://schema.org",
            "@type": "VideoObject",
            "name": "Building agents with Foundry",
            "description": "A deep dive into the Foundry agent runtime.",
            "datePublished": "2026-06-01T15:30:00Z",
            "identifier": "BRK101",
            "author": [{"@type": "Person", "name": "Mustafa Suleyman"}, {"@type": "Person", "name": "Asha Sharma"}],
            "keywords": "Foundry, agents, MCP"
          }
          </script>
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, "https://build.microsoft.com/en-US/sessions/BRK101");

        Assert.NotNull(m);
        Assert.Equal("Building agents with Foundry", m!.Title);
        Assert.Equal("A deep dive into the Foundry agent runtime.", m.Description);
        Assert.Equal("BRK101", m.SessionCode);
        Assert.Equal("2026-06-01T15:30:00Z", m.PublishedAt);
        Assert.Equal(new[] { "Mustafa Suleyman", "Asha Sharma" }, m.Speakers);
        Assert.Equal(new[] { "Foundry", "agents", "MCP" }, m.Tags);
        Assert.Equal("https://build.microsoft.com/en-US/sessions/BRK101", m.SourceUrl);
        Assert.NotNull(m.Sources);
        Assert.Contains(m.Sources!, s => s.Strategy == "json-ld");
    }

    [Fact]
    public void HandlesAtGraphWrappedJsonLd()
    {
        // The @graph wrapper is one of three valid JSON-LD shapes (object, array, @graph). Most
        // CMSs emit @graph when multiple node types co-exist on one page.
        var html = """
        <html><head>
          <script type="application/ld+json">
          {
            "@context": "https://schema.org",
            "@graph": [
              { "@type": "BreadcrumbList", "itemListElement": [] },
              { "@type": "VideoObject", "name": "Keynote", "description": "Annual keynote" }
            ]
          }
          </script>
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("Keynote", m!.Title);
        Assert.Equal("Annual keynote", m.Description);
    }

    [Fact]
    public void FallsBackToOpenGraphWhenJsonLdAbsent()
    {
        var html = """
        <html><head>
          <meta property="og:title" content="OpenGraph Title">
          <meta property="og:description" content="OpenGraph Description">
          <meta property="article:published_time" content="2026-05-01T00:00:00Z">
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("OpenGraph Title", m!.Title);
        Assert.Equal("OpenGraph Description", m.Description);
        Assert.Equal("2026-05-01T00:00:00Z", m.PublishedAt);
        Assert.Contains(m.Sources!, s => s.Strategy == "opengraph");
    }

    [Fact]
    public void FallsBackToHtmlTitleWhenStructuredDataAbsent()
    {
        var html = """
        <html><head>
          <title>Plain HTML Title</title>
          <meta name="description" content="A short description.">
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("Plain HTML Title", m!.Title);
        Assert.Equal("A short description.", m.Description);
        Assert.Contains(m.Sources!, s => s.Strategy == "html-title");
    }

    [Fact]
    public void MergesAcrossStrategiesFirstNonNullWins()
    {
        // JSON-LD has title only; OpenGraph fills in description. Final result must include
        // BOTH, sourced from BOTH strategies.
        var html = """
        <html><head>
          <script type="application/ld+json">
          { "@type": "VideoObject", "name": "From JSON-LD" }
          </script>
          <meta property="og:title" content="OG Title (should lose)">
          <meta property="og:description" content="From OpenGraph">
          <title>Plain (should lose to og:description for description)</title>
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("From JSON-LD", m!.Title);              // json-ld wins
        Assert.Equal("From OpenGraph", m.Description);       // og fills the gap
        // Both strategies are credited:
        Assert.Contains(m.Sources!, s => s.Strategy == "json-ld" && s.Fields.Contains("title"));
        Assert.Contains(m.Sources!, s => s.Strategy == "opengraph" && s.Fields.Contains("description"));
    }

    [Fact]
    public void ReturnsNullForEmptyOrUnparseableHtml()
    {
        Assert.Null(SessionMetadataExtractor.Extract("", null));
        Assert.Null(SessionMetadataExtractor.Extract("   ", null));
        // No structured data and no <title> / <meta> — nothing to surface.
        Assert.Null(SessionMetadataExtractor.Extract("<html><body>just text</body></html>", null));
    }

    [Fact]
    public void TolerantesMalformedJsonLd()
    {
        // A garbage JSON-LD block must not throw; the strategy silently skips and other
        // strategies still run.
        var html = """
        <html><head>
          <script type="application/ld+json">{ this is not json }</script>
          <title>Fallback worked</title>
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("Fallback worked", m!.Title);
    }

    [Fact]
    public void DedupesSpeakersCaseInsensitively()
    {
        var html = """
        <html><head>
          <script type="application/ld+json">
          {
            "@type": "VideoObject",
            "name": "Test",
            "author": [
              {"@type": "Person", "name": "Satya Nadella"},
              {"@type": "Person", "name": "satya nadella"},
              {"@type": "Person", "name": "Jensen Huang"}
            ]
          }
          </script>
        </head><body/></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal(2, m!.Speakers!.Count);
        Assert.Contains("Satya Nadella", m.Speakers);
        Assert.Contains("Jensen Huang", m.Speakers);
    }

    [Fact]
    public void DecodesHtmlEntitiesInTitleAndMeta()
    {
        var html = """
        <html><head>
          <title>One &amp; Two</title>
          <meta name="description" content="A &lt;test&gt; description">
        </head></html>
        """;

        var m = SessionMetadataExtractor.Extract(html, sourceUrl: null);

        Assert.NotNull(m);
        Assert.Equal("One & Two", m!.Title);
        Assert.Equal("A <test> description", m.Description);
    }
}
