using System.Globalization;
using System.Text;
using Whisper.net;

namespace Zakira.Replay.Core;

/// <summary>
/// Local speech-to-text provider backed by <a href="https://github.com/sandrohanea/whisper.net">Whisper.net</a>
/// (managed bindings to whisper.cpp). Runs entirely on the caller's machine — no LLM, no
/// network — and returns the same Markdown shape <see cref="CopilotTranscriptionProvider"/>
/// produces, so <see cref="ChunkedTranscriptionService"/> can stitch chunks transparently.
/// </summary>
/// <remarks>
/// Selected via <c>--llm-provider local-whisper</c>. Requires a ggml model file installed via
/// <c>zakira-replay deps install whisper-model [tiny|base|small|medium|large-v3|large-v3-turbo]</c>
/// or referenced explicitly through <c>ZAKIRA_REPLAY_WHISPER_MODEL_PATH</c> / config key
/// <c>llm.localWhisper.modelPath</c>.
/// </remarks>
public sealed class LocalWhisperTranscriptionProvider : ITranscriptionProvider, IDisposable
{
    private readonly LocalWhisperOptions options;
    private readonly object initLock = new();
    private WhisperFactory? factory;
    private bool initialised;
    private bool disposed;

    public LocalWhisperTranscriptionProvider(LocalWhisperOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureInitialised();
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(audioPath))
        {
            throw new ReplayException($"Local Whisper cannot read audio: file not found at '{audioPath}'.");
        }

        // Build a fresh processor per call. WhisperProcessor holds per-inference state (segment
        // callback wiring, language detection, etc.); building it per call is the safe pattern
        // documented in Whisper.net. The expensive native model load lives on the shared
        // WhisperFactory so the cost is paid once per process.
        using var processor = BuildProcessor();
        await using var pcm = File.OpenRead(audioPath);

        var builder = new StringBuilder();
        try
        {
            await foreach (var segment in processor.ProcessAsync(pcm, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendSegmentLine(builder, segment);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayException($"Local Whisper transcription failed for '{audioPath}': {ex.Message}", ex);
        }

        return builder.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        factory?.Dispose();
        factory = null;
    }

    /// <summary>
    /// Builds a single markdown line for a Whisper segment in the canonical
    /// <c>**[mm:ss - mm:ss]** text</c> format used everywhere else in the pipeline.
    /// Public so unit tests can verify the contract without spinning up native code.
    /// </summary>
    public static void AppendSegmentLine(StringBuilder builder, SegmentData segment)
    {
        var text = segment.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var startSeconds = Math.Max(0, segment.Start.TotalSeconds);
        var endSeconds = Math.Max(startSeconds, segment.End.TotalSeconds);
        builder.Append("**[")
            .Append(Timestamp.Format(startSeconds))
            .Append(" - ")
            .Append(Timestamp.Format(endSeconds))
            .Append("]** ")
            .AppendLine(text);
    }

    private WhisperProcessor BuildProcessor()
    {
        var builder = factory!.CreateBuilder();
        var language = string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language!.Trim();
        if (language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            builder = builder.WithLanguageDetection();
        }
        else
        {
            builder = builder.WithLanguage(language);
        }

        if (options.Threads is > 0)
        {
            builder = builder.WithThreads(options.Threads.Value);
        }

        return builder.Build();
    }

    private void EnsureInitialised()
    {
        if (initialised)
        {
            return;
        }

        lock (initLock)
        {
            if (initialised)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(options.ModelPath))
            {
                throw new ReplayException(
                    "Local Whisper requires a model path. Run `zakira-replay deps install whisper-model small` or set `llm.localWhisper.modelPath` (or `ZAKIRA_REPLAY_WHISPER_MODEL_PATH`).");
            }

            if (!File.Exists(options.ModelPath))
            {
                throw new ReplayException(
                    $"Local Whisper model not found at '{options.ModelPath}'. Run `zakira-replay deps install whisper-model {LocalWhisperOptions.DefaultModelSize}` to download it, or set `llm.localWhisper.modelPath` to an existing ggml model file.");
            }

            try
            {
                factory = WhisperFactory.FromPath(options.ModelPath);
                initialised = true;
            }
            catch (Exception ex) when (ex is not ReplayException)
            {
                throw new ReplayException(
                    $"Failed to initialise local Whisper engine for '{options.ModelPath}': {ex.Message}. " +
                    "Verify the file is a valid ggml model and that the matching whisper.cpp native runtime is installed for this platform.",
                    ex);
            }
        }
    }
}

/// <summary>
/// Resolved configuration for <see cref="LocalWhisperTranscriptionProvider"/>. Use
/// <see cref="Resolve(ReplayConfig?)"/> to populate from environment, config, and defaults.
/// </summary>
public sealed record LocalWhisperOptions(string? ModelPath, string? Language, int? Threads)
{
    /// <summary>Default ggml model size when neither env vars nor config specify one.</summary>
    public const string DefaultModelSize = "small";

    /// <summary>Default language hint. <c>auto</c> enables Whisper's built-in language detection.</summary>
    public const string DefaultLanguage = "auto";

    /// <summary>
    /// Resolve all options for the local Whisper provider. Resolution order:
    /// <list type="number">
    ///   <item>Environment variables (<c>ZAKIRA_REPLAY_WHISPER_*</c>).</item>
    ///   <item>Explicit config values (<c>llm.localWhisper.*</c>).</item>
    ///   <item>Derived defaults (<see cref="DefaultModelSize"/>, <see cref="DefaultLanguage"/>) over the
    ///         portable model directory.</item>
    /// </list>
    /// </summary>
    public static LocalWhisperOptions Resolve(ReplayConfig? config = null)
    {
        config ??= new ConfigStore().Load();

        var modelPath = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_MODEL_PATH"),
            config.Llm.LocalWhisper.ModelPath);

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            var installer = new PortableDependencyInstaller(config);
            var modelSize = NormalizeModelSize(FirstNonEmpty(
                Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_MODEL_SIZE"),
                config.Llm.LocalWhisper.ModelSize,
                DefaultModelSize)!);

            modelPath = Path.Combine(installer.Layout.WhisperModelDirectory, BuildModelFileName(modelSize));
        }

        var language = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_LANGUAGE"),
            config.Llm.LocalWhisper.Language,
            DefaultLanguage);

        int? threads = null;
        var threadsValue = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_THREADS"),
            config.Llm.LocalWhisper.Threads is null ? null : config.Llm.LocalWhisper.Threads.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(threadsValue)
            && int.TryParse(threadsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedThreads)
            && parsedThreads > 0)
        {
            threads = parsedThreads;
        }

        return new LocalWhisperOptions(modelPath, language, threads);
    }

    /// <summary>
    /// Produce the canonical ggml model file name for a given size, matching the naming used by
    /// the whisper.cpp Hugging Face repository (<c>ggml-&lt;size&gt;.bin</c>).
    /// </summary>
    public static string BuildModelFileName(string modelSize)
    {
        var normalized = NormalizeModelSize(modelSize);
        return $"ggml-{normalized}.bin";
    }

    /// <summary>
    /// Normalise a user-supplied size string. Accepts canonical names (<c>tiny</c>, <c>base</c>,
    /// <c>small</c>, <c>medium</c>, <c>large-v3</c>, <c>large-v3-turbo</c>), English-only variants
    /// (<c>tiny.en</c>, …), and a handful of aliases (<c>turbo</c> → <c>large-v3-turbo</c>).
    /// </summary>
    public static string NormalizeModelSize(string? modelSize)
    {
        if (string.IsNullOrWhiteSpace(modelSize))
        {
            return DefaultModelSize;
        }

        var trimmed = modelSize.Trim().ToLowerInvariant().Replace('_', '-');
        return trimmed switch
        {
            "turbo" => "large-v3-turbo",
            "large" => "large-v3",
            _ => trimmed
        };
    }

    /// <summary>
    /// Supported model sizes that <see cref="PortableDependencyInstaller"/> can fetch from the
    /// official whisper.cpp Hugging Face repository.
    /// </summary>
    public static IReadOnlyList<string> SupportedModelSizes { get; } =
    [
        "tiny", "tiny.en",
        "base", "base.en",
        "small", "small.en",
        "medium", "medium.en",
        "large-v1",
        "large-v2",
        "large-v3",
        "large-v3-turbo"
    ];

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
