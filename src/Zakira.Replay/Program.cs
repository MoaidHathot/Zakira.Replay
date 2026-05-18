using System.Text;
using Zakira.Replay.Cli;
using Zakira.Replay.Core;

// stdout default encoding to UTF-8 so JSON / Markdown output round-trips on Windows.
Console.OutputEncoding = Encoding.UTF8;

// Wire Ctrl-C to a real CancellationToken so async actions and subprocesses (yt-dlp,
// ffmpeg, Playwright) get a chance to clean up. Without this Program.cs was passing
// CancellationToken.None and Ctrl-C would kill the process mid-write, leaving a corrupt
// manifest.json behind. ArtifactStore.WriteJsonAsync now writes atomically, but a polite
// shutdown is still strictly better.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
catch (MissingDependencyException ex)
{
    Console.Error.WriteLine(ex.ToDisplayString());
    return 2;
}
catch (ReplayException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (ex is ProcessFailedException processFailed && !string.IsNullOrWhiteSpace(processFailed.StandardError))
    {
        Console.Error.WriteLine(processFailed.StandardError.Trim());
    }

    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Error: operation cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Unexpected error:");
    Console.Error.WriteLine(ex);
    return 1;
}
