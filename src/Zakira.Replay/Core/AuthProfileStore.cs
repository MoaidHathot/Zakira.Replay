using System.Globalization;

namespace Zakira.Replay.Core;

/// <summary>
/// On-disk store for Playwright storage-state profiles created by
/// <c>zakira-replay auth login &lt;profile&gt;</c>. Each profile is a single JSON file under the
/// configured auth directory. Resolution priority for the directory:
/// <list type="number">
/// <item><description><c>ZAKIRA_REPLAY_AUTH_DIRECTORY</c> environment variable</description></item>
/// <item><description><see cref="AuthConfig.Directory"/> in user config</description></item>
/// <item><description>Default: <c>auth/</c> next to the configuration file</description></item>
/// </list>
/// Profile names are slugified before they touch the file system, so a name like
/// <c>"Microsoft Ignite 2026"</c> becomes <c>microsoft-ignite-2026.json</c> on disk.
/// </summary>
public sealed class AuthProfileStore
{
    private readonly ReplayConfig config;
    private readonly string? configPath;

    public AuthProfileStore(ReplayConfig? config = null, string? configPath = null)
    {
        this.config = config ?? new ConfigStore().Load();
        this.configPath = configPath ?? new ConfigStore().ConfigPath;
    }

    /// <summary>Absolute path of the directory that stores auth-profile files.</summary>
    public string Directory => GetAuthDirectory(this.config, this.configPath);

    /// <summary>Slugified profile name as it would appear on disk (without extension).</summary>
    public static string SlugifyProfileName(string profileName)
    {
        return Slug.Create(profileName, 80);
    }

    /// <summary>
    /// Resolves a user-supplied profile name to an absolute file path on disk. Always returns
    /// a valid path even when the profile does not yet exist (use <see cref="TryRead"/> to
    /// check existence).
    /// </summary>
    public string GetProfilePath(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ReplayException("Auth profile name must be a non-empty string.");
        }

        var slug = SlugifyProfileName(profileName);
        if (string.IsNullOrEmpty(slug))
        {
            throw new ReplayException($"Auth profile name '{profileName}' produced an empty slug.");
        }

        return Path.Combine(Directory, slug + ".json");
    }

    /// <summary>
    /// Returns metadata for the requested profile, or <c>null</c> when the file does not exist
    /// on disk. Does not load or parse the storage-state JSON itself; callers that need to
    /// hand the file to Playwright pass <see cref="AuthProfile.Path"/> directly.
    /// </summary>
    public AuthProfile? TryRead(string profileName, DateTimeOffset? referenceTime = null)
    {
        var path = GetProfilePath(profileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var now = referenceTime ?? DateTimeOffset.UtcNow;
        var age = now - lastWrite;
        var threshold = TimeSpan.FromMinutes(Math.Max(1, config.Auth.StaleThresholdMinutes));
        var isStale = age >= threshold;

        return new AuthProfile(
            Name: profileName,
            Slug: SlugifyProfileName(profileName),
            Path: path,
            ByteCount: info.Length,
            CreatedAtUtc: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            LastWriteAtUtc: lastWrite,
            Age: age,
            IsStale: isStale);
    }

    /// <summary>
    /// Lists every <c>*.json</c> file in the auth directory and returns one
    /// <see cref="AuthProfile"/> per file. Profile <c>Name</c> equals the slug because the
    /// original (un-slugged) display name is not preserved on disk.
    /// </summary>
    public IReadOnlyList<AuthProfile> List(DateTimeOffset? referenceTime = null)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var now = referenceTime ?? DateTimeOffset.UtcNow;
        var threshold = TimeSpan.FromMinutes(Math.Max(1, config.Auth.StaleThresholdMinutes));
        return System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                var age = now - lastWrite;
                var slug = Path.GetFileNameWithoutExtension(path);
                return new AuthProfile(
                    Name: slug,
                    Slug: slug,
                    Path: path,
                    ByteCount: info.Length,
                    CreatedAtUtc: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                    LastWriteAtUtc: lastWrite,
                    Age: age,
                    IsStale: age >= threshold);
            })
            .ToArray();
    }

    /// <summary>
    /// Deletes the file backing the named profile. Returns true when a file existed and was
    /// deleted, false when the profile did not exist on disk. Throws when the file existed but
    /// could not be deleted (e.g. locked by another process).
    /// </summary>
    public bool Clear(string profileName)
    {
        var path = GetProfilePath(profileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Writes the supplied JSON storage-state body to the profile file. Used by
    /// <c>auth login</c>; tests can also use this to seed fixtures without launching Playwright.
    /// </summary>
    public string SaveJson(string profileName, string storageStateJson)
    {
        var path = GetProfilePath(profileName);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, storageStateJson);
        return path;
    }

    private static string GetAuthDirectory(ReplayConfig config, string? configPath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_AUTH_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(fromEnv));
        }

        if (!string.IsNullOrWhiteSpace(config.Auth.Directory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.Auth.Directory));
        }

        var configDir = string.IsNullOrWhiteSpace(configPath)
            ? Path.GetDirectoryName(ConfigStore.GetDefaultConfigPath())
            : Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(configDir)
            ? Path.GetFullPath("auth")
            : Path.Combine(configDir, "auth");
    }
}

/// <summary>
/// Snapshot of an on-disk auth profile. <see cref="Path"/> is what Playwright consumes via
/// <c>BrowserNewContextOptions.StorageStatePath</c>; everything else is metadata for CLI
/// listing and pipeline staleness warnings.
/// </summary>
public sealed record AuthProfile(
    string Name,
    string Slug,
    string Path,
    long ByteCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastWriteAtUtc,
    TimeSpan Age,
    bool IsStale)
{
    public string FormatAge()
    {
        if (Age.TotalSeconds < 60)
        {
            return $"{Age.TotalSeconds:F0}s";
        }

        if (Age.TotalMinutes < 60)
        {
            return $"{Age.TotalMinutes:F0}m";
        }

        if (Age.TotalHours < 48)
        {
            return $"{Age.TotalHours:F1}h";
        }

        return Age.TotalDays.ToString("F1", CultureInfo.InvariantCulture) + "d";
    }
}
