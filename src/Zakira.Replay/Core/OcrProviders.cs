namespace Zakira.Replay.Core;

/// <summary>
/// Stable identifiers for OCR provider implementations. These are part of the public artifact
/// contract — the value carried in <c>OcrFrameResult.Provider</c>, <c>manifest.json</c>, and
/// <c>request.json</c>. Rename only with a schema bump.
/// </summary>
public static class OcrProviders
{
    /// <summary>
    /// OCR via an LLM provider (GitHub Copilot, OpenAI, or Azure OpenAI), using vision-capable
    /// chat models. Default for backward compatibility; matches the original behaviour of
    /// <see cref="CopilotOcrProvider"/>.
    /// </summary>
    public const string Copilot = "copilot";

    /// <summary>
    /// Fully local OCR via ONNX models (RapidOCR / PP-OCRv5). No LLM, no network at run-time
    /// after the models are installed via <c>deps install ocr</c>.
    /// </summary>
    public const string Local = "local";
}

/// <summary>
/// Resolves an <see cref="IOcrProvider"/> from CLI/config inputs. Mirrors the shape of
/// <see cref="LlmProviderFactory"/> so the same orchestrator patterns apply.
/// </summary>
public static class OcrProviderFactory
{
    /// <summary>
    /// Returns the OCR provider name configured for the current environment, honouring
    /// <c>ZAKIRA_REPLAY_OCR_PROVIDER</c>, then <c>ocr.provider</c> in config, then the
    /// <see cref="OcrProviders.Local"/> default (offline RapidOCR via ONNX).
    /// </summary>
    public static string GetConfiguredProvider(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return Normalize(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_PROVIDER") ?? config.Ocr.Provider);
    }

    /// <summary>
    /// Normalises a user-supplied provider string into one of the <see cref="OcrProviders"/>
    /// constants. Unknown values are returned verbatim so the caller can emit
    /// <c>OCR_UNKNOWN_PROVIDER</c>. Empty/null inputs default to <see cref="OcrProviders.Local"/>.
    /// </summary>
    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return OcrProviders.Local;
        }

        return provider.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "copilot" or "llm" or "github-copilot" or "openai" or "azure-openai" => OcrProviders.Copilot,
            "local" or "rapidocr" or "rapid-ocr" or "onnx" or "paddleocr" or "paddle-ocr" or "offline" => OcrProviders.Local,
            var value => value
        };
    }
}
