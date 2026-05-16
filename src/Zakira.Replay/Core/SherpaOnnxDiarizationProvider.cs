using SherpaOnnx;

namespace Zakira.Replay.Core;

/// <summary>
/// Local speaker diarization provider backed by <a href="https://github.com/k2-fsa/sherpa-onnx">sherpa-onnx</a>:
/// pyannote-segmentation 3.0 for speech activity / speaker change detection plus a 3D-Speaker
/// (or NeMo) embedding extractor for clustering. Runs entirely on the caller's machine via
/// ONNX Runtime; no network at run-time after the models are installed via
/// <c>zakira-replay deps install diarization</c>.
/// </summary>
/// <remarks>
/// The provider expects a 16 kHz mono PCM (s16le) WAV file — the same artifact
/// <see cref="AudioChunker"/> produces. <see cref="DiarizationOptions.NumSpeakers"/> is used as
/// a hard cluster count when known; otherwise <see cref="DiarizationOptions.Threshold"/>
/// controls the agglomerative clustering cutoff. The native sherpa-onnx engine is stateless
/// across runs but expensive to spin up, so we lazy-init once per provider instance and reuse
/// it for all subsequent <c>DiarizeAsync</c> calls.
/// </remarks>
public sealed class SherpaOnnxDiarizationProvider : IDiarizationProvider, IDisposable
{
    private readonly object initLock = new();
    private OfflineSpeakerDiarization? engine;
    private DiarizationOptions? engineOptions;
    private bool disposed;

    public Task<IReadOnlyList<DiarizationSegment>> DiarizeAsync(
        string audioPath,
        DiarizationOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        // sherpa-onnx's Process / ProcessWithCallback is synchronous and native-CPU-bound; run
        // on a worker so we keep the pipeline's async-await contract intact and cancellation can
        // still propagate.
        return Task.Run<IReadOnlyList<DiarizationSegment>>(
            () => DiarizeCore(audioPath, options, progress, cancellationToken),
            cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        engine?.Dispose();
        engine = null;
    }

    private IReadOnlyList<DiarizationSegment> DiarizeCore(
        string audioPath,
        DiarizationOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new ReplayException($"Diarization cannot read audio: file not found at '{audioPath}'.");
        }

        var missing = options.MissingFiles();
        if (missing.Count > 0)
        {
            throw new ReplayException(
                $"Diarization models not found. Run `zakira-replay deps install diarization` (or set `diarization.segmentationModelPath`, `diarization.embeddingModelPath`). Missing: {string.Join(", ", missing)}.");
        }

        EnsureEngine(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Read the WAV as float[] samples at engine.SampleRate. AudioChunker emits 16 kHz mono
        // PCM s16le, which matches what pyannote-segmentation-3.0 expects.
        var (samples, sampleRate) = PcmWaveReader.Read(audioPath);
        if (sampleRate != engine!.SampleRate)
        {
            throw new ReplayException(
                $"Diarization input audio must be {engine.SampleRate} Hz; got {sampleRate} Hz. " +
                "Re-extract audio with ffmpeg `-ar 16000 -ac 1` (AudioChunker does this by default).");
        }

        if (samples.Length == 0)
        {
            return [];
        }

        progress?.Report($"Diarizing {samples.Length / (double)sampleRate:F1}s of audio...");

        OfflineSpeakerDiarizationSegment[] rawSegments;
        try
        {
            // Wire a progress callback so long files give the user a heartbeat.
            var callbackProgress = progress;
            rawSegments = callbackProgress is null
                ? engine.Process(samples)
                : engine.ProcessWithCallback(
                    samples,
                    new OfflineSpeakerDiarizationProgressCallback((processed, total, _) =>
                    {
                        if (total > 0)
                        {
                            var percent = 100.0 * processed / total;
                            callbackProgress.Report($"Diarization: {percent:F0}% ({processed}/{total} chunks)");
                        }
                        return 0;
                    }),
                    IntPtr.Zero);
        }
        catch (Exception ex) when (ex is not ReplayException and not OperationCanceledException)
        {
            throw new ReplayException($"sherpa-onnx diarization failed: {ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ConvertSegments(rawSegments);
    }

    /// <summary>
    /// Translate sherpa-onnx's raw segments into the stable
    /// <see cref="DiarizationSegment"/> contract. Public for unit-testability — we exercise the
    /// merger / format guarantees without spinning up the native engine.
    /// </summary>
    public static IReadOnlyList<DiarizationSegment> ConvertSegments(OfflineSpeakerDiarizationSegment[] rawSegments)
    {
        if (rawSegments is null || rawSegments.Length == 0)
        {
            return [];
        }

        var converted = new List<DiarizationSegment>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var start = TimeSpan.FromSeconds(Math.Max(0, raw.Start));
            var endSeconds = Math.Max(raw.Start, raw.End);
            var end = TimeSpan.FromSeconds(endSeconds);
            converted.Add(new DiarizationSegment(start, end, DiarizationSegment.FormatSpeakerId(raw.Speaker)));
        }

        // sherpa-onnx returns segments in time order but defend against future changes.
        converted.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return converted;
    }

    private void EnsureEngine(DiarizationOptions options)
    {
        if (engine is not null && engineOptions == options)
        {
            return;
        }

        lock (initLock)
        {
            if (engine is not null && engineOptions == options)
            {
                return;
            }

            engine?.Dispose();
            engine = null;

            try
            {
                var config = new OfflineSpeakerDiarizationConfig
                {
                    Segmentation = new OfflineSpeakerSegmentationModelConfig
                    {
                        Pyannote = new OfflineSpeakerSegmentationPyannoteModelConfig
                        {
                            Model = options.SegmentationModelPath ?? string.Empty
                        },
                        NumThreads = options.Threads,
                        Provider = "cpu"
                    },
                    Embedding = new SpeakerEmbeddingExtractorConfig
                    {
                        Model = options.EmbeddingModelPath ?? string.Empty,
                        NumThreads = options.Threads,
                        Provider = "cpu"
                    },
                    Clustering = new FastClusteringConfig
                    {
                        NumClusters = options.NumSpeakers ?? -1,   // -1 = unknown; rely on Threshold
                        Threshold = options.Threshold ?? 0.5f
                    },
                    MinDurationOn = options.MinDurationOn,
                    MinDurationOff = options.MinDurationOff
                };

                engine = new OfflineSpeakerDiarization(config);
                engineOptions = options;
            }
            catch (Exception ex) when (ex is not ReplayException)
            {
                throw new ReplayException(
                    $"Failed to initialise sherpa-onnx diarization engine: {ex.Message}. " +
                    "Verify the segmentation and embedding ONNX files exist and the native sherpa-onnx runtime is loadable for this platform.",
                    ex);
            }
        }
    }
}

/// <summary>
/// Minimal 16-bit-PCM WAV reader. Built deliberately tiny: <see cref="AudioChunker"/> writes
/// 16 kHz mono PCM s16le, which is what pyannote-segmentation-3.0 and the 3D-Speaker embedding
/// extractor both expect. We don't ship a general-purpose decoder.
/// </summary>
internal static class PcmWaveReader
{
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44
            || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F'
            || bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
        {
            throw new ReplayException($"Diarization expects a RIFF/WAVE file; got something else at '{path}'.");
        }

        var format = (short)0;
        var channels = (short)0;
        var sampleRate = 0;
        var bitsPerSample = (short)0;
        var dataOffset = -1;
        var dataLength = 0;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            var chunkBody = offset + 8;

            switch (chunkId)
            {
                case "fmt ":
                    format = BitConverter.ToInt16(bytes, chunkBody);
                    channels = BitConverter.ToInt16(bytes, chunkBody + 2);
                    sampleRate = BitConverter.ToInt32(bytes, chunkBody + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, chunkBody + 14);
                    break;
                case "data":
                    dataOffset = chunkBody;
                    dataLength = chunkSize;
                    break;
            }

            // Chunks are word-aligned; pad odd-length chunks by one byte.
            offset = chunkBody + chunkSize + (chunkSize % 2);
            if (dataOffset >= 0)
            {
                break;
            }
        }

        if (dataOffset < 0)
        {
            throw new ReplayException($"WAV file contains no `data` chunk: '{path}'.");
        }

        if (format != 1)
        {
            throw new ReplayException($"Diarization only accepts PCM (format=1) WAV; got format={format} at '{path}'.");
        }

        if (channels != 1)
        {
            throw new ReplayException($"Diarization only accepts mono WAV; got {channels} channels at '{path}'.");
        }

        if (bitsPerSample != 16)
        {
            throw new ReplayException($"Diarization only accepts 16-bit PCM WAV; got {bitsPerSample}-bit at '{path}'.");
        }

        var availableData = Math.Min(dataLength, bytes.Length - dataOffset);
        var sampleCount = availableData / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s16 = BitConverter.ToInt16(bytes, dataOffset + i * 2);
            samples[i] = s16 / 32768.0f;
        }

        return (samples, sampleRate);
    }
}
