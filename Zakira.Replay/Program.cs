using System.Text;
using Zakira.Replay.Cli;
using Zakira.Replay.Core;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return await CliApp.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
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
