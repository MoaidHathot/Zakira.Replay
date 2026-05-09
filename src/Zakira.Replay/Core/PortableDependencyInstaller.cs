using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Zakira.Replay.Core;

public sealed class PortableDependencyInstaller
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Ffprobe = "ffprobe";
    public const string Onnx = "onnx";
    public const string All = "all";
    public const string DefaultOnnxModelFile = "model_quantized.onnx";

    private const string YtDlpWindowsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpLinuxX64Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";
    private const string YtDlpLinuxArm64Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64";
    private const string YtDlpMacOsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";
    private const string FfmpegWindowsX64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    private const string OnnxRepositoryBaseUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main";

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

    public PortableDependencyLayout Layout => new(GetPortableDirectory(config), GetOnnxModelDirectory(config));

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

    public static string GetDefaultPortableDirectory()
    {
        return Path.Combine(GetDefaultDataDirectory(), "portable");
    }

    public static string GetDefaultOnnxModelDirectory()
    {
        return Path.Combine(GetDefaultPortableDirectory(), "models", "all-MiniLM-L6-v2");
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
                default:
                    throw new ReplayException($"Unknown dependency target: {target}. Use yt-dlp, ffmpeg, ffprobe, onnx, media, or all.");
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
            }
        }

        return new PortableDependencyInstallResult(items, layout.PortableDirectory, layout.OnnxModelDirectory);
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

    private async Task<bool> DownloadFileAsync(string url, string destinationPath, bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!force && File.Exists(destinationPath))
        {
            progress?.Report($"Exists: {destinationPath}");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        progress?.Report($"Downloading: {url}");
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

public sealed record PortableDependencyLayout(string PortableDirectory, string OnnxModelDirectory);

public sealed record PortableDependencyInstallResult(
    IReadOnlyList<PortableDependencyInstallItem> Items,
    string PortableDirectory,
    string OnnxModelDirectory);

public sealed record PortableDependencyInstallItem(
    string Name,
    string Path,
    bool Installed,
    string Source,
    string Message);
