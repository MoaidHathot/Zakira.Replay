using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

public static class AnalysisCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string CreateKey(AnalyzeRequest request)
    {
        var input = new CacheKeyInput(
            Source: request.Source,
            VisionInstruction: request.VisionInstruction,
            OcrInstruction: request.OcrInstruction,
            IncludeTranscript: request.IncludeTranscript,
            FrameCount: request.FrameCount,
            ExtractAudio: request.ExtractAudio,
            UseSpeechToText: request.UseSpeechToText,
            UseOcr: request.UseOcr,
            UseVision: request.UseVision,
            MaxAiFrames: request.MaxAiFrames,
            Model: request.Model,
            LlmProvider: request.LlmProvider,
            FrameStrategy: request.FrameStrategy,
            CookiesPath: request.CookiesPath,
            CookiesFromBrowser: request.CookiesFromBrowser,
            CaptionLanguages: request.CaptionLanguages is { Count: > 0 }
                ? request.CaptionLanguages.Select(language => language.Trim().ToLowerInvariant()).ToArray()
                : null,
            SlideGrouping: request.SlideGrouping,
            SlideHashDistance: request.SlideHashDistance,
            FramesPerMinute: request.FramesPerMinute,
            SceneSafetyCap: request.SceneSafetyCap);
        var json = JsonSerializer.Serialize(input, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private sealed record CacheKeyInput(
        string Source,
        string VisionInstruction,
        string OcrInstruction,
        bool IncludeTranscript,
        int FrameCount,
        bool ExtractAudio,
        bool UseSpeechToText,
        bool UseOcr,
        bool UseVision,
        int MaxAiFrames,
        string Model,
        string LlmProvider,
        string FrameStrategy,
        string? CookiesPath,
        string? CookiesFromBrowser,
        IReadOnlyList<string>? CaptionLanguages,
        bool? SlideGrouping,
        int? SlideHashDistance,
        int? FramesPerMinute,
        int? SceneSafetyCap);
}

public sealed record ArtifactCacheEntry(
    string SchemaVersion,
    string CacheKey,
    string RunId,
    DateTimeOffset CreatedAt);
