using System.Diagnostics;

namespace Zakira.Replay.Core;

/// <summary>
/// Interactive one-time setup of a dedicated Microsoft Edge profile for
/// <see cref="PlaywrightVideoCaptureClient"/>'s persistent-context mode. Launches the user's
/// real Edge binary (no Playwright automation surface) with
/// <c>--user-data-dir &lt;configured-dir&gt;</c> so cookies, tokens, and saved-passwords land
/// in Chromium's normal DPAPI-encrypted storage (per-user, per-machine on Windows) rather
/// than the plaintext <c>StorageState</c> JSON used by <see cref="AuthProfileLoginService"/>.
/// </summary>
/// <remarks>
/// Why a separate command instead of reusing <c>auth login</c>? Because <c>auth login</c>
/// runs through Playwright in order to extract a portable <c>StorageState</c> snapshot —
/// the very portability that creates the at-rest security concern. Here we deliberately
/// bypass Playwright: we launch Edge as the user would, Edge writes its own profile, and
/// we verify the resulting profile is usable. No Zakira-produced secrets-on-disk.
/// </remarks>
public sealed class EdgeProfileInitService
{
    private readonly DependencyResolver dependencies;

    public EdgeProfileInitService(DependencyResolver? dependencies = null)
    {
        this.dependencies = dependencies ?? new DependencyResolver();
    }

    public async Task<EdgeProfileInitResult> RunAsync(EdgeProfileInitRequest request, TextWriter stdout, CancellationToken cancellationToken)
    {
        var userDataDir = string.IsNullOrWhiteSpace(request.UserDataDir)
            ? throw new ReplayException("EdgeProfileInitRequest.UserDataDir is required.")
            : request.UserDataDir;
        var profileDir = string.IsNullOrWhiteSpace(request.ProfileDirectory) ? "Default" : request.ProfileDirectory;

        Directory.CreateDirectory(userDataDir);

        var edge = dependencies.RequireEdge("dedicated Edge profile initialisation");

        var args = new List<string>
        {
            $"--user-data-dir={userDataDir}",
            $"--profile-directory={profileDir}",
            // Discourage Edge's first-run dance from grabbing focus / running modal prompts.
            "--no-first-run",
            "--no-default-browser-check"
        };
        if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            args.Add(request.StartUrl);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = edge,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        stdout.WriteLine($"Launching Edge with user-data-dir: {userDataDir}");
        stdout.WriteLine($"Profile directory:                {profileDir}");
        if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            stdout.WriteLine($"Initial URL:                      {request.StartUrl}");
        }
        stdout.WriteLine();
        stdout.WriteLine("1. Sign in to your Microsoft account (complete MFA).");
        stdout.WriteLine("2. Optionally navigate to confirm the session works against your target sites.");
        stdout.WriteLine("3. Close Edge when done. Zakira will then verify the profile is usable.");
        stdout.WriteLine();

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new ReplayException($"Failed to launch Edge ({edge}): {ex.Message}");
        }

        if (process is null)
        {
            throw new ReplayException($"Edge launch returned no process handle (binary: {edge}).");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
            throw;
        }

        // After Edge exits, verify the profile actually got initialised. Chromium writes
        // Cookies to either Default/Network/Cookies (current) or Default/Cookies (legacy);
        // treat either as proof of life.
        var profilePath = Path.Combine(userDataDir, profileDir);
        var modernCookies = Path.Combine(profilePath, "Network", "Cookies");
        var legacyCookies = Path.Combine(profilePath, "Cookies");

        string? cookiesPath = null;
        if (File.Exists(modernCookies) && new FileInfo(modernCookies).Length > 0)
        {
            cookiesPath = modernCookies;
        }
        else if (File.Exists(legacyCookies) && new FileInfo(legacyCookies).Length > 0)
        {
            cookiesPath = legacyCookies;
        }

        if (cookiesPath is null)
        {
            stdout.WriteLine($"Profile verification FAILED: no Cookies file found under {profilePath}.");
            stdout.WriteLine("Possible causes:");
            stdout.WriteLine("  - Edge closed before you signed in (sign in and close again).");
            stdout.WriteLine("  - Edge used a different profile sub-folder than expected.");
            stdout.WriteLine($"    Check: {profilePath}");
            stdout.WriteLine("  - A corporate policy redirected to a managed profile.");
            return new EdgeProfileInitResult(userDataDir, profileDir, Initialized: false, CookiesPath: null);
        }

        stdout.WriteLine($"Profile initialised. Cookies at: {cookiesPath}");
        return new EdgeProfileInitResult(userDataDir, profileDir, Initialized: true, CookiesPath: cookiesPath);
    }
}

public sealed record EdgeProfileInitRequest(string UserDataDir, string? ProfileDirectory = null, string? StartUrl = null);

public sealed record EdgeProfileInitResult(string UserDataDir, string ProfileDirectory, bool Initialized, string? CookiesPath);
