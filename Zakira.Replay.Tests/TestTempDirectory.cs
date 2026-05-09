namespace Zakira.Replay.Tests;

internal sealed class TestTempDirectory : IDisposable
{
    public TestTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zakira.Replay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(params string[] parts)
    {
        return System.IO.Path.Combine([Path, .. parts]);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
