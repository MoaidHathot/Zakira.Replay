using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class SharePointStreamInterceptorTests
{
    [Theory]
    [InlineData("https://microsofteur-my.sharepoint.com/personal/idof_microsoft_com/_api/v2.1/drives/b!abc/items/012XYZ?select=media%2Ftranscripts%2CaudioTracks&%24expand=media%2Ftranscripts%2Cmedia%2FaudioTracks", true)]
    [InlineData("https://corp-my.sharepoint.com/personal/a_b_c/_api/v2.0/drives/x/items/y?select=media/transcripts&$expand=media/transcripts", true)]
    [InlineData("https://corp.sharepoint.com/sites/x/_api/v2.1/drives/d/items/i?$expand=media%2Ftranscripts", true)]
    // The per-transcript content URL is NOT the metadata URL.
    [InlineData("https://x.sharepoint.com/_api/v2.1/drives/d/items/i/versions/current/media/transcripts/abc/content", false)]
    // Plain item URL with no transcripts in the query.
    [InlineData("https://x.sharepoint.com/_api/v2.1/drives/d/items/i", false)]
    // Other Stream URLs we should NOT match (frame fragments, etc.).
    [InlineData("https://x.sharepoint.com/_api_cached/v2.1/drives/d/items/i/oneDrive.transcode?part=mediasegment&segmentTime=1234", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void IsTranscriptMetadataUrlMatchesSharePointPatterns(string url, bool expected)
    {
        Assert.Equal(expected, SharePointStreamInterceptor.IsTranscriptMetadataUrl(url));
    }

    [Fact]
    public void TryConvertToVttPassesThroughWebVtt()
    {
        var input = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello world\n";
        var output = SharePointStreamInterceptor.TryConvertToVtt(input);

        Assert.NotNull(output);
        Assert.StartsWith("WEBVTT", output);
        Assert.Contains("00:00:00.000", output);
        Assert.Contains("Hello world", output);
    }

    [Fact]
    public void TryConvertToVttHandlesTeamsJsonEntries()
    {
        var json = """
        {
          "entries": [
            { "startTime": 1.5,   "endTime": 3.25,  "text": "First sentence.",  "speakerDisplayName": "Alice" },
            { "startTime": 3.25,  "endTime": 6.0,   "text": "Second sentence.", "speakerDisplayName": "Bob"   }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.StartsWith("WEBVTT", output);
        Assert.Contains("00:00:01.500 --> 00:00:03.250", output);
        Assert.Contains("00:00:03.250 --> 00:00:06.000", output);
        Assert.Contains("<v Alice>First sentence.", output);
        Assert.Contains("<v Bob>Second sentence.", output);
    }

    [Fact]
    public void TryConvertToVttHandlesTeamsJsonEvents()
    {
        var json = """
        {
          "events": [
            { "start": 0,    "end": 1.0, "text": "One" },
            { "start": 1.0,  "end": 2.0, "text": "Two" }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("One", output);
        Assert.Contains("Two", output);
    }

    [Fact]
    public void TryConvertToVttHandlesIso8601StartEndOffsets()
    {
        var json = """
        {
          "entries": [
            { "startOffset": "PT0S",      "endOffset": "PT2S",     "text": "Hello"   },
            { "startOffset": "PT2S",      "endOffset": "PT4.5S",   "text": "World"   }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("00:00:00.000 --> 00:00:02.000", output);
        Assert.Contains("00:00:02.000 --> 00:00:04.500", output);
    }

    [Fact]
    public void TryConvertToVttHandlesRecognizedPhrasesShape()
    {
        var json = """
        {
          "recognizedPhrases": [
            {
              "startTime": 0.0,
              "endTime": 1.5,
              "nBest": [
                { "display": "Hello there.", "confidence": 0.95 }
              ]
            }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("Hello there.", output);
    }

    [Fact]
    public void TryConvertToVttHandlesRawArrayRoot()
    {
        var json = """
        [
          { "startTime": 0, "endTime": 1, "text": "alpha" },
          { "startTime": 1, "endTime": 2, "text": "beta"  }
        ]
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("alpha", output);
        Assert.Contains("beta", output);
    }

    [Fact]
    public void TryConvertToVttReturnsNullForUnknownShape()
    {
        var json = """{ "somethingElse": [{ "x": 1 }] }""";
        Assert.Null(SharePointStreamInterceptor.TryConvertToVtt(json));
    }

    [Fact]
    public void TryConvertToVttReturnsNullForGarbage()
    {
        Assert.Null(SharePointStreamInterceptor.TryConvertToVtt(""));
        Assert.Null(SharePointStreamInterceptor.TryConvertToVtt("not json, not vtt"));
    }

    [Fact]
    public void TryConvertToVttSkipsEntriesWithoutText()
    {
        var json = """
        {
          "entries": [
            { "startTime": 0, "endTime": 1, "text": "" },
            { "startTime": 1, "endTime": 2, "text": "kept" }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("kept", output);
        // The empty-text entry must not produce a cue.
        var cueCount = (output.Split("-->").Length - 1);
        Assert.Equal(1, cueCount);
    }

    [Fact]
    public void TryConvertToVttSkipsEntriesWithBadTiming()
    {
        var json = """
        {
          "entries": [
            { "startTime": 5, "endTime": 2, "text": "backwards" },
            { "startTime": 0, "endTime": 1, "text": "kept"      }
          ]
        }
        """;
        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.Contains("kept", output);
        Assert.DoesNotContain("backwards", output);
    }

    /// <summary>
    /// The exact shape we observed downloading a Microsoft Teams transcript via the
    /// <c>?isformatjson=true</c> URL variant on the SharePoint Stream content endpoint.
    /// Anchors the converter against the real-world schema so this format keeps working if
    /// the converter is ever refactored.
    /// </summary>
    [Fact]
    public void TryConvertToVttHandlesRealTeamsTranscriptShape()
    {
        // Trimmed to two entries; the actual response also has speechServiceResultId, confidence,
        // roomId, spokenLanguageTag, hasBeenEdited \u2014 all of which the converter must tolerate.
        var json = """
        {
          "$schema": "http://stream.office.com/schemas/transcript.json",
          "version": "1.0.0",
          "type": "Transcript",
          "entries": [
            {
              "id": "faa02c46-6a8c-41d7-a8d3-5065badd57aa/30",
              "speechServiceResultId": "dad43b5ca85340bb8bc57f361fe13acc",
              "text": "Hello, good morning, everyone.",
              "rawText": null,
              "speakerId": "14ee28ff-1735-4b11-975b-2defa68ae71c@72f988bf-86f1-41af-91ab-2d7cd011db47",
              "speakerDisplayName": "Liad Shiran",
              "confidence": 0.795949399471283,
              "startOffset": "00:00:06.3720481",
              "endOffset": "00:00:09.5720481",
              "hasBeenEdited": false,
              "roomId": "5b41b7d5-17fd-40ab-b4de-f7ab5881777c@72f988bf-86f1-41af-91ab-2d7cd011db47",
              "spokenLanguageTag": "he-il"
            },
            {
              "id": "faa02c46-6a8c-41d7-a8d3-5065badd57aa/59",
              "text": "Let's get started.",
              "speakerDisplayName": "Boris Forzun",
              "startOffset": "00:00:11.1120481",
              "endOffset": "00:00:17.9120481"
            }
          ]
        }
        """;

        var output = SharePointStreamInterceptor.TryConvertToVtt(json);

        Assert.NotNull(output);
        Assert.StartsWith("WEBVTT", output);
        // ISO 8601 startOffset/endOffset must be parsed.
        Assert.Contains("00:00:06.372 --> 00:00:09.572", output);
        Assert.Contains("00:00:11.112 --> 00:00:17.912", output);
        // Speaker tags must appear in the canonical VTT voice-span form so SubtitleConverter
        // picks them up as speaker attribution.
        Assert.Contains("<v Liad Shiran>Hello, good morning, everyone.", output);
        Assert.Contains("<v Boris Forzun>Let's get started.", output);
    }
}
