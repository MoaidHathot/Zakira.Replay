namespace Zakira.Replay.Core;

public class ReplayException : Exception
{
    public ReplayException(string message)
        : base(message)
    {
    }

    public ReplayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MissingDependencyException : ReplayException
{
    public MissingDependencyException(string dependency, string requiredFor, string? envVarName = null)
        : base($"Missing dependency: {dependency}")
    {
        Dependency = dependency;
        RequiredFor = requiredFor;
        EnvVarName = envVarName;
    }

    public string Dependency { get; }

    public string RequiredFor { get; }

    public string? EnvVarName { get; }

    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"Missing dependency: {Dependency}",
            string.Empty,
            $"Required for: {RequiredFor}.",
            "Install it manually and ensure it is available on PATH, or run `zakira-replay deps install media` to install portable media tools."
        };

        if (!string.IsNullOrWhiteSpace(EnvVarName))
        {
            lines.Add($"Alternatively configure {EnvVarName}=<full path>.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ProcessFailedException : ReplayException
{
    public ProcessFailedException(string fileName, IReadOnlyList<string> arguments, int exitCode, string standardError)
        : base($"Process failed with exit code {exitCode}: {fileName}")
    {
        FileName = fileName;
        Arguments = arguments;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public int ExitCode { get; }

    public string StandardError { get; }
}
