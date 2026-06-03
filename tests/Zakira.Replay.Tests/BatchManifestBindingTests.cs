using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class BatchManifestBindingTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static BatchManifest Deserialize(string json)
        => JsonSerializer.Deserialize<BatchManifest>(json, WebOptions)
           ?? throw new InvalidOperationException("manifest deserialized to null");

    [Fact]
    public void ManifestBindsCaptureModeAuthProfileAndDiarizationFromJson()
    {
        // The exact fields the published schema advertised but the model previously dropped.
        var json = """
        {
          "captureMode": "browser",
          "authProfile": "edge",
          "useDiarization": true,
          "numSpeakers": 3,
          "diarizationThreshold": 0.55,
          "ocrProvider": "copilot",
          "smartCrop": true,
          "smartCropProfile": "slides",
          "items": [ { "source": "https://example.com/v1" } ]
        }
        """;

        var manifest = Deserialize(json);

        Assert.Equal("browser", manifest.CaptureMode);
        Assert.Equal("edge", manifest.AuthProfile);
        Assert.True(manifest.UseDiarization);
        Assert.Equal(3, manifest.NumSpeakers);
        Assert.Equal(0.55, manifest.DiarizationThreshold);
        Assert.Equal("copilot", manifest.OcrProvider);
        Assert.True(manifest.SmartCrop);
        Assert.Equal("slides", manifest.SmartCropProfile);
    }

    [Fact]
    public void ItemBindsCaptureModeAuthProfileAndDiarizationFromJson()
    {
        var json = """
        {
          "items": [
            {
              "source": "https://example.com/v1",
              "captureMode": "ytdlp",
              "authProfile": "work",
              "useDiarization": true,
              "numSpeakers": 2,
              "diarizationThreshold": 0.4,
              "ocrProvider": "local",
              "smartCrop": false,
              "smartCropProfile": "talking-head"
            }
          ]
        }
        """;

        var item = Deserialize(json).Items.Single();

        Assert.Equal("ytdlp", item.CaptureMode);
        Assert.Equal("work", item.AuthProfile);
        Assert.True(item.UseDiarization);
        Assert.Equal(2, item.NumSpeakers);
        Assert.Equal(0.4, item.DiarizationThreshold);
        Assert.Equal("local", item.OcrProvider);
        Assert.False(item.SmartCrop);
        Assert.Equal("talking-head", item.SmartCropProfile);
    }

    [Fact]
    public void BuildAnalyzeRequestPropagatesManifestLevelFields()
    {
        var manifest = new BatchManifest
        {
            CaptureMode = "browser",
            AuthProfile = "edge",
            UseDiarization = true,
            NumSpeakers = 4,
            DiarizationThreshold = 0.6,
            SmartCrop = true,
            SmartCropProfile = "slides",
            Items = [new BatchItem { Source = "https://example.com/v1" }],
        };

        var request = BatchRunner.BuildAnalyzeRequest(manifest, manifest.Items[0]);

        Assert.Equal("browser", request.CaptureMode);
        Assert.Equal("edge", request.AuthProfile);
        Assert.True(request.UseDiarization);
        Assert.Equal(4, request.NumSpeakers);
        Assert.Equal(0.6f, request.DiarizationThreshold);
        Assert.True(request.SmartCrop);
        Assert.Equal("slides", request.SmartCropProfile);
    }

    [Fact]
    public void BuildAnalyzeRequestItemOverridesManifest()
    {
        var manifest = new BatchManifest
        {
            CaptureMode = "ytdlp",
            AuthProfile = "manifest-profile",
            UseDiarization = false,
            Items =
            [
                new BatchItem
                {
                    Source = "https://example.com/v1",
                    CaptureMode = "browser",
                    AuthProfile = "item-profile",
                    UseDiarization = true,
                },
            ],
        };

        var request = BatchRunner.BuildAnalyzeRequest(manifest, manifest.Items[0]);

        Assert.Equal("browser", request.CaptureMode);
        Assert.Equal("item-profile", request.AuthProfile);
        Assert.True(request.UseDiarization);
    }

    [Fact]
    public void BuildAnalyzeRequestLeavesDiarizationThresholdUnsetWhenNonPositive()
    {
        var manifest = new BatchManifest
        {
            DiarizationThreshold = 0,
            Items = [new BatchItem { Source = "https://example.com/v1" }],
        };

        var request = BatchRunner.BuildAnalyzeRequest(manifest, manifest.Items[0]);

        Assert.Null(request.DiarizationThreshold);
    }

    [Fact]
    public void BuildAnalyzeRequestDefaultsCaptureModeAndAuthProfileToNull()
    {
        var manifest = new BatchManifest
        {
            Items = [new BatchItem { Source = "https://example.com/v1" }],
        };

        var request = BatchRunner.BuildAnalyzeRequest(manifest, manifest.Items[0]);

        Assert.Null(request.CaptureMode);
        Assert.Null(request.AuthProfile);
        Assert.False(request.UseDiarization);
    }
}
