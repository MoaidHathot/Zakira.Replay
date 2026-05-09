using System.Runtime.InteropServices;

namespace Zakira.Replay.Core;

public sealed class DependencyResolver
{
    private static readonly string[] WindowsExecutableExtensions = [".exe", ".cmd", ".bat"];
    private readonly ReplayConfig config;
    private readonly PortableDependencyInstaller installer;

    public DependencyResolver(ReplayConfig? config = null)
    {
        this.config = config ?? new ConfigStore().Load();
        installer = new PortableDependencyInstaller(this.config);
    }

    public DependencyStatus GetYtDlpStatus() => GetExecutableStatus("yt-dlp", "ZAKIRA_REPLAY_YTDLP_PATH", config.Dependencies.YtDlpPath);

    public DependencyStatus GetFfmpegStatus() => GetExecutableStatus("ffmpeg", "ZAKIRA_REPLAY_FFMPEG_PATH", config.Dependencies.FfmpegPath);

    public DependencyStatus GetFfprobeStatus() => GetExecutableStatus("ffprobe", "ZAKIRA_REPLAY_FFPROBE_PATH", config.Dependencies.FfprobePath);

    public DependencyStatus GetEdgeStatus()
    {
        var configured = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? DependencyStatus.Found("edge", configured, "ZAKIRA_REPLAY_EDGE_PATH")
                : DependencyStatus.Missing("edge", $"ZAKIRA_REPLAY_EDGE_PATH is set but does not exist: {configured}", "ZAKIRA_REPLAY_EDGE_PATH");
        }

        if (!string.IsNullOrWhiteSpace(config.Dependencies.EdgePath))
        {
            return File.Exists(config.Dependencies.EdgePath)
                ? DependencyStatus.Found("edge", config.Dependencies.EdgePath, "config:edge.path")
                : DependencyStatus.Missing("edge", $"configured edge.path does not exist: {config.Dependencies.EdgePath}", "config:edge.path");
        }

        foreach (var path in GetKnownEdgePaths())
        {
            if (File.Exists(path))
            {
                return DependencyStatus.Found("edge", path, null);
            }
        }

        var fromPath = FindOnPath("msedge");
        return fromPath is null
            ? DependencyStatus.Missing("edge", "msedge was not found in known locations or PATH.", "ZAKIRA_REPLAY_EDGE_PATH")
            : DependencyStatus.Found("edge", fromPath, null);
    }

    public string RequireYtDlp(string requiredFor) => Require(GetYtDlpStatus(), "yt-dlp", requiredFor, "ZAKIRA_REPLAY_YTDLP_PATH");

    public string RequireFfmpeg(string requiredFor) => Require(GetFfmpegStatus(), "ffmpeg", requiredFor, "ZAKIRA_REPLAY_FFMPEG_PATH");

    public string RequireFfprobe(string requiredFor) => Require(GetFfprobeStatus(), "ffprobe", requiredFor, "ZAKIRA_REPLAY_FFPROBE_PATH");

    public string RequireEdge(string requiredFor) => Require(GetEdgeStatus(), "edge", requiredFor, "ZAKIRA_REPLAY_EDGE_PATH");

    public IReadOnlyList<DependencyStatus> GetAllStatuses()
    {
        return
        [
            GetYtDlpStatus(),
            GetFfmpegStatus(),
            GetFfprobeStatus(),
            GetEdgeStatus(),
            DependencyStatus.Found("github-copilot-sdk", "uses logged-in GitHub Copilot session", null)
        ];
    }

    private string Require(DependencyStatus status, string dependency, string requiredFor, string envVarName)
    {
        if (status.IsFound && !string.IsNullOrWhiteSpace(status.Path))
        {
            return status.Path;
        }

        if (config.Dependencies.AutoDownload && SupportsPortableDownload(dependency) && !IsExplicitMissing(status))
        {
            installer.InstallAsync([dependency], force: false, progress: null, CancellationToken.None).GetAwaiter().GetResult();
            status = dependency switch
            {
                "yt-dlp" => GetYtDlpStatus(),
                "ffmpeg" => GetFfmpegStatus(),
                "ffprobe" => GetFfprobeStatus(),
                _ => status
            };

            if (status.IsFound && !string.IsNullOrWhiteSpace(status.Path))
            {
                return status.Path;
            }
        }

        throw new MissingDependencyException(dependency, requiredFor, envVarName);
    }

    private DependencyStatus GetExecutableStatus(string executableName, string envVarName, string? configuredPath)
    {
        var configured = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? DependencyStatus.Found(executableName, configured, envVarName)
                : DependencyStatus.Missing(executableName, $"{envVarName} is set but does not exist: {configured}", envVarName);
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath)
                ? DependencyStatus.Found(executableName, configuredPath, $"config:{executableName}.path")
                : DependencyStatus.Missing(executableName, $"configured {executableName}.path does not exist: {configuredPath}", $"config:{executableName}.path");
        }

        var portablePath = installer.GetPortableExecutablePath(executableName);
        if (File.Exists(portablePath))
        {
            return DependencyStatus.Found(executableName, portablePath, "portable");
        }

        var found = FindOnPath(executableName);
        return found is null
            ? DependencyStatus.Missing(executableName, $"{executableName} was not found in the portable directory or on PATH.", null)
            : DependencyStatus.Found(executableName, found, null);
    }

    private static bool IsExplicitMissing(DependencyStatus status)
    {
        return !string.IsNullOrWhiteSpace(status.Source)
            && (status.Source.StartsWith("ZAKIRA_REPLAY_", StringComparison.OrdinalIgnoreCase)
                || status.Source.StartsWith("config:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SupportsPortableDownload(string dependency)
    {
        return dependency is "yt-dlp" or "ffmpeg" or "ffprobe";
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidates = GetExecutableCandidates(executableName);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetExecutableCandidates(string executableName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.HasExtension(executableName))
        {
            yield return executableName;
            yield break;
        }

        yield return executableName;

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathext)
            ? WindowsExecutableExtensions
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var extension in extensions)
        {
            yield return executableName + extension.ToLowerInvariant();
        }
    }

    private static IEnumerable<string> GetKnownEdgePaths()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe");
        }
    }
}

public sealed record DependencyStatus(string Name, bool IsFound, string? Path, string? Source, string? Message)
{
    public static DependencyStatus Found(string name, string path, string? source) => new(name, true, path, source, null);

    public static DependencyStatus Missing(string name, string message, string? source) => new(name, false, null, source, message);
}
