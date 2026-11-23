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

    /// <summary>Subprocess wall-clock budgets from the resolved config (<c>timeouts.*</c>).</summary>
    public TimeoutsConfig Timeouts => config.Timeouts ?? new TimeoutsConfig();

    public DependencyStatus GetYtDlpStatus() => GetExecutableStatus("yt-dlp", config.Dependencies.YtDlpPath, "ZAKIRA_REPLAY_YTDLP_PATH");

    public DependencyStatus GetFfmpegStatus() => GetExecutableStatus("ffmpeg", config.Dependencies.FfmpegPath, "ZAKIRA_REPLAY_FFMPEG_PATH");

    public DependencyStatus GetFfprobeStatus() => GetExecutableStatus("ffprobe", config.Dependencies.FfprobePath, "ZAKIRA_REPLAY_FFPROBE_PATH");

    public DependencyStatus GetCopilotCliStatus() => GetExecutableStatus("copilot", config.Dependencies.CopilotCliPath, "ZAKIRA_REPLAY_COPILOT_CLI_PATH", "COPILOT_CLI_PATH");

    public DependencyStatus GetEdgeStatus()
    {
        if (GetExplicitStatus("edge", config.Dependencies.EdgePath, "ZAKIRA_REPLAY_EDGE_PATH") is { } status)
        {
            return status;
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

    public string RequireCopilotCli(string requiredFor) => Require(GetCopilotCliStatus(), "copilot", requiredFor, "ZAKIRA_REPLAY_COPILOT_CLI_PATH");

    public IReadOnlyList<DependencyStatus> GetAllStatuses()
    {
        return
        [
            GetYtDlpStatus(),
            GetFfmpegStatus(),
            GetFfprobeStatus(),
            GetEdgeStatus(),
            GetCopilotCliStatus(),
            DependencyStatus.Found("github-copilot-sdk", "managed SDK compiled in (auth: logged-in GitHub Copilot session)", null)
        ];
    }

    private string Require(DependencyStatus status, string dependency, string requiredFor, string envVarName)
    {
        if (status.IsFound && !string.IsNullOrWhiteSpace(status.Path))
        {
            return status.Path;
        }

        // A missing explicit path carries the config key or env var as its source. Only
        // a miss after searching both portable and PATH is eligible for a download.
        if (config.Dependencies.AutoDownload && SupportsPortableDownload(dependency) && status.Source is null)
        {
            installer.InstallAsync([dependency], force: false, progress: null, CancellationToken.None).GetAwaiter().GetResult();
            status = dependency switch
            {
                "yt-dlp" => GetYtDlpStatus(),
                "ffmpeg" => GetFfmpegStatus(),
                "ffprobe" => GetFfprobeStatus(),
                "copilot" => GetCopilotCliStatus(),
                _ => status
            };

            if (status.IsFound && !string.IsNullOrWhiteSpace(status.Path))
            {
                return status.Path;
            }
        }

        throw new MissingDependencyException(dependency, requiredFor, envVarName);
    }

    private static DependencyStatus? GetExplicitStatus(string executableName, string? configuredPath, params string[] envVarNames)
    {
        foreach (var envVarName in envVarNames)
        {
            var value = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return CheckPath(value, envVarName, $"{envVarName} is set but does not exist");
            }
        }

        return string.IsNullOrWhiteSpace(configuredPath) ? null
            : CheckPath(configuredPath, $"config:{executableName}.path", $"configured {executableName}.path does not exist");

        DependencyStatus CheckPath(string value, string source, string missingMessage)
        {
            var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return File.Exists(path)
                ? DependencyStatus.Found(executableName, Path.GetFullPath(path), source)
                : DependencyStatus.Missing(executableName, $"{missingMessage}: {path}", source);
        }
    }

    private DependencyStatus GetExecutableStatus(string executableName, string? configuredPath, params string[] envVarNames)
    {
        if (GetExplicitStatus(executableName, configuredPath, envVarNames) is { } status)
        {
            return status;
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

    private static bool SupportsPortableDownload(string dependency)
    {
        return dependency is "yt-dlp" or "ffmpeg" or "ffprobe" or "copilot";
    }

    /// <summary>First PATH entry that contains <paramref name="executableName"/> (PATHEXT-aware on Windows), or null.</summary>
    public static string? FindExecutableOnPath(string executableName) => FindOnPath(executableName);

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
