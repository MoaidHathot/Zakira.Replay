using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zakira.Replay.Core;

public sealed class PortableDependencyInstaller
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Ffprobe = "ffprobe";
    public const string Onnx = "onnx";
    public const string Ocr = "ocr";
    public const string WhisperModel = "whisper-model";
    public const string Diarization = "diarization";
    public const string Vision = "vision";
    public const string All = "all";

    // `deps install copilot-cli` target token vs. the on-disk executable name. Mirrors how the
    // Ffmpeg target yields two executables (ffmpeg + ffprobe): the target is "copilot-cli" but the
    // resolved binary is "copilot" (copilot.exe on Windows) so it also matches a PATH-installed CLI.
    public const string CopilotCli = "copilot-cli";
    public const string Copilot = "copilot";

    // RapidOCR PP-OCRv5 latin model files (matches RapidOcrNet defaults; see https://github.com/BobLd/RapidOcrNet).
    public const string OcrDetectionModelFile = "ch_PP-OCRv5_det_mobile.onnx";
    public const string OcrClassificationModelFile = "ch_ppocr_mobile_v2.0_cls_mobile.onnx";
    public const string OcrRecognitionModelFile = "latin_PP-OCRv5_rec_mobile.onnx";
    public const string OcrDictionaryFile = "ppocrv5_latin_dict.txt";

    // Diarization model files. pyannote-segmentation-3.0 ONNX is mirrored on Hugging Face as a
    // flat .onnx download (saves us implementing a tar.bz2 decoder), and the 3D-Speaker
    // ERes2NetV2 embedding extractor lives directly on the sherpa-onnx GitHub release as .onnx.
    public const string DiarizationSegmentationFile = "pyannote-segmentation-3-0.onnx";
    public const string DiarizationEmbeddingFile = "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    private const string YtDlpReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const string YtDlpLinuxX64Url = YtDlpReleaseUrl + "yt-dlp_linux";
    private const string YtDlpLinuxArm64Url = YtDlpReleaseUrl + "yt-dlp_linux_aarch64";
    private const string YtDlpMacOsUrl = YtDlpReleaseUrl + "yt-dlp_macos";
    private const string FfmpegWindowsX64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    // GitHub Copilot CLI (native, self-contained "copilot"/"copilot.exe"). Published per-platform on
    // the public npm registry as @github/copilot-<platform>, which is exactly where the SDK's own
    // build targets fetch it. We download it on demand instead of bundling the ~52 MB binary. The
    // version is surfaced from the referenced GitHub.Copilot.SDK via assembly metadata (see
    // Zakira.Replay.csproj) so the download tracks the SDK; DefaultCopilotCliVersion is a fallback.
    private const string CopilotCliNpmRegistry = "https://registry.npmjs.org";
    private const string DefaultCopilotCliVersion = "1.0.36-0";

    // RapidAI's RapidOCR model store on ModelScope; same SHA-pinned tag the upstream Python package uses.
    private const string RapidOcrModelBaseUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0";

    // ggml whisper.cpp model store on Hugging Face. Honours HF_TOKEN if set (rate-limit relief).
    private const string WhisperModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    // Diarization model sources.
    private const string DiarizationSegmentationUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx?download=true";
    private const string DiarizationEmbeddingUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    // Vision model sources. CLIP ViT-B/32 quantized ONNX from Xenova's community export (the same
    // one transformers.js ships). Tracking the `main` branch to follow upstream fixes; the
    // verification side is a load-time inference check in LocalOnnxVisionProvider rather than
    // a content-hash comparison so silent upstream changes are detected by behaviour rather
    // than by hash failures users can't act on.
    private const string ClipVisionEncoderUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx";
    private const string ClipTextEncoderUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model_quantized.onnx";
    private const string ClipTokenizerVocabUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/vocab.json";
    private const string ClipTokenizerMergesUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/merges.txt";

    private readonly ReplayConfig config;
    private readonly HttpClient httpClient;
    private readonly ProcessRunner processRunner;

    public PortableDependencyInstaller(ReplayConfig? config = null, HttpClient? httpClient = null)
        : this(config, httpClient, new ProcessRunner()) { }

    internal PortableDependencyInstaller(ReplayConfig? config, HttpClient? httpClient, ProcessRunner processRunner)
    {
        this.config = config ?? new ConfigStore().Load();
        this.httpClient = httpClient ?? new HttpClient();
        this.processRunner = processRunner;
        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"Zakira.Replay/{ReplayVersion.Current}");
        }
    }

    public PortableDependencyLayout Layout => new(
        GetPortableDirectory(config),
        GetOnnxModelDirectory(config),
        GetOcrModelDirectory(config),
        GetWhisperModelDirectory(config),
        GetDiarizationModelDirectory(config),
        GetVisionModelDirectory(config));

    public string GetPortableExecutablePath(string executableName)
    {
        var portable = GetPortableDirectory(config);
        var fileName = GetExecutableFileName(executableName);
        var flatPath = Path.Combine(portable, fileName);
        if (OperatingSystem.IsWindows() && fileName == "yt-dlp.exe" && GetWindowsYtDlpAsset(RuntimeInformation.OSArchitecture) is { } asset)
        {
            // Prefer the complete directory build; a pre-existing 0.17 single-file install
            // remains usable until the next explicit install. Neither lookup nor doctor migrates it.
            var bundledPath = Path.Combine(portable, YtDlp, asset.Executable);
            return File.Exists(bundledPath) || !File.Exists(flatPath) ? bundledPath : flatPath;
        }

        return flatPath;
    }

    public string GetOnnxModelPath()
    {
        return Path.Combine(Layout.OnnxModelDirectory, "model.onnx");
    }

    public string GetOnnxVocabularyPath()
    {
        return Path.Combine(Layout.OnnxModelDirectory, "vocab.txt");
    }

    /// <summary>
    /// Returns the tokenizer file path for the currently-configured search-embedding model.
    /// For BGE / arctic / generic-BERT models this is the same as
    /// <see cref="GetOnnxVocabularyPath"/>; for E5 / XLM-R it points at the SentencePiece
    /// binary model file instead.
    /// </summary>
    public string GetOnnxTokenizerPath()
    {
        var entry = ResolveSearchEmbeddingModel(config);
        return Path.Combine(Layout.OnnxModelDirectory, entry?.TokenizerFileName ?? "vocab.txt");
    }

    /// <summary>
    /// Returns the active search-embedding model entry from the registry, or null when the
    /// user has configured an arbitrary <c>search.onnx.model</c> id that's not in
    /// <see cref="KnownSearchEmbeddingModels"/>.
    /// </summary>
    public KnownSearchEmbeddingModel? GetActiveSearchEmbeddingModel()
    {
        return ResolveSearchEmbeddingModel(config);
    }

    public string GetOcrDetectionModelPath() => Path.Combine(Layout.OcrModelDirectory, OcrDetectionModelFile);

    public string GetOcrClassificationModelPath() => Path.Combine(Layout.OcrModelDirectory, OcrClassificationModelFile);

    public string GetOcrRecognitionModelPath() => Path.Combine(Layout.OcrModelDirectory, OcrRecognitionModelFile);

    public string GetOcrDictionaryPath() => Path.Combine(Layout.OcrModelDirectory, OcrDictionaryFile);

    public string GetWhisperModelPath(string? modelSize = null)
    {
        var fileName = LocalWhisperOptions.BuildModelFileName(modelSize ?? LocalWhisperOptions.DefaultModelSize);
        return Path.Combine(Layout.WhisperModelDirectory, fileName);
    }

    public string GetDiarizationSegmentationPath() => Path.Combine(Layout.DiarizationModelDirectory, DiarizationSegmentationFile);

    public string GetDiarizationEmbeddingPath() => Path.Combine(Layout.DiarizationModelDirectory, DiarizationEmbeddingFile);

    public string GetVisionClipImageEncoderPath() => Path.Combine(Layout.VisionModelDirectory, LocalVisionOptions.ClipImageEncoderFile);

    public string GetVisionClipTextEncoderPath() => Path.Combine(Layout.VisionModelDirectory, LocalVisionOptions.ClipTextEncoderFile);

    public string GetVisionClipKindEmbeddingsPath() => Path.Combine(Layout.VisionModelDirectory, LocalVisionOptions.ClipKindEmbeddingsFile);

    public string GetVisionClipTokenizerVocabPath() => Path.Combine(Layout.VisionModelDirectory, "clip-vocab.json");

    public string GetVisionClipTokenizerMergesPath() => Path.Combine(Layout.VisionModelDirectory, "clip-merges.txt");

    public static string GetDefaultWhisperModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "whisper");
    }

    public static string GetDefaultDiarizationModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "diarization");
    }

    public static string GetDefaultVisionModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "vision");
    }

    public static string GetDefaultPortableDirectory()
    {
        return Path.Combine(GetDefaultDataDirectory(), "portable");
    }

    public static string GetDefaultOcrModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "rapidocr-ppocrv5-latin");
    }

    public static IReadOnlyList<string> NormalizeTargets(IEnumerable<string>? targets)
    {
        var normalized = new List<string>();
        var values = targets?.Where(target => !string.IsNullOrWhiteSpace(target)).ToArray() ?? [];
        if (values.Length == 0)
        {
            values = ["media"];
        }

        foreach (var target in values)
        {
            switch (NormalizeTarget(target))
            {
                case All:
                case "dependencies":
                case "deps":
                    AddUnique(normalized, YtDlp);
                    AddUnique(normalized, Ffmpeg);
                    AddUnique(normalized, Onnx);
                    AddUnique(normalized, Ocr);
                    AddUnique(normalized, WhisperModel);
                    AddUnique(normalized, Diarization);
                    AddUnique(normalized, CopilotCli);
                    break;
                case "media":
                    AddUnique(normalized, YtDlp);
                    AddUnique(normalized, Ffmpeg);
                    break;
                case YtDlp:
                    AddUnique(normalized, YtDlp);
                    break;
                case Ffmpeg:
                case Ffprobe:
                    AddUnique(normalized, Ffmpeg);
                    break;
                case Onnx:
                case "search-onnx":
                case "model":
                case "models":
                    AddUnique(normalized, Onnx);
                    break;
                case Ocr:
                case "rapidocr":
                case "ocr-onnx":
                case "ocr-models":
                    AddUnique(normalized, Ocr);
                    break;
                case WhisperModel:
                case "whisper":
                case "stt":
                case "whisper-models":
                case "ggml":
                    AddUnique(normalized, WhisperModel);
                    break;
                case Diarization:
                case "diarize":
                case "speaker":
                case "speakers":
                case "speaker-diarization":
                case "sherpa-onnx":
                case "sherpa":
                    AddUnique(normalized, Diarization);
                    break;
                case Vision:
                case "vision-models":
                case "clip":
                case "clip-blip":
                case "local-vision":
                    AddUnique(normalized, Vision);
                    break;
                case CopilotCli:
                case Copilot:
                case "gh-copilot":
                case "github-copilot":
                case "copilot-exe":
                    AddUnique(normalized, CopilotCli);
                    break;
                default:
                    throw new ReplayException($"Unknown dependency target: {target}. Use yt-dlp, ffmpeg, ffprobe, onnx, ocr, whisper-model, diarization, vision, copilot-cli, media, or all.");
            }
        }

        return normalized;
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return await InstallAsync(targets, force, progress, cancellationToken, whisperModelSize: null, ocrLanguagePack: null, visionMode: null).ConfigureAwait(false);
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? whisperModelSize)
    {
        return await InstallAsync(targets, force, progress, cancellationToken, whisperModelSize, ocrLanguagePack: null, visionMode: null).ConfigureAwait(false);
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? whisperModelSize,
        string? ocrLanguagePack)
    {
        return await InstallAsync(targets, force, progress, cancellationToken, whisperModelSize, ocrLanguagePack, visionMode: null).ConfigureAwait(false);
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? whisperModelSize,
        string? ocrLanguagePack,
        string? visionMode)
    {
        var items = new List<PortableDependencyInstallItem>();
        var layout = Layout;
        Directory.CreateDirectory(layout.PortableDirectory);

        foreach (var target in NormalizeTargets(targets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (target)
            {
                case YtDlp:
                    items.Add(await InstallYtDlpAsync(force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case Ffmpeg:
                    items.AddRange(await InstallFfmpegAsync(force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case Onnx:
                    items.Add(await InstallOnnxAsync(force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case Ocr:
                    items.AddRange(await InstallOcrModelsAsync(ocrLanguagePack, force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case WhisperModel:
                    items.Add(await InstallWhisperModelAsync(whisperModelSize, force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case Diarization:
                    items.AddRange(await InstallDiarizationModelsAsync(force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case Vision:
                    items.AddRange(await InstallVisionModelsAsync(visionMode, force, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case CopilotCli:
                    items.Add(await InstallCopilotCliAsync(force, progress, cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        return new PortableDependencyInstallResult(items, layout.PortableDirectory, layout.OnnxModelDirectory, layout.OcrModelDirectory, layout.WhisperModelDirectory, layout.DiarizationModelDirectory, layout.VisionModelDirectory);
    }

    private async Task<PortableDependencyInstallItem> InstallYtDlpAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await InstallWindowsYtDlpAsync(force, progress, cancellationToken).ConfigureAwait(false);
        }

        var url = GetUnixYtDlpUrl();
        var path = GetPortableExecutablePath(YtDlp);
        var installed = await DownloadFileAsync(url, path, force, progress, cancellationToken).ConfigureAwait(false);
        SetExecutableBit(path);
        return new PortableDependencyInstallItem(YtDlp, path, installed, url, installed ? "downloaded" : "already exists");
    }

    private async Task<PortableDependencyInstallItem> InstallWindowsYtDlpAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // yt-dlp runs out of process: even an emulated Replay should download the native OS build.
        var asset = GetWindowsYtDlpAsset(RuntimeInformation.OSArchitecture)
            ?? throw new ReplayException($"Portable yt-dlp download is not supported on Windows {RuntimeInformation.OSArchitecture}. Configure yt-dlp.path with a compatible executable.");
        var url = YtDlpReleaseUrl + asset.Archive;
        var portable = GetPortableDirectory(config);
        var destination = Path.Combine(portable, YtDlp);
        var path = Path.Combine(destination, asset.Executable);
        if (!force && File.Exists(path))
        {
            var existingVersion = await VerifyVersionAsync().ConfigureAwait(false);
            return new PortableDependencyInstallItem(YtDlp, path, false, url, $"already exists ({existingVersion})");
        }

        var work = Directory.CreateDirectory(Path.Combine(portable, $".yt-dlp-{Guid.NewGuid():N}")).FullName;
        var archive = Path.Combine(work, asset.Archive);
        var bundle = Path.Combine(work, "bundle");
        var backup = work + ".previous";
        try
        {
            await DownloadFileAsync(url, archive, force: true, progress, cancellationToken).ConfigureAwait(false);
            await ZipFile.ExtractToDirectoryAsync(archive, bundle, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(bundle, asset.Executable)))
            {
                throw new ReplayException($"Downloaded {asset.Archive} did not contain {asset.Executable}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var replacing = Directory.Exists(destination);
            if (replacing)
            {
                Directory.Move(destination, backup);
            }

            // Probe from the final path: application control can make a different decision in
            // a staging directory. Restore the entire previous bundle if activation or probing fails.
            var activated = false;
            string version;
            try
            {
                Directory.Move(bundle, destination);
                activated = true;
                version = await VerifyVersionAsync().ConfigureAwait(false);
            }
            catch
            {
                if (activated) Directory.Move(destination, bundle);
                if (replacing) Directory.Move(backup, destination);
                throw;
            }

            if (replacing) Directory.Delete(backup, recursive: true);
            return new PortableDependencyInstallItem(YtDlp, path, true, url, $"downloaded ({version})");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        async Task<string> VerifyVersionAsync()
        {
            var timeout = TimeoutsConfig.Resolve(config.Timeouts.DependencyCheckSeconds, 60);
            var (error, version) = await DependencyVersionProbe.RunAsync(YtDlp, path, processRunner, timeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                var advice = DoctorAdvice.ForRunnableError(YtDlp, error) ?? "To replace the bundle, run `zakira-replay deps install yt-dlp --force`.";
                throw new ReplayException($"Portable yt-dlp at '{path}' failed `--version`: {error}. {advice}");
            }

            return version!;
        }
    }

    private async Task<IReadOnlyList<PortableDependencyInstallItem>> InstallFfmpegAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new ReplayException("Portable ffmpeg download is currently implemented for Windows x64. Install ffmpeg manually or configure ffmpeg.path and ffprobe.path.");
        }

        var ffmpegPath = GetPortableExecutablePath(Ffmpeg);
        var ffprobePath = GetPortableExecutablePath(Ffprobe);
        if (!force && File.Exists(ffmpegPath) && File.Exists(ffprobePath))
        {
            return
            [
                new PortableDependencyInstallItem(Ffmpeg, ffmpegPath, false, FfmpegWindowsX64Url, "already exists"),
                new PortableDependencyInstallItem(Ffprobe, ffprobePath, false, FfmpegWindowsX64Url, "already exists")
            ];
        }

        Directory.CreateDirectory(Layout.PortableDirectory);
        var downloadDirectory = Path.Combine(Layout.PortableDirectory, ".downloads");
        Directory.CreateDirectory(downloadDirectory);
        var archivePath = Path.Combine(downloadDirectory, "ffmpeg.zip");
        await DownloadFileAsync(FfmpegWindowsX64Url, archivePath, force: true, progress, cancellationToken).ConfigureAwait(false);
        ExtractExecutableFromZip(archivePath, "ffmpeg.exe", ffmpegPath);
        ExtractExecutableFromZip(archivePath, "ffprobe.exe", ffprobePath);

        return
        [
            new PortableDependencyInstallItem(Ffmpeg, ffmpegPath, true, FfmpegWindowsX64Url, "downloaded"),
            new PortableDependencyInstallItem(Ffprobe, ffprobePath, true, FfmpegWindowsX64Url, "downloaded")
        ];
    }

    private async Task<PortableDependencyInstallItem> InstallCopilotCliAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var url = GetCopilotCliUrl();
        var destinationPath = GetPortableExecutablePath(Copilot);
        if (!force && File.Exists(destinationPath))
        {
            return new PortableDependencyInstallItem(Copilot, destinationPath, false, url, "already exists");
        }

        Directory.CreateDirectory(Layout.PortableDirectory);
        var downloadDirectory = Path.Combine(Layout.PortableDirectory, ".downloads");
        Directory.CreateDirectory(downloadDirectory);
        var archivePath = Path.Combine(downloadDirectory, "copilot-cli.tgz");
        await DownloadFileAsync(url, archivePath, force: true, progress, cancellationToken).ConfigureAwait(false);

        // The npm tarball nests everything under `package/`; the CLI is a single self-contained
        // binary (copilot.exe on Windows, copilot elsewhere) - no sibling files are required, which
        // is why the SDK's own targets copy only that one file too.
        ExtractExecutableFromTarGz(archivePath, GetExecutableFileName(Copilot), destinationPath);
        SetExecutableBit(destinationPath);
        return new PortableDependencyInstallItem(Copilot, destinationPath, true, url, "downloaded");
    }

    private async Task<PortableDependencyInstallItem> InstallOnnxAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // Resolve the active known model entry. Custom user-supplied models (no registry
        // hit) cannot be auto-downloaded — for those the user has already pointed
        // search.onnx.modelPath / tokenizerPath at their own files.
        var entry = ResolveSearchEmbeddingModel(config)
            ?? throw new ReplayException(
                $"search.onnx.model='{config.Search.Onnx.Model}' is not a known model id ({string.Join(", ", KnownSearchEmbeddingModels.Ids)}). " +
                "Either choose a known id or set search.onnx.modelPath / tokenizerPath explicitly and skip `deps install onnx`.");

        var modelDirectory = Layout.OnnxModelDirectory;
        Directory.CreateDirectory(modelDirectory);

        var installed = false;
        foreach (var file in entry.Files)
        {
            var url = $"{entry.RepositoryBaseUrl}/{file.RemotePath}?download=true";
            var localPath = Path.Combine(modelDirectory, file.LocalName);
            installed |= await DownloadFileAsync(url, localPath, force, progress, cancellationToken).ConfigureAwait(false);
        }

        return new PortableDependencyInstallItem(Onnx, modelDirectory, installed, entry.RepositoryBaseUrl, installed ? $"downloaded {entry.Id}" : $"already exists ({entry.Id})");
    }

    private async Task<IReadOnlyList<PortableDependencyInstallItem>> InstallOcrModelsAsync(string? languagePack, bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelDirectory = Layout.OcrModelDirectory;
        Directory.CreateDirectory(modelDirectory);

        // Resolve which language pack to install. Order:
        //   1. CLI/explicit override (`languagePack` arg) — supports aliases.
        //   2. Persistent config (`ocr.local.languagePack`).
        //   3. Latin default for backwards compat with 0.5.x and earlier installs.
        var requested = string.IsNullOrWhiteSpace(languagePack)
            ? (config.Ocr.Local.LanguagePack ?? OcrLanguagePacks.Latin)
            : languagePack;

        if (!OcrLanguagePacks.TryGet(requested, out var pack))
        {
            throw new ReplayException(
                $"Unknown OCR language pack: '{requested}'. Known packs: {string.Join(", ", OcrLanguagePacks.All.Select(p => p.Name))}.");
        }

        // Detection + classification are shared across packs — download once, reuse forever.
        // The recognition model + dictionary are pack-specific.
        var files = new[]
        {
            new OnnxDownloadFile(OcrDetectionModelFile, $"{RapidOcrModelBaseUrl}/onnx/PP-OCRv5/det/{OcrDetectionModelFile}"),
            new OnnxDownloadFile(OcrClassificationModelFile, $"{RapidOcrModelBaseUrl}/onnx/PP-OCRv4/cls/{OcrClassificationModelFile}"),
            new OnnxDownloadFile(pack.RecognitionModelFile, $"{RapidOcrModelBaseUrl}/onnx/PP-OCRv5/rec/{pack.RecognitionModelFile}"),
            new OnnxDownloadFile(pack.DictionaryFile, $"{RapidOcrModelBaseUrl}/paddle/PP-OCRv5/rec/{pack.RecognitionModelDirectory}/{pack.DictionaryFile}")
        };

        var items = new List<PortableDependencyInstallItem>(files.Length);
        foreach (var file in files)
        {
            var destination = Path.Combine(modelDirectory, file.Name);
            var installed = await DownloadFileAsync(file.Url, destination, force, progress, cancellationToken).ConfigureAwait(false);
            items.Add(new PortableDependencyInstallItem(Ocr, destination, installed, file.Url, installed ? $"downloaded ({pack.Name})" : $"already exists ({pack.Name})"));
        }

        return items;
    }

    private async Task<PortableDependencyInstallItem> InstallWhisperModelAsync(string? modelSize, bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var size = LocalWhisperOptions.NormalizeModelSize(string.IsNullOrWhiteSpace(modelSize)
            ? (config.Llm.LocalWhisper.ModelSize ?? LocalWhisperOptions.DefaultModelSize)
            : modelSize);

        if (!LocalWhisperOptions.SupportedModelSizes.Contains(size, StringComparer.OrdinalIgnoreCase))
        {
            throw new ReplayException(
                $"Unknown Whisper model size: '{modelSize ?? size}'. Use one of: {string.Join(", ", LocalWhisperOptions.SupportedModelSizes)}.");
        }

        var modelDirectory = Layout.WhisperModelDirectory;
        Directory.CreateDirectory(modelDirectory);
        var fileName = LocalWhisperOptions.BuildModelFileName(size);
        var url = $"{WhisperModelBaseUrl}/{Uri.EscapeDataString(fileName)}?download=true";
        var destination = Path.Combine(modelDirectory, fileName);

        // Hugging Face throttles unauthenticated downloads of large model files. Pick up an
        // optional HF_TOKEN exactly like Whisper.net's own downloader does so users can lift the
        // rate limit by simply exporting `HF_TOKEN=hf_xxx` in their shell.
        var hfToken = Environment.GetEnvironmentVariable("HF_TOKEN");
        var installed = string.IsNullOrWhiteSpace(hfToken)
            ? await DownloadFileAsync(url, destination, force, progress, cancellationToken).ConfigureAwait(false)
            : await DownloadFileAsync(url, destination, force, progress, cancellationToken, authorizationHeader: ("Authorization", $"Bearer {hfToken}")).ConfigureAwait(false);

        return new PortableDependencyInstallItem(WhisperModel, destination, installed, url, installed ? $"downloaded ({size})" : $"already exists ({size})");
    }

    private async Task<IReadOnlyList<PortableDependencyInstallItem>> InstallDiarizationModelsAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelDirectory = Layout.DiarizationModelDirectory;
        Directory.CreateDirectory(modelDirectory);

        var files = new (string Name, string Url)[]
        {
            (DiarizationSegmentationFile, DiarizationSegmentationUrl),
            (DiarizationEmbeddingFile, DiarizationEmbeddingUrl)
        };

        var items = new List<PortableDependencyInstallItem>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(modelDirectory, file.Name);
            var installed = await DownloadFileAsync(file.Url, destination, force, progress, cancellationToken).ConfigureAwait(false);
            items.Add(new PortableDependencyInstallItem(Diarization, destination, installed, file.Url, installed ? "downloaded" : "already exists"));
        }

        return items;
    }

    /// <summary>
    /// Installs ONNX model files for the local (no-LLM) vision provider. In v1 only the
    /// <c>clip</c> mode is auto-downloadable: vision encoder, text encoder, and the CLIP BPE
    /// tokenizer (vocab.json + merges.txt) sourced from the community-maintained
    /// <c>Xenova/clip-vit-base-patch32</c> ONNX export on Hugging Face. The <c>clip-blip</c>
    /// mode currently emits a friendly "configure paths manually" item — no public BLIP ONNX
    /// has been validated against <see cref="LocalOnnxVisionProvider"/> in this release.
    /// </summary>
    private async Task<IReadOnlyList<PortableDependencyInstallItem>> InstallVisionModelsAsync(string? mode, bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelDirectory = Layout.VisionModelDirectory;
        Directory.CreateDirectory(modelDirectory);

        var resolvedMode = VisionProviderFactory.NormalizeMode(string.IsNullOrWhiteSpace(mode) ? "clip" : mode);
        var items = new List<PortableDependencyInstallItem>();

        if (resolvedMode == LocalVisionMode.Heuristic)
        {
            items.Add(new PortableDependencyInstallItem(Vision, modelDirectory, false, string.Empty, "heuristic mode requires no model files."));
            return items;
        }

        // CLIP image encoder + text encoder + BPE tokenizer files. Same set for `clip` and
        // `clip-blip` modes — the BLIP-specific files are emitted as a stub below.
        var clipFiles = new (string Name, string Url)[]
        {
            (LocalVisionOptions.ClipImageEncoderFile, ClipVisionEncoderUrl),
            (LocalVisionOptions.ClipTextEncoderFile, ClipTextEncoderUrl),
            ("clip-vocab.json", ClipTokenizerVocabUrl),
            ("clip-merges.txt", ClipTokenizerMergesUrl)
        };

        foreach (var file in clipFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(modelDirectory, file.Name);
            var installed = await DownloadFileAsync(file.Url, destination, force, progress, cancellationToken).ConfigureAwait(false);
            items.Add(new PortableDependencyInstallItem(Vision, destination, installed, file.Url, installed ? "downloaded" : "already exists"));
        }

        if (resolvedMode == LocalVisionMode.ClipCaption)
        {
            // Download Florence-2 ONNX from onnx-community. Track main branch.
            var quantization = LocalVisionOptions.NormalizeQuantization(
                Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_FLORENCE_QUANTIZATION")
                ?? config.Vision.Local.FlorenceQuantization);
            var suffix = LocalVisionOptions.QuantizationSuffix(quantization);
            var florenceBaseUrl = "https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/main";

            // Florence-2 graph + tokenizer file set.
            // - vision_encoder: image (768x768 RGB) -> visual features [1, 577, 768]
            // - embed_tokens: token ids -> embeddings (used by encoder + decoder)
            // - encoder_model: fused image+text embeddings -> encoder hidden states
            // - decoder_model_merged: KV-cache-enabled autoregressive decoder
            //   (chosen over decoder_model.onnx for production-grade perf per v2 spec)
            var florenceFiles = new (string LocalName, string Url)[]
            {
                (LocalVisionOptions.FlorenceVisionEncoderFile, $"{florenceBaseUrl}/onnx/vision_encoder{suffix}.onnx"),
                (LocalVisionOptions.FlorenceEncoderFile,        $"{florenceBaseUrl}/onnx/encoder_model{suffix}.onnx"),
                (LocalVisionOptions.FlorenceDecoderFile,        $"{florenceBaseUrl}/onnx/decoder_model_merged{suffix}.onnx"),
                (LocalVisionOptions.FlorenceEmbedTokensFile,    $"{florenceBaseUrl}/onnx/embed_tokens{suffix}.onnx"),
                (LocalVisionOptions.FlorenceVocabFile,          $"{florenceBaseUrl}/vocab.json"),
                (LocalVisionOptions.FlorenceMergesFile,         $"{florenceBaseUrl}/merges.txt"),
                (LocalVisionOptions.FlorenceAddedTokensFile,    $"{florenceBaseUrl}/added_tokens.json")
            };

            foreach (var file in florenceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(modelDirectory, file.LocalName);
                var installed = await DownloadFileAsync(file.Url, destination, force, progress, cancellationToken).ConfigureAwait(false);
                items.Add(new PortableDependencyInstallItem(Vision, destination, installed, file.Url, installed ? $"downloaded ({quantization})" : $"already exists ({quantization})"));
            }
        }

        return items;
    }

    private static string GetVisionModelDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_VISION_MODEL_DIRECTORY"),
            config.Vision.Local.ModelDirectory,
            GetDefaultVisionModelDirectory())!));
    }

    private async Task<bool> DownloadFileAsync(string url, string destinationPath, bool force, IProgress<string>? progress, CancellationToken cancellationToken, (string Name, string Value)? authorizationHeader = null)
    {
        if (!force && File.Exists(destinationPath))
        {
            progress?.Report($"Exists: {destinationPath}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        progress?.Report($"Downloading: {url}");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        if (authorizationHeader is { } header)
        {
            requestMessage.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tempPath = destinationPath + ".tmp";
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, destinationPath, overwrite: true);
        progress?.Report($"Wrote: {destinationPath}");
        return true;
    }

    private static void ExtractExecutableFromZip(string archivePath, string executableName, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(item => item.FullName.EndsWith("/bin/" + executableName, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(item => Path.GetFileName(item.FullName).Equals(executableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ReplayException($"Downloaded ffmpeg archive did not contain {executableName}.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    /// <summary>
    /// Extracts a single executable entry from a gzip-compressed tarball (npm <c>.tgz</c>). npm
    /// packs everything under a top-level <c>package/</c> directory, so we match by trailing path
    /// segment or file name. Uses only the BCL (<see cref="GZipStream"/> + <see cref="TarReader"/>),
    /// so no third-party archive dependency is needed.
    /// </summary>
    private static void ExtractExecutableFromTarGz(string archivePath, string executableName, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var fileStream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var entryName = entry.Name.Replace('\\', '/');
            if (entryName.EndsWith('/' + executableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(entryName), executableName, StringComparison.OrdinalIgnoreCase))
            {
                entry.ExtractToFile(destinationPath, overwrite: true);
                return;
            }
        }

        throw new ReplayException($"Downloaded Copilot CLI archive did not contain {executableName}.");
    }

    internal static (string Archive, string Executable)? GetWindowsYtDlpAsset(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => ("yt-dlp_win.zip", "yt-dlp.exe"),
            Architecture.Arm64 => ("yt-dlp_win_arm64.zip", "yt-dlp_arm64.exe"),
            Architecture.X86 => ("yt-dlp_win_x86.zip", "yt-dlp_x86.exe"),
            _ => null
        };

    private static string GetUnixYtDlpUrl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? YtDlpLinuxArm64Url : YtDlpLinuxX64Url;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return YtDlpMacOsUrl;
        }

        throw new ReplayException("Portable yt-dlp download is not supported on this operating system. Install yt-dlp manually or configure yt-dlp.path.");
    }

    private static string GetCopilotCliUrl()
    {
        var platform = GetCopilotCliPlatform();
        var version = GetCopilotCliVersion();
        return $"{CopilotCliNpmRegistry}/@github/copilot-{platform}/-/copilot-{platform}-{version}.tgz";
    }

    // Maps the current OS/arch to the npm platform token used by the @github/copilot-<platform>
    // packages (win32-x64, win32-arm64, linux-x64, linux-arm64, darwin-x64, darwin-arm64) - the
    // same mapping GitHub.Copilot.SDK.targets uses.
    private static string GetCopilotCliPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new ReplayException($"Portable GitHub Copilot CLI download is not supported on {RuntimeInformation.ProcessArchitecture}. Install the Copilot CLI manually or configure copilot-cli.path.")
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"win32-{arch}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"linux-{arch}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"darwin-{arch}";
        }

        throw new ReplayException("Portable GitHub Copilot CLI download is not supported on this operating system. Install the Copilot CLI manually or configure copilot-cli.path.");
    }

    // The CLI version tracked by the referenced GitHub.Copilot.SDK, surfaced as assembly metadata
    // by the csproj so the on-demand download and the compiled-in SDK never drift. Falls back to a
    // pinned default if the metadata is unavailable (e.g. in a unit-test host).
    private static string GetCopilotCliVersion()
    {
        var version = typeof(PortableDependencyInstaller).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "CopilotCliVersion", StringComparison.OrdinalIgnoreCase))?
            .Value;
        return string.IsNullOrWhiteSpace(version) ? DefaultCopilotCliVersion : version;
    }

    private static string GetPortableDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY"),
            config.Dependencies.PortableDirectory,
            GetDefaultPortableDirectory())!));
    }

    private static string GetOnnxModelDirectory(ReplayConfig config)
    {
        // Explicit overrides win first; otherwise the directory follows the active
        // search-embedding model id so a user who flips `search.onnx.model` between
        // bge-small-en-v1.5 / snowflake-arctic-embed-s / multilingual-e5-small can keep
        // all three model bundles side-by-side under `<portable>/models/<id>/`.
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY"),
            config.Search.Onnx.ModelDirectory,
            GetActiveSearchModelDirectory(config))!));
    }

    private static string GetActiveSearchModelDirectory(ReplayConfig config)
    {
        var entry = ResolveSearchEmbeddingModel(config);
        var directoryName = entry?.DirectoryName ?? KnownSearchEmbeddingModels.DefaultModel;
        return Path.Combine(GetDefaultPortableDirectory(), "models", directoryName);
    }

    private static KnownSearchEmbeddingModel? ResolveSearchEmbeddingModel(ReplayConfig config)
    {
        var id = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL"),
            config.Search.Onnx.Model,
            KnownSearchEmbeddingModels.DefaultModel);
        return KnownSearchEmbeddingModels.TryGet(id, out var entry) ? entry : null;
    }

    private static string GetOcrModelDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_OCR_MODEL_DIRECTORY"),
            config.Ocr.Local.ModelDirectory,
            GetDefaultOcrModelDirectory())!));
    }

    private static string GetWhisperModelDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_WHISPER_MODEL_DIRECTORY"),
            config.Llm.LocalWhisper.ModelPath is { } modelPath && !string.IsNullOrWhiteSpace(modelPath)
                ? Path.GetDirectoryName(modelPath)
                : null,
            GetDefaultWhisperModelDirectory())!));
    }

    private static string GetDiarizationModelDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_DIARIZATION_MODEL_DIRECTORY"),
            config.Diarization.ModelDirectory,
            GetDefaultDiarizationModelDirectory())!));
    }

    private static string GetExecutableFileName(string executableName)
    {
        var normalized = NormalizeTarget(executableName);
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Path.HasExtension(normalized)
            ? normalized + ".exe"
            : normalized;
    }

    private static string GetDefaultDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Zakira.Replay");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Zakira.Replay");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Zakira.Replay")
            : Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(xdgDataHome)), "Zakira.Replay");
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static string NormalizeTarget(string target)
    {
        return target.Trim().ToLowerInvariant().Replace('_', '-');
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static void SetExecutableBit(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed record OnnxDownloadFile(string Name, string Url);
}

public sealed record PortableDependencyLayout(string PortableDirectory, string OnnxModelDirectory, string OcrModelDirectory, string WhisperModelDirectory, string DiarizationModelDirectory, string VisionModelDirectory);

public sealed record PortableDependencyInstallResult(
    IReadOnlyList<PortableDependencyInstallItem> Items,
    string PortableDirectory,
    string OnnxModelDirectory,
    string OcrModelDirectory,
    string WhisperModelDirectory,
    string DiarizationModelDirectory,
    string VisionModelDirectory);

public sealed record PortableDependencyInstallItem(
    string Name,
    string Path,
    bool Installed,
    string Source,
    string Message);
