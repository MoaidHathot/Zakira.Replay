using System.Text;
using Whisper.net;
using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class LocalWhisperTranscriptionProviderTests
{
    [Theory]
    [InlineData("local-whisper", LlmProviders.LocalWhisper)]
    [InlineData("localwhisper", LlmProviders.LocalWhisper)]
    [InlineData("whisper", LlmProviders.LocalWhisper)]
    [InlineData("local-stt", LlmProviders.LocalWhisper)]
    [InlineData("LOCAL-WHISPER", LlmProviders.LocalWhisper)]
    [InlineData(" Whisper ", LlmProviders.LocalWhisper)]
    [InlineData("LOCAL_WHISPER", LlmProviders.LocalWhisper)]
    public void NormalizeMapsAliasesToLocalWhisper(string input, string expected)
    {
        Assert.Equal(expected, LlmProviderFactory.Normalize(input));
    }

    [Fact]
    public void TryCreateReturnsNullForLocalWhisperSoOcrAndVisionFallBackToWarnings()
    {
        // local-whisper is STT-only. The analysis pipeline's chat-LLM resolution path uses
        // TryCreate so OCR/vision branches see "no chat LLM" instead of an exception.
        Assert.Null(LlmProviderFactory.TryCreate("local-whisper", new ReplayConfig()));
    }

    [Fact]
    public void CreateThrowsForLocalWhisperWithGuidanceMessage()
    {
        // llm ask --llm-provider local-whisper should be rejected with a clear message.
        var ex = Assert.Throws<ReplayException>(() => LlmProviderFactory.Create("local-whisper", new ReplayConfig()));
        Assert.Contains("speech-to-text only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultModelReturnsConfiguredWhisperSize()
    {
        var config = new ReplayConfig
        {
            Llm = new LlmConfig
            {
                LocalWhisper = new LocalWhisperConfig { ModelSize = "medium" }
            }
        };

        Assert.Equal("medium", LlmProviderFactory.GetDefaultModel("local-whisper", config));
    }

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("small", "ggml-small.bin")]
    [InlineData("large-v3-turbo", "ggml-large-v3-turbo.bin")]
    [InlineData("LARGE_V3", "ggml-large-v3.bin")]
    [InlineData("turbo", "ggml-large-v3-turbo.bin")]
    [InlineData("large", "ggml-large-v3.bin")]
    [InlineData(null, "ggml-small.bin")]
    [InlineData("", "ggml-small.bin")]
    public void BuildModelFileNameProducesGgmlConvention(string? size, string expected)
    {
        Assert.Equal(expected, LocalWhisperOptions.BuildModelFileName(size!));
    }

    [Fact]
    public void SupportedModelSizesListIsImmutableAndCoversAllOfficialSizes()
    {
        // Sanity check that we didn't accidentally drop a size when reformatting.
        Assert.Contains("tiny", LocalWhisperOptions.SupportedModelSizes);
        Assert.Contains("base.en", LocalWhisperOptions.SupportedModelSizes);
        Assert.Contains("large-v3", LocalWhisperOptions.SupportedModelSizes);
        Assert.Contains("large-v3-turbo", LocalWhisperOptions.SupportedModelSizes);
        Assert.Equal(12, LocalWhisperOptions.SupportedModelSizes.Count);
    }

    [Fact]
    public void ResolveHonoursEnvironmentVariableOverModelPath()
    {
        const string envVar = "ZAKIRA_REPLAY_WHISPER_MODEL_PATH";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "/custom/path/ggml-medium.bin");
            var config = new ReplayConfig
            {
                Llm = new LlmConfig
                {
                    LocalWhisper = new LocalWhisperConfig
                    {
                        ModelPath = "/config-path/ggml-base.bin"
                    }
                }
            };

            var options = LocalWhisperOptions.Resolve(config);

            Assert.Equal("/custom/path/ggml-medium.bin", options.ModelPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void ResolveLanguageDefaultsToAuto()
    {
        const string envVar = "ZAKIRA_REPLAY_WHISPER_LANGUAGE";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            var options = LocalWhisperOptions.Resolve(new ReplayConfig());

            Assert.Equal("auto", options.Language);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public async Task TranscribeAsyncThrowsCleanErrorWhenModelMissing()
    {
        using var temp = new TestTempDirectory();
        var missing = temp.GetPath("ggml-does-not-exist.bin");
        var provider = new LocalWhisperTranscriptionProvider(new LocalWhisperOptions(missing, "auto", null));

        // A fake audio file is enough — the provider must reject before opening it because
        // EnsureInitialised fires first.
        var audio = temp.GetPath("audio.wav");
        await File.WriteAllBytesAsync(audio, [0], CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.TranscribeAsync(audio, CancellationToken.None));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deps install whisper-model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsyncThrowsWhenModelPathIsUnset()
    {
        var provider = new LocalWhisperTranscriptionProvider(new LocalWhisperOptions(ModelPath: null, Language: "auto", Threads: null));

        using var temp = new TestTempDirectory();
        var audio = temp.GetPath("audio.wav");
        await File.WriteAllBytesAsync(audio, [0], CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ReplayException>(
            () => provider.TranscribeAsync(audio, CancellationToken.None));

        Assert.Contains("model path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendSegmentLineFormatsTimestampedMarkdown()
    {
        // Contract: the provider must emit lines ChunkedTranscriptionService can parse —
        // `**[mm:ss - mm:ss]** text` — so timestamp shifting across chunks keeps working.
        var builder = new StringBuilder();
        LocalWhisperTranscriptionProvider.AppendSegmentLine(builder, new SegmentData(
            text: "Hello world.",
            start: TimeSpan.FromSeconds(5),
            end: TimeSpan.FromSeconds(10.5),
            minProbability: 0,
            maxProbability: 0,
            probability: 0,
            noSpeechProbability: 0,
            language: "en",
            tokens: []));

        Assert.Equal("**[00:05 - 00:10]** Hello world." + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void AppendSegmentLineFormatsHoursMinutesSecondsForLongAudio()
    {
        var builder = new StringBuilder();
        LocalWhisperTranscriptionProvider.AppendSegmentLine(builder, new SegmentData(
            text: "Deep into the talk.",
            start: TimeSpan.FromMinutes(75),                  // 01:15:00
            end: TimeSpan.FromMinutes(75).Add(TimeSpan.FromSeconds(4)), // 01:15:04
            minProbability: 0,
            maxProbability: 0,
            probability: 0,
            noSpeechProbability: 0,
            language: "en",
            tokens: []));

        Assert.Equal("**[01:15:00 - 01:15:04]** Deep into the talk." + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void AppendSegmentLineSkipsEmptyText()
    {
        var builder = new StringBuilder();
        LocalWhisperTranscriptionProvider.AppendSegmentLine(builder, new SegmentData(
            text: "   ",
            start: TimeSpan.Zero,
            end: TimeSpan.FromSeconds(1),
            minProbability: 0,
            maxProbability: 0,
            probability: 0,
            noSpeechProbability: 0,
            language: "en",
            tokens: []));

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void AppendSegmentLineClampsNegativeStartsToZero()
    {
        // Whisper rarely emits negative timestamps but it can happen on the very first segment
        // when language-detection consumes prefix audio. We clamp to zero so downstream parsers
        // never see a "-00:00:01" string they can't shift.
        var builder = new StringBuilder();
        LocalWhisperTranscriptionProvider.AppendSegmentLine(builder, new SegmentData(
            text: "Edge case.",
            start: TimeSpan.FromSeconds(-0.5),
            end: TimeSpan.FromSeconds(2),
            minProbability: 0,
            maxProbability: 0,
            probability: 0,
            noSpeechProbability: 0,
            language: "en",
            tokens: []));

        Assert.Equal("**[00:00 - 00:02]** Edge case." + Environment.NewLine, builder.ToString());
    }
}
