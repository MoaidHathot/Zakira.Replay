using System.IO.Compression;
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
    public const string All = "all";
    public const string DefaultOnnxModelFile = "model_quantized.onnx";

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

    private const string YtDlpWindowsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpLinuxX64Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";
    private const string YtDlpLinuxArm64Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64";
    private const string YtDlpMacOsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";
    private const string FfmpegWindowsX64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    private const string OnnxRepositoryBaseUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main";

    // RapidAI's RapidOCR model store on ModelScope; same SHA-pinned tag the upstream Python package uses.
    private const string RapidOcrModelBaseUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0";

    // ggml whisper.cpp model store on Hugging Face. Honours HF_TOKEN if set (rate-limit relief).
    private const string WhisperModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    // Diarization model sources.
    private const string DiarizationSegmentationUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx?download=true";
    private const string DiarizationEmbeddingUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    private readonly ReplayConfig config;
    private readonly HttpClient httpClient;

    public PortableDependencyInstaller(ReplayConfig? config = null, HttpClient? httpClient = null)
    {
        this.config = config ?? new ConfigStore().Load();
        this.httpClient = httpClient ?? new HttpClient();
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
        GetDiarizationModelDirectory(config));

    public string GetPortableExecutablePath(string executableName)
    {
        return Path.Combine(Layout.PortableDirectory, GetExecutableFileName(executableName));
    }

    public string GetOnnxModelPath()
    {
        return Path.Combine(Layout.OnnxModelDirectory, "model.onnx");
    }

    public string GetOnnxVocabularyPath()
    {
        return Path.Combine(Layout.OnnxModelDirectory, "vocab.txt");
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

    public static string GetDefaultWhisperModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "whisper");
    }

    public static string GetDefaultDiarizationModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "diarization");
    }

    public static string GetDefaultPortableDirectory()
    {
        return Path.Combine(GetDefaultDataDirectory(), "portable");
    }

    public static string GetDefaultOnnxModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "all-MiniLM-L6-v2");
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
                default:
                    throw new ReplayException($"Unknown dependency target: {target}. Use yt-dlp, ffmpeg, ffprobe, onnx, ocr, whisper-model, diarization, media, or all.");
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
        return await InstallAsync(targets, force, progress, cancellationToken, whisperModelSize: null, ocrLanguagePack: null).ConfigureAwait(false);
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? whisperModelSize)
    {
        return await InstallAsync(targets, force, progress, cancellationToken, whisperModelSize, ocrLanguagePack: null).ConfigureAwait(false);
    }

    public async Task<PortableDependencyInstallResult> InstallAsync(
        IEnumerable<string>? targets,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? whisperModelSize,
        string? ocrLanguagePack)
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
            }
        }

        return new PortableDependencyInstallResult(items, layout.PortableDirectory, layout.OnnxModelDirectory, layout.OcrModelDirectory, layout.WhisperModelDirectory, layout.DiarizationModelDirectory);
    }

    private async Task<PortableDependencyInstallItem> InstallYtDlpAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var url = GetYtDlpUrl();
        var path = GetPortableExecutablePath(YtDlp);
        var installed = await DownloadFileAsync(url, path, force, progress, cancellationToken).ConfigureAwait(false);
        SetExecutableBit(path);
        return new PortableDependencyInstallItem(YtDlp, path, installed, url, installed ? "downloaded" : "already exists");
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

    private async Task<PortableDependencyInstallItem> InstallOnnxAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var modelDirectory = Layout.OnnxModelDirectory;
        Directory.CreateDirectory(modelDirectory);
        var modelFile = GetOnnxModelFile(config);
        var files = new[]
        {
            new OnnxDownloadFile("model.onnx", $"{OnnxRepositoryBaseUrl}/onnx/{Uri.EscapeDataString(modelFile)}?download=true"),
            new OnnxDownloadFile("vocab.txt", $"{OnnxRepositoryBaseUrl}/vocab.txt?download=true"),
            new OnnxDownloadFile("config.json", $"{OnnxRepositoryBaseUrl}/config.json?download=true"),
            new OnnxDownloadFile("tokenizer_config.json", $"{OnnxRepositoryBaseUrl}/tokenizer_config.json?download=true")
        };

        var installed = false;
        foreach (var file in files)
        {
            installed |= await DownloadFileAsync(file.Url, Path.Combine(modelDirectory, file.Name), force, progress, cancellationToken).ConfigureAwait(false);
        }

        return new PortableDependencyInstallItem(Onnx, modelDirectory, installed, OnnxRepositoryBaseUrl, installed ? "downloaded" : "already exists");
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

    private static string GetYtDlpUrl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return YtDlpWindowsUrl;
        }

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

    private static string GetPortableDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_PORTABLE_DIRECTORY"),
            config.Dependencies.PortableDirectory,
            GetDefaultPortableDirectory())!));
    }

    private static string GetOnnxModelDirectory(ReplayConfig config)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_DIRECTORY"),
            config.Search.Onnx.ModelDirectory,
            GetDefaultOnnxModelDirectory())!));
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

    private static string GetOnnxModelFile(ReplayConfig config)
    {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_ONNX_MODEL_FILE"),
            config.Search.Onnx.ModelFile,
            DefaultOnnxModelFile)!;
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

public sealed record PortableDependencyLayout(string PortableDirectory, string OnnxModelDirectory, string OcrModelDirectory, string WhisperModelDirectory, string DiarizationModelDirectory);

public sealed record PortableDependencyInstallResult(
    IReadOnlyList<PortableDependencyInstallItem> Items,
    string PortableDirectory,
    string OnnxModelDirectory,
    string OcrModelDirectory,
    string WhisperModelDirectory,
    string DiarizationModelDirectory);

public sealed record PortableDependencyInstallItem(
    string Name,
    string Path,
    bool Installed,
    string Source,
    string Message);
