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
            Instruction: request.Instruction,
            IncludeTranscript: request.IncludeTranscript,
            FrameCount: request.FrameCount,
            ExtractAudio: request.ExtractAudio,
            UseSpeechToText: request.UseSpeechToText,
            UseOcr: request.UseOcr,
            UseVision: request.UseVision,
            UseSummary: request.UseSummary,
            MaxAiFrames: request.MaxAiFrames,
            Model: request.Model,
            LlmProvider: request.LlmProvider,
            FrameStrategy: request.FrameStrategy,
            CookiesPath: request.CookiesPath,
            CookiesFromBrowser: request.CookiesFromBrowser);
        var json = JsonSerializer.Serialize(input, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private sealed record CacheKeyInput(
        string Source,
        string Instruction,
        bool IncludeTranscript,
        int FrameCount,
        bool ExtractAudio,
        bool UseSpeechToText,
        bool UseOcr,
        bool UseVision,
        bool UseSummary,
        int MaxAiFrames,
        string Model,
        string LlmProvider,
        string FrameStrategy,
        string? CookiesPath,
        string? CookiesFromBrowser);
}

public sealed record ArtifactCacheEntry(
    string SchemaVersion,
    string CacheKey,
    string RunId,
    DateTimeOffset CreatedAt);
