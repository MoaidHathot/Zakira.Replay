using System.Reflection;

namespace Zakira.Replay.Core;

/// <summary>
/// Single source of truth for the Zakira.Replay package version at runtime.
/// The value is sourced from <see cref="AssemblyInformationalVersionAttribute"/>,
/// which MSBuild generates from the <c>&lt;Version&gt;</c> property in the project file.
/// </summary>
internal static class ReplayVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(ReplayVersion).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<commit-hash>" to InformationalVersion; strip that for display.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
