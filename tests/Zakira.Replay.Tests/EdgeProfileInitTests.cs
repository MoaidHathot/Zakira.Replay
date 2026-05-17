using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class EdgeProfileInitTests
{
    [Fact]
    public void EdgeProfileInitRequestRequiresUserDataDir()
    {
        // The record allows construction with empty string; the service validates at run time.
        var request = new EdgeProfileInitRequest(UserDataDir: string.Empty);
        Assert.Equal(string.Empty, request.UserDataDir);
        Assert.Null(request.ProfileDirectory);
        Assert.Null(request.StartUrl);
    }

    [Fact]
    public void EdgeProfileInitResultPreservesFields()
    {
        var result = new EdgeProfileInitResult(
            UserDataDir: @"C:\zakira\edge",
            ProfileDirectory: "Default",
            Initialized: true,
            CookiesPath: @"C:\zakira\edge\Default\Network\Cookies");

        Assert.True(result.Initialized);
        Assert.Equal(@"C:\zakira\edge", result.UserDataDir);
        Assert.Equal("Default", result.ProfileDirectory);
        Assert.EndsWith("Cookies", result.CookiesPath);
    }

    [Fact]
    public async Task RunAsyncThrowsWhenUserDataDirIsEmpty()
    {
        var service = new EdgeProfileInitService();
        using var stdout = new StringWriter();

        var ex = await Assert.ThrowsAnyAsync<ReplayException>(() =>
            service.RunAsync(new EdgeProfileInitRequest(string.Empty), stdout, CancellationToken.None));
        Assert.Contains("UserDataDir is required", ex.Message);
    }

    [Fact]
    public async Task RunAsyncThrowsWhenEdgeBinaryMissing()
    {
        using var temp = new TestTempDirectory();

        // Force DependencyResolver to fail to find Edge by pointing the path config at a
        // non-existent file and clearing the env var.
        var config = ConfigStore.CreateDefaultConfig();
        config.Dependencies.EdgePath = temp.GetPath("nonexistent-edge.exe");
        var resolver = new DependencyResolver(config);
        var service = new EdgeProfileInitService(resolver);
        using var stdout = new StringWriter();

        var previousEnv = Environment.GetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_PATH", temp.GetPath("definitely-missing.exe"));
            await Assert.ThrowsAnyAsync<ReplayException>(() =>
                service.RunAsync(new EdgeProfileInitRequest(temp.GetPath("edge")), stdout, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_EDGE_PATH", previousEnv);
        }
    }
}
