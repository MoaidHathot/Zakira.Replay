using Microsoft.Playwright;

namespace Zakira.Replay.Core;

/// <summary>
/// Drives an interactive Playwright login against a real browser window: opens Edge in
/// non-headless mode, optionally navigates to a starting URL, hands control to the user, and
/// — once the user signals completion (Enter on the supplied <see cref="TextReader"/> or the
/// browser window being closed) — captures the resulting cookies/localStorage/sessionStorage
/// to a JSON file via <c>BrowserContext.StorageStateAsync</c>.
/// </summary>
/// <remarks>
/// This is the only Zakira.Replay code path that launches Playwright with <c>Headless = false</c>.
/// It is invoked exclusively by <c>zakira-replay auth login</c> and never runs as part of an
/// <c>analyze</c> pipeline — analyses always reuse a previously-saved profile through
/// <see cref="AuthProfileStore"/>.
/// </remarks>
public sealed class AuthProfileLoginService
{
    private readonly DependencyResolver dependencies;
    private readonly AuthProfileStore store;

    public AuthProfileLoginService(DependencyResolver? dependencies = null, AuthProfileStore? store = null)
    {
        this.dependencies = dependencies ?? new DependencyResolver();
        this.store = store ?? new AuthProfileStore();
    }

    public async Task<AuthLoginResult> RunAsync(AuthLoginRequest request, TextReader stdin, TextWriter stdout, CancellationToken cancellationToken)
    {
        var profilePath = store.GetProfilePath(request.ProfileName);
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);

        var edge = dependencies.RequireEdge("interactive auth-profile login");

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = edge,
            Headless = false,
            Args = ["--disable-gpu"]
        }).ConfigureAwait(false);

        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
        };

        var loadedExisting = false;
        if (File.Exists(profilePath))
        {
            contextOptions.StorageStatePath = profilePath;
            loadedExisting = true;
            stdout.WriteLine($"Loaded existing auth profile from {profilePath} (top-up login).");
        }

        await using var context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            try
            {
                await page.GotoAsync(request.StartUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                stdout.WriteLine($"Initial navigation to {request.StartUrl} failed: {ex.Message}. The browser is still open; navigate manually.");
            }
        }

        stdout.WriteLine();
        stdout.WriteLine($"Browser opened. Sign in to your account in the window now.");
        stdout.WriteLine($"When the page shows you are signed in, return here and press Enter to save the session.");
        stdout.WriteLine($"(Closing the browser window before pressing Enter also saves what is currently captured.)");
        stdout.WriteLine();

        var enterTask = Task.Run(async () =>
        {
            try
            {
                await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore: the disconnected path will fire instead.
            }
        }, cancellationToken);

        var disconnectedTcs = new TaskCompletionSource();
        EventHandler<IBrowser> onDisconnected = (_, _) => disconnectedTcs.TrySetResult();
        browser.Disconnected += onDisconnected;
        try
        {
            await Task.WhenAny(enterTask, disconnectedTcs.Task).ConfigureAwait(false);
        }
        finally
        {
            browser.Disconnected -= onDisconnected;
        }

        // The user-closed-the-browser path means the context is gone, so we can't pull state.
        if (disconnectedTcs.Task.IsCompleted && !enterTask.IsCompleted)
        {
            stdout.WriteLine("Browser closed before Enter was pressed. Run `auth login` again to save a usable profile.");
            return new AuthLoginResult(profilePath, Saved: false, LoadedExisting: loadedExisting);
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = profilePath
        }).ConfigureAwait(false);

        stdout.WriteLine($"Saved auth profile to: {profilePath}");
        return new AuthLoginResult(profilePath, Saved: true, LoadedExisting: loadedExisting);
    }
}

public sealed record AuthLoginRequest(string ProfileName, string? StartUrl);

public sealed record AuthLoginResult(string ProfilePath, bool Saved, bool LoadedExisting);
