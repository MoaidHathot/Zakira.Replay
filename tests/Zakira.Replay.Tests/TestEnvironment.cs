using System.Runtime.CompilerServices;

namespace Zakira.Replay.Tests;

/// <summary>
/// Process-wide isolation for the test run. Pins <c>ZAKIRA_REPLAY_CONFIG_PATH</c> to a file that
/// does not exist inside a scratch directory so no test ever reads the developer's real user
/// config (which may point <c>runs.directory</c> at a production runs folder). Without this, any
/// test that resolves the runs root through config — the DI <c>ArtifactStore</c>, CLI helper
/// commands, or <c>ArtifactStore.GetDefaultRootDirectory()</c> — would write batch/queue/job
/// artifacts into that folder.
///
/// Tests that need a specific config still set and restore the variable themselves; restoring
/// lands back on the pinned scratch path, not on the developer's config.
///
/// <c>ZAKIRA_REPLAY_RUNS_DIRECTORY</c> is deliberately cleared rather than pinned so the
/// existing cwd-scoped tests keep their documented <c>&lt;cwd&gt;/runs</c> fallback.
/// </summary>
internal static class TestEnvironment
{
    public static string ScratchDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "Zakira.Replay.Tests",
        "config-isolation",
        Environment.ProcessId.ToString());

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(ScratchDirectory);
        Environment.SetEnvironmentVariable("ZAKIRA_REPLAY_CONFIG_PATH", Path.Combine(ScratchDirectory, "Zakira.Replay.json"));
        Environment.SetEnvironmentVariable(Core.ArtifactStore.RunsDirectoryEnvironmentVariable, null);
    }
}
