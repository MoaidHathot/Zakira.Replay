using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class MediastreamTranscriptInterceptorTests
{
    // ---------- URL detection ----------------------------------------------------------------

    [Theory]
    // Canonical Microsoft Build "InstaVOD" player URLs (the BRK247 / BRK201 shape).
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK247IVOD.json", true)]
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK201IVOD.json", true)]
    // Same host + player.html but no path= query: we can't derive the config URL.
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html", false)]
    // Right host but a different page (not the player wrapper).
    [InlineData("https://mediastream.microsoft.com/events/2026/2606/M9Z7/index.html", false)]
    // Right path-ish but on a different host that we don't want to misidentify.
    [InlineData("https://other.microsoft.com/events/players/live/mvp/player.html?path=/x.json", false)]
    [InlineData("https://medius.microsoft.com/Embed/video-nc/abc", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void IsMediastreamPlayerUrlMatchesPlayerWrapperPages(string url, bool expected)
    {
        Assert.Equal(expected, MediastreamTranscriptInterceptor.IsMediastreamPlayerUrl(url));
    }

    [Theory]
    // Canonical case: extract the path= value, prepend the host.
    [InlineData(
        "https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK247IVOD.json",
        "https://mediastream.microsoft.com/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK247IVOD.json")]
    // path= without a leading slash: we prepend one defensively.
    [InlineData(
        "https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=events/2026/2606/M9Z7/player/json/Config.json",
        "https://mediastream.microsoft.com/events/2026/2606/M9Z7/player/json/Config.json")]
    // URL-encoded path= value: must round-trip correctly.
    [InlineData(
        "https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=%2Fevents%2F2026%2F2606%2FM9Z7%2Fplayer%2Fjson%2FConfig-M9Z7-BRK247IVOD.json",
        "https://mediastream.microsoft.com/events/2026/2606/M9Z7/player/json/Config-M9Z7-BRK247IVOD.json")]
    // Trailing & with extra params: we honour the path= one.
    [InlineData(
        "https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=/x.json&debug=1",
        "https://mediastream.microsoft.com/x.json")]
    public void BuildConfigJsonUrlExtractsPathQuery(string playerUrl, string expected)
    {
        Assert.Equal(expected, MediastreamTranscriptInterceptor.BuildConfigJsonUrl(playerUrl));
    }

    [Theory]
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html")] // no query
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html?other=foo")] // no path=
    [InlineData("https://mediastream.microsoft.com/events/players/live/mvp/player.html?path=")] // empty path=
    [InlineData("")]
    [InlineData("not-a-url")]
    public void BuildConfigJsonUrlReturnsNullForMissingPath(string playerUrl)
    {
        Assert.Null(MediastreamTranscriptInterceptor.BuildConfigJsonUrl(playerUrl));
    }

    // ---------- Config JSON -> HLS master URL ------------------------------------------------

    [Fact]
    public void BuildHlsMasterUrlPicksFirstMainEntryAndHighestWeightCdn()
    {
        // Mirrors the real BRK247 config shape: two CDN regions per origin (weight 100 + 0
        // failover), two main manifests (we-prod-01 then nc-prod-01), and an asl entry we MUST
        // NOT pick.
        var json = """
        {
          "id": "test",
          "coreConfig": {
            "videoTitle": "Test",
            "cdns": {
              "we-prod-01": [
                { "hostName": "https://stream.event.microsoft.com/prodwe", "weight": 100 },
                { "hostName": "https://failover-we.example.com", "weight": 0 }
              ],
              "nc-prod-01": [
                { "hostName": "https://stream.event.microsoft.com/prodnc", "weight": 100 }
              ]
            },
            "manifests": {
              "main": [
                { "origin": "we-prod-01", "manifest": "/Content/HLS/LLCU/abc/master.m3u8", "weight": 100 },
                { "origin": "nc-prod-01", "manifest": "/Content/HLS/LLCU/def/master.m3u8", "weight": 100 }
              ],
              "asl": [
                { "origin": "we-prod-01", "manifest": "/Content/HLS/LLCU/asl/master.m3u8", "weight": 100 }
              ]
            }
          }
        }
        """;
        var config = JsonSerializer.Deserialize<MediastreamConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var url = MediastreamTranscriptInterceptor.BuildHlsMasterUrl(config!);

        Assert.Equal("https://stream.event.microsoft.com/prodwe/Content/HLS/LLCU/abc/master.m3u8", url);
    }

    [Fact]
    public void BuildHlsMasterUrlFallsBackToSecondMainEntryWhenFirstOriginHasNoUsableCdn()
    {
        // First entry's origin maps to an empty CDN list; second's resolves cleanly.
        var json = """
        {
          "coreConfig": {
            "cdns": {
              "broken-origin": [],
              "good-origin": [{ "hostName": "https://cdn.example.com", "weight": 50 }]
            },
            "manifests": {
              "main": [
                { "origin": "broken-origin", "manifest": "/a.m3u8" },
                { "origin": "good-origin", "manifest": "/b.m3u8" }
              ]
            }
          }
        }
        """;
        var config = JsonSerializer.Deserialize<MediastreamConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var url = MediastreamTranscriptInterceptor.BuildHlsMasterUrl(config!);

        Assert.Equal("https://cdn.example.com/b.m3u8", url);
    }

    [Fact]
    public void BuildHlsMasterUrlReturnsNullWhenAllCdnsZeroWeight()
    {
        // Every host has weight=0: nothing to ship.
        var json = """
        {
          "coreConfig": {
            "cdns": { "x": [{ "hostName": "https://x", "weight": 0 }] },
            "manifests": { "main": [{ "origin": "x", "manifest": "/m.m3u8" }] }
          }
        }
        """;
        var config = JsonSerializer.Deserialize<MediastreamConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(MediastreamTranscriptInterceptor.BuildHlsMasterUrl(config!));
    }

    [Theory]
    [InlineData("""{ "coreConfig": null }""")]
    [InlineData("""{ "coreConfig": { "cdns": {}, "manifests": { "main": [] } } }""")]
    [InlineData("""{ "coreConfig": { "manifests": { "main": [{ "origin": "x", "manifest": "/m.m3u8" }] } } }""")] // no cdns
    [InlineData("""{ "coreConfig": { "cdns": { "x": [{"hostName":"h","weight":1}] } } }""")] // no manifests
    public void BuildHlsMasterUrlReturnsNullForMalformedConfigs(string json)
    {
        var config = JsonSerializer.Deserialize<MediastreamConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Null(MediastreamTranscriptInterceptor.BuildHlsMasterUrl(config!));
    }

    // ---------- HLS master playlist -> subtitle track ---------------------------------------

    [Fact]
    public void SelectSubtitlePlaylistPicksFirstEnglishMatchByDefault()
    {
        // Real BRK247-shape master.m3u8 (audio + subtitles + video variants). One subtitle.
        var master = """
        #EXTM3U
        #EXT-X-VERSION:4
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio1",NAME="ENG",DEFAULT=YES,AUTOSELECT=YES,LANGUAGE="ENG",URI="Stream(08)/index.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="caption_1",DEFAULT=NO,AUTOSELECT=YES,LANGUAGE="ENG",URI="Stream(16)/index.m3u8"
        #EXT-X-MEDIA:TYPE=CLOSED-CAPTIONS,GROUP-ID="cc",NAME="caption_1",DEFAULT=NO,AUTOSELECT=YES,LANGUAGE="ENG",INSTREAM-ID="CC1"
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=573600,RESOLUTION=320x180
        Stream(01)/index.m3u8
        """;

        var track = MediastreamTranscriptInterceptor.SelectSubtitlePlaylist(master, preferredLanguage: null);

        Assert.NotNull(track);
        Assert.Equal("Stream(16)/index.m3u8", track!.Uri);
        Assert.Equal("ENG", track.Language);
    }

    [Fact]
    public void SelectSubtitlePlaylistPicksPreferredLanguageWhenMultipleTracks()
    {
        // Two subtitle tracks; preference picks the second.
        var master = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="caption_1",LANGUAGE="ENG",URI="Stream(16)/index.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="caption_2",LANGUAGE="JPN",URI="Stream(17)/index.m3u8"
        """;

        var track = MediastreamTranscriptInterceptor.SelectSubtitlePlaylist(master, preferredLanguage: "ja");

        Assert.NotNull(track);
        Assert.Equal("Stream(17)/index.m3u8", track!.Uri);
        Assert.Equal("JPN", track.Language);
    }

    [Fact]
    public void SelectSubtitlePlaylistFallsBackToFirstWhenPreferenceDoesNotMatch()
    {
        var master = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="caption_1",LANGUAGE="ENG",URI="Stream(16)/index.m3u8"
        """;

        var track = MediastreamTranscriptInterceptor.SelectSubtitlePlaylist(master, preferredLanguage: "ko");

        Assert.NotNull(track);
        Assert.Equal("Stream(16)/index.m3u8", track!.Uri);
    }

    [Fact]
    public void SelectSubtitlePlaylistReturnsNullWhenNoSubtitleEntry()
    {
        // Audio + video but no subtitles \u2014 the bare-page case the original session was
        // chasing. Caller emits CAPTURE_MEDIASTREAM_TRANSCRIPT_FAILED on null.
        var master = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio1",NAME="ENG",URI="Stream(08)/index.m3u8"
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=573600
        Stream(01)/index.m3u8
        """;

        Assert.Null(MediastreamTranscriptInterceptor.SelectSubtitlePlaylist(master, preferredLanguage: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a playlist")]
    public void SelectSubtitlePlaylistReturnsNullForBlankOrJunk(string master)
    {
        Assert.Null(MediastreamTranscriptInterceptor.SelectSubtitlePlaylist(master, preferredLanguage: null));
    }

    [Theory]
    [InlineData("#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"Stream(16)/index.m3u8\",LANGUAGE=\"ENG\"", "URI", "Stream(16)/index.m3u8")]
    [InlineData("#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"Stream(16)/index.m3u8\",LANGUAGE=\"ENG\"", "LANGUAGE", "ENG")]
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=573600,CODECS=\"avc3,mp4a\"", "BANDWIDTH", "573600")]
    [InlineData("#EXT-X-MEDIA:TYPE=AUDIO,URI=\"Stream(08)/index.m3u8\"", "MISSING", null)]
    public void ExtractTagAttributeHandlesQuotedAndBareValues(string line, string attribute, string? expected)
    {
        Assert.Equal(expected, MediastreamTranscriptInterceptor.ExtractTagAttribute(line, attribute));
    }

    // ---------- Subtitle playlist -> segment timeline ---------------------------------------

    [Fact]
    public void ExtractSegmentTimelineProducesCumulativeStartTimes()
    {
        // Three segments at 4s each (BRK247's standard segment duration).
        var playlist = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:4
        #EXT-X-MEDIA-SEQUENCE:0
        #EXTINF:4,
        Segment(1).vtt
        #EXT-X-PROGRAM-DATE-TIME:2026-06-03T22:00:00.000Z
        #EXTINF:4,
        Segment(2).vtt
        #EXT-X-PROGRAM-DATE-TIME:2026-06-03T22:00:04.000Z
        #EXTINF:4,
        Segment(3).vtt
        #EXT-X-ENDLIST
        """;

        var segments = MediastreamTranscriptInterceptor.ExtractSegmentTimeline(playlist);

        Assert.Equal(3, segments.Count);
        Assert.Equal("Segment(1).vtt", segments[0].Uri);
        Assert.Equal(0, segments[0].StartSeconds);
        Assert.Equal(4, segments[0].DurationSeconds);
        Assert.Equal(4, segments[1].StartSeconds);
        Assert.Equal(8, segments[2].StartSeconds);
    }

    [Fact]
    public void ExtractSegmentTimelineHandlesVariableSegmentDurations()
    {
        // Last segment is typically a fractional duration (the encoder flushes the tail).
        var playlist = """
        #EXTM3U
        #EXTINF:4,
        a.vtt
        #EXTINF:4,
        b.vtt
        #EXTINF:2.5,
        tail.vtt
        #EXT-X-ENDLIST
        """;

        var segments = MediastreamTranscriptInterceptor.ExtractSegmentTimeline(playlist);

        Assert.Equal(3, segments.Count);
        Assert.Equal(8, segments[2].StartSeconds);
        Assert.Equal(2.5, segments[2].DurationSeconds);
    }

    [Fact]
    public void ExtractSegmentTimelineSkipsBareUriWithoutPrecedingExtinf()
    {
        // Hardening: don't accept dangling URIs (e.g. comments removed). The pairing rule is
        // strict: every URI must come right after its #EXTINF tag.
        var playlist = """
        #EXTM3U
        dangling.vtt
        #EXTINF:4,
        paired.vtt
        """;

        var segments = MediastreamTranscriptInterceptor.ExtractSegmentTimeline(playlist);

        var only = Assert.Single(segments);
        Assert.Equal("paired.vtt", only.Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a playlist")]
    [InlineData("#NOT-M3U\n#EXTINF:4,\nseg.vtt")] // missing EXTM3U header
    public void ExtractSegmentTimelineRefusesNonPlaylistInput(string text)
    {
        Assert.Empty(MediastreamTranscriptInterceptor.ExtractSegmentTimeline(text));
    }

    // ---------- VTT cue parsing helpers -------------------------------------------------------

    [Theory]
    [InlineData("<c.gray>hello</c>", "hello")]
    [InlineData("<c.gray.bright>hi</c>", "hi")]
    [InlineData("<c>nope</c>", "nope")]
    [InlineData("plain text", "plain text")]
    [InlineData("<c.gray>multi</c> <c.yellow>color</c>", "multi color")]
    [InlineData("", "")]
    public void StripColorTagsRemovesAllCFormVariants(string input, string expected)
    {
        Assert.Equal(expected, MediastreamTranscriptInterceptor.StripColorTags(input));
    }

    [Theory]
    [InlineData("00:00:00.000", 0.0)]
    [InlineData("00:00:04.000", 4.0)]
    [InlineData("00:01:30.500", 90.5)]
    [InlineData("01:02:03.456", 3723.456)]
    [InlineData("02:30.000", 150.0)] // MM:SS form
    public void TryParseVttTimestampHandlesStandardForms(string text, double expected)
    {
        Assert.True(MediastreamTranscriptInterceptor.TryParseVttTimestamp(text, out var actual));
        Assert.Equal(expected, actual, precision: 3);
    }

    [Fact]
    public void ExtractFinalCueLastLineReturnsBottomLineOfHighestEndCue()
    {
        // Mirrors a real BRK247 segment at ~10min: two-line final cue, last line is the
        // currently-growing tail ("but the context win"), top line is the previously settled
        // phrase. We must pick the bottom line.
        var vtt = """
        WEBVTT
        X-TIMESTAMP-MAP=MPEGTS:6476193260,LOCAL:00:00:00.000

        00:00:02.666 --> 00:00:02.699 align:left line:79% position:20%,line-left size:59%
        <c.gray>because I've seen it before,</c>
        <c.gray>bu</c>

        00:00:03.399 --> 00:00:03.966 align:left line:79% position:20%,line-left size:59%
        <c.gray>because I've seen it before,</c>
        <c.gray>but the context</c>

        00:00:03.999 --> 00:00:04.000 align:left line:79% position:20%,line-left size:59%
        <c.gray>because I've seen it before,</c>
        <c.gray>but the context win</c>
        """;

        var line = MediastreamTranscriptInterceptor.ExtractFinalCueLastLine(vtt);

        Assert.Equal("but the context win", line);
    }

    [Fact]
    public void ExtractFinalCueLastLineHandlesSingleLineCue()
    {
        // Segment 0 of BRK247: the speaker just started, the cue has one line growing.
        var vtt = """
        WEBVTT
        X-TIMESTAMP-MAP=MPEGTS:6476193260,LOCAL:00:00:00.000

        00:00:03.932 --> 00:00:03.966 align:left line:85% position:20%,line-left size:59%
        <c.gray>ac</c>

        00:00:03.966 --> 00:00:03.999 align:left line:85% position:20%,line-left size:59%
        <c.gray>actu</c>

        00:00:03.999 --> 00:00:04.000 align:left line:85% position:20%,line-left size:59%
        <c.gray>actual</c>
        """;

        var line = MediastreamTranscriptInterceptor.ExtractFinalCueLastLine(vtt);

        Assert.Equal("actual", line);
    }

    [Fact]
    public void ExtractFinalCueLastLineReturnsNullForEmptyOrNoCuesInput()
    {
        Assert.Null(MediastreamTranscriptInterceptor.ExtractFinalCueLastLine(""));
        Assert.Null(MediastreamTranscriptInterceptor.ExtractFinalCueLastLine("WEBVTT\n"));
        // Cue with only color tags (no actual text) is treated as empty.
        var emptyish = """
        WEBVTT

        00:00:00.000 --> 00:00:04.000
        <c.gray></c>
        """;
        Assert.Null(MediastreamTranscriptInterceptor.ExtractFinalCueLastLine(emptyish));
    }

    // ---------- Rolling-VTT dedupe (end-to-end on the in-memory pipeline) -------------------

    [Fact]
    public void DedupeRollingVttSegmentsCollapsesProgressiveGrowth()
    {
        // The classic word-growing pattern: three segments emit "actu" -> "actually" ->
        // "actually it's interesting". The first two are prefixes of the last; dedupe drops
        // them, keeping just the third with the WIDEST window (start of first segment,
        // end of last).
        var segments = new List<FetchedVttSegment>
        {
            new(0,  4, MakeSimpleVtt("actu")),
            new(4,  4, MakeSimpleVtt("actually")),
            new(8,  4, MakeSimpleVtt("actually it's interesting")),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        Assert.Contains("WEBVTT", merged);
        Assert.Contains("actually it's interesting", merged);
        // The intermediate partials should NOT appear. Normalise to LF first so we don't
        // accidentally pass on a CRLF/LF mismatch when the dedupe SHOULD have caught a
        // duplicate-text condition.
        var normalised = merged.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("00:00:00.000 --> 00:00:04.000\nactu\n", normalised);
        Assert.DoesNotContain("00:00:04.000 --> 00:00:08.000\nactually\n", normalised);
        // Single merged cue covers the whole window.
        Assert.Contains("00:00:00.000 --> 00:00:12.000", merged);
    }

    [Fact]
    public void DedupeRollingVttSegmentsKeepsDistinctPhrasesAsSeparateCues()
    {
        // Two completely different phrases across two segments -> two cues in the output.
        var segments = new List<FetchedVttSegment>
        {
            new(0,  4, MakeSimpleVtt("hello world")),
            new(4,  4, MakeSimpleVtt("totally different phrase")),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        Assert.Contains("hello world", merged);
        Assert.Contains("totally different phrase", merged);
        // First cue ends when the second starts.
        Assert.Contains("00:00:00.000 --> 00:00:04.000", merged);
        Assert.Contains("00:00:04.000 --> 00:00:08.000", merged);
    }

    [Fact]
    public void DedupeRollingVttSegmentsSkipsEmptySegments()
    {
        // The speaker paused for the middle 4s: that segment's last cue is empty (or its VTT
        // is empty after stripping). Result: two cues with a gap, NOT a single merged one.
        var segments = new List<FetchedVttSegment>
        {
            new(0, 4, MakeSimpleVtt("first phrase")),
            new(4, 4, ""), // silence
            new(8, 4, MakeSimpleVtt("second phrase")),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        Assert.Contains("first phrase", merged);
        Assert.Contains("second phrase", merged);
    }

    [Fact]
    public void DedupeRollingVttSegmentsReturnsEmptyWhenAllSegmentsAreEmpty()
    {
        var segments = new List<FetchedVttSegment>
        {
            new(0, 4, ""),
            new(4, 4, ""),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        Assert.Equal(string.Empty, merged);
    }

    [Fact]
    public void DedupeRollingVttSegmentsHandlesRealBrkRollingPattern()
    {
        // Reconstruct the BRK247 ~10min cluster from the original session: segments A and B
        // share "because I've seen it before," in their upper (settled) line; their TAIL grows
        // from "but the context win" -> "but the context window". Segment C moves on to
        // "but the context window is what" (which extends B's tail) on its upper line and
        // introduces "hurt me here" as its growing bottom line.
        //
        // Expected output (multi-line dedupe):
        //   1) "because I've seen it before,"   (upper line of segA; segB's identical upper
        //                                        line is skipped by the "appeared in previous
        //                                        segment" rule)
        //   2) "but the context window is what" (the prefix-extension chain
        //                                        "but the context win" -> "but the context
        //                                        window" -> "but the context window is what"
        //                                        collapses to its longest form)
        //   3) "hurt me here"                   (segC's new bottom line)
        var segA = """
        WEBVTT

        00:00:03.999 --> 00:00:04.000
        <c.gray>because I've seen it before,</c>
        <c.gray>but the context win</c>
        """;
        var segB = """
        WEBVTT

        00:00:03.999 --> 00:00:04.000
        <c.gray>because I've seen it before,</c>
        <c.gray>but the context window</c>
        """;
        var segC = """
        WEBVTT

        00:00:03.999 --> 00:00:04.000
        <c.gray>but the context window is what</c>
        <c.gray>hurt me here</c>
        """;

        var segments = new List<FetchedVttSegment>
        {
            new(0, 4, segA),
            new(4, 4, segB),
            new(8, 4, segC),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        // The three expected phrases all appear.
        Assert.Contains("because I've seen it before,", merged);
        Assert.Contains("but the context window is what", merged);
        Assert.Contains("hurt me here", merged);
        // The growing-tail partials should NOT appear (the prefix-extension dedupe
        // collapsed them).
        var normalised = merged.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("\nbut the context win\n", normalised);
        Assert.DoesNotContain("\nbut the context window\n", normalised);
        // Exactly three cues in the output.
        Assert.Contains("\n1\n", normalised);
        Assert.Contains("\n2\n", normalised);
        Assert.Contains("\n3\n", normalised);
        Assert.DoesNotContain("\n4\n", normalised);
    }

    [Fact]
    public void DedupeRollingVttSegmentsEmitsValidWebvttHeader()
    {
        // Smoke: the merged output is parseable by every VTT consumer (SubtitleConverter).
        var segments = new List<FetchedVttSegment> { new(0, 4, MakeSimpleVtt("hello")) };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        Assert.StartsWith("WEBVTT", merged.TrimStart('\uFEFF'));
        Assert.Contains("00:00:00.000 --> 00:00:04.000", merged);
        Assert.Contains("hello", merged);
    }

    [Fact]
    public void DedupeRollingVttSegmentsCollapsesScreenResidenceAcrossManySegments()
    {
        // BRK247-style: a single "completed" phrase stays visible on the upper line for 4
        // consecutive segments while the bottom line grows through different content. The
        // upper-line phrase must appear exactly ONCE in the output (collapsed by the
        // "appeared in previous segment" rule), not four times.
        string Seg(string upper, string lower) => $"""
            WEBVTT

            00:00:03.999 --> 00:00:04.000
            <c.gray>{upper}</c>
            <c.gray>{lower}</c>
            """;
        var segments = new List<FetchedVttSegment>
        {
            new(0,  4, Seg("the answer is forty-two,", "and that's wh")),
            new(4,  4, Seg("the answer is forty-two,", "and that's why we")),
            new(8,  4, Seg("the answer is forty-two,", "and that's why we built")),
            new(12, 4, Seg("the answer is forty-two,", "and that's why we built it")),
        };

        var merged = MediastreamTranscriptInterceptor.DedupeRollingVttSegments(segments);

        // The upper phrase appears exactly once (not four times).
        var upperOccurrences = (merged.Length - merged.Replace("the answer is forty-two,", "", StringComparison.Ordinal).Length)
            / "the answer is forty-two,".Length;
        Assert.Equal(1, upperOccurrences);
        // The longest form of the growing tail survives the prefix-extension dedupe.
        Assert.Contains("and that's why we built it", merged);
        // The intermediate partials should NOT appear.
        var normalised = merged.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("\nand that's wh\n", normalised);
        Assert.DoesNotContain("\nand that's why we\n", normalised);
        Assert.DoesNotContain("\nand that's why we built\n", normalised);
    }

    // ---------- Language-preference selection -----------------------------------------------

    [Fact]
    public void SelectPreferredLanguageSkipsAutoAndPicksFirstConcreteCode()
    {
        Assert.Equal("ja", MediastreamTranscriptInterceptor.SelectPreferredLanguage(["auto", "ja", "en"]));
        Assert.Null(MediastreamTranscriptInterceptor.SelectPreferredLanguage(["auto"]));
        Assert.Null(MediastreamTranscriptInterceptor.SelectPreferredLanguage([]));
        Assert.Null(MediastreamTranscriptInterceptor.SelectPreferredLanguage(null));
    }

    // ---------- Helpers ---------------------------------------------------------------------

    /// <summary>
    /// Build a tiny VTT body with a single cue ending at 4s carrying the given text on its
    /// bottom line. Sufficient for testing the dedupe pipeline without writing full
    /// rolling-caption sequences in every test.
    /// </summary>
    private static string MakeSimpleVtt(string lastLine)
    {
        return $"""
        WEBVTT

        00:00:03.999 --> 00:00:04.000
        <c.gray>{lastLine}</c>
        """;
    }
}
