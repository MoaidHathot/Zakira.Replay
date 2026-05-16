namespace Zakira.Replay.Core;

/// <summary>
/// Stable identifiers for vision provider implementations. Mirrors <see cref="OcrProviders"/>
/// in shape. These are part of the public artifact contract — the value carried in
/// <c>VisionFrameResult.Provider</c>, <c>manifest.json</c>, and <c>request.json</c>. Rename
/// only with a schema bump.
/// </summary>
public static class VisionProviders
{
    /// <summary>
    /// Vision via an LLM provider (GitHub Copilot, OpenAI, Azure OpenAI, or Ollama) using
    /// vision-capable chat models. Default for backward compatibility; produced by
    /// <see cref="CopilotVisionProvider"/>.
    /// </summary>
    public const string Copilot = "copilot";

    /// <summary>
    /// Fully local vision via classical CV + optional ONNX models (CLIP for zero-shot kind
    /// classification, BLIP for image captioning). Never invokes an LLM. Produced by
    /// <see cref="LocalOnnxVisionProvider"/>; behaviour controlled by <see cref="LocalVisionMode"/>.
    /// </summary>
    public const string Local = "local";
}

/// <summary>
/// Sub-mode of the local (no-LLM) vision provider. Selects how much classical-CV/ONNX
/// machinery the provider engages. The three values are ordered by footprint and quality:
/// <see cref="Heuristic"/> needs no models, <see cref="Clip"/> adds a CLIP image encoder
/// (~150 MB), and <see cref="ClipBlip"/> additionally loads BLIP image-captioning (~400 MB).
/// </summary>
public enum LocalVisionMode
{
    /// <summary>
    /// No models. Structure derived entirely from the OCR result for the same frame:
    /// <c>Kind</c> by token-pattern scoring, <c>Title</c> / <c>Bullets</c> / <c>CodeBlocks</c>
    /// / <c>UiElements</c> by layout heuristics, <c>FreeText</c> = concatenated OCR.
    /// </summary>
    Heuristic = 0,

    /// <summary>
    /// CLIP zero-shot classification fills <c>Kind</c>. Structured fields and <c>FreeText</c>
    /// remain heuristic-derived from OCR.
    /// </summary>
    Clip = 1,

    /// <summary>
    /// CLIP for <c>Kind</c> plus BLIP-base image captioning to fill <c>FreeText</c> with an
    /// actual visual description (prefixed by "Frame appears to show:" so consumers know it
    /// came from a smaller captioning model). Structured fields stay heuristic-derived.
    /// Default when <c>--vision-provider local</c> is selected without an explicit
    /// <c>--local-vision-mode</c>.
    /// </summary>
    ClipBlip = 2
}

/// <summary>
/// Resolves an <see cref="IVisionProvider"/> selection from CLI/config inputs. Mirrors the
/// shape of <see cref="OcrProviderFactory"/> so the same orchestrator patterns apply.
/// </summary>
public static class VisionProviderFactory
{
    /// <summary>
    /// Returns the vision provider name configured for the current environment, honouring
    /// <c>ZAKIRA_REPLAY_VISION_PROVIDER</c>, then <c>vision.provider</c> in config, then the
    /// <see cref="VisionProviders.Copilot"/> default.
    /// </summary>
    public static string GetConfiguredProvider(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return Normalize(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_PROVIDER") ?? config.Vision.Provider);
    }

    /// <summary>
    /// Returns the local-vision sub-mode configured for the current environment, honouring
    /// <c>ZAKIRA_REPLAY_VISION_LOCAL_MODE</c>, then <c>vision.local.mode</c> in config, then
    /// the <see cref="LocalVisionMode.ClipBlip"/> default.
    /// </summary>
    public static LocalVisionMode GetConfiguredLocalMode(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();
        return NormalizeMode(Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_LOCAL_MODE") ?? config.Vision.Local.Mode);
    }

    /// <summary>
    /// Normalises a user-supplied provider string into one of the <see cref="VisionProviders"/>
    /// constants. Unknown values are returned verbatim so the caller can emit
    /// <c>VISION_UNKNOWN_PROVIDER</c>. Empty/null inputs default to
    /// <see cref="VisionProviders.Copilot"/> (existing behaviour).
    /// </summary>
    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return VisionProviders.Copilot;
        }

        return provider.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "copilot" or "llm" or "github-copilot" or "openai" or "azure-openai" or "ollama" => VisionProviders.Copilot,
            "local" or "local-onnx" or "onnx" or "offline" => VisionProviders.Local,
            var value => value
        };
    }

    /// <summary>
    /// Normalises a user-supplied mode string into a <see cref="LocalVisionMode"/> enum value.
    /// Empty/null/unknown inputs default to <see cref="LocalVisionMode.ClipBlip"/>.
    /// </summary>
    public static LocalVisionMode NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return LocalVisionMode.ClipBlip;
        }

        return mode.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "heuristic" or "heuristics" or "ocr-only" or "ocr" or "no-models" or "none" => LocalVisionMode.Heuristic,
            "clip" or "clip-only" or "zero-shot" => LocalVisionMode.Clip,
            "clip-blip" or "clip+blip" or "blip" or "full" or "default" => LocalVisionMode.ClipBlip,
            _ => LocalVisionMode.ClipBlip
        };
    }

    /// <summary>
    /// Returns the canonical config-string form of a <see cref="LocalVisionMode"/>. Used by
    /// <c>info --json</c>, the cache key, and the request manifest for audit.
    /// </summary>
    public static string FormatMode(LocalVisionMode mode) => mode switch
    {
        LocalVisionMode.Heuristic => "heuristic",
        LocalVisionMode.Clip => "clip",
        LocalVisionMode.ClipBlip => "clip-blip",
        _ => "clip-blip"
    };
}
