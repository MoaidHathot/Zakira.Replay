using Zakira.Replay.Core;

namespace Zakira.Replay.Cli;

/// <summary>
/// Human-readable rendering for the <c>doctor</c> command. The probe logic that builds the
/// <see cref="DoctorReport"/> and the <c>info</c> report lives in
/// <see cref="DiagnosticsService"/> (Core) so the CLI and MCP server share identical logic;
/// this file is presentation only.
/// </summary>
internal static class CliDiagnostics
{
    /// <summary>
    /// Renders a <see cref="DoctorReport"/> as a scannable, optionally colorized report:
    /// a count summary header, one aligned row per dependency (green ✓ ok / yellow ! degraded /
    /// red ✗ missing) with the source and path dimmed, an indented reason line for anything that
    /// needs attention, and a closing summary. Pure function of its inputs — <paramref name="color"/>
    /// gates every escape code so redirected output and unit tests stay plain text.
    /// </summary>
    public static void RenderDoctorText(DoctorReport report, TextWriter writer, bool color)
    {
        var deps = report.Dependencies;
        var okCount = deps.Count(d => d.IsFound && d.IsRunnable);
        var degradedCount = deps.Count(d => d.IsFound && !d.IsRunnable);
        var missingCount = deps.Count(d => !d.IsFound);

        var badges = new List<string> { ConsoleStyle.Green($"{okCount} ok", color) };
        if (degradedCount > 0)
        {
            badges.Add(ConsoleStyle.Yellow($"{degradedCount} degraded", color));
        }
        if (missingCount > 0)
        {
            badges.Add(ConsoleStyle.Red($"{missingCount} missing", color));
        }

        writer.WriteLine($"{ConsoleStyle.Bold("Zakira.Replay doctor", color)}  {string.Join(" \u00b7 ", badges)}");
        writer.WriteLine();

        // Widest word is "degraded" (8). Pad the plain word before coloring so the ANSI escape
        // codes never throw the name column out of alignment.
        var nameWidth = deps.Count == 0 ? 0 : Math.Min(deps.Max(d => d.Name.Length), 20);
        const string Indent = "         ";

        foreach (var dep in deps)
        {
            if (dep.IsFound && dep.IsRunnable)
            {
                var detail = JoinDetail(dep.Source, dep.Path);
                var line = $"  {ConsoleStyle.Green("\u2713", color)} {ConsoleStyle.Green("ok".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}";
                if (!string.IsNullOrEmpty(detail))
                {
                    line += "  " + ConsoleStyle.Dim(detail, color);
                }

                writer.WriteLine(line);
            }
            else if (dep.IsFound)
            {
                writer.WriteLine($"  {ConsoleStyle.Yellow("!", color)} {ConsoleStyle.Yellow("degraded".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}{SourceSuffix(dep.Source, color)}");
                WriteDetailLines(writer, Indent, dep.Path, FirstNonEmpty(dep.RunnableError, dep.Message), ConsoleStyle.Yellow, color);
            }
            else
            {
                writer.WriteLine($"  {ConsoleStyle.Red("\u2717", color)} {ConsoleStyle.Red("missing".PadRight(8), color)} {dep.Name.PadRight(nameWidth)}{SourceSuffix(dep.Source, color)}");
                WriteDetailLines(writer, Indent, dep.Path, FirstNonEmpty(dep.Message, dep.RunnableError), ConsoleStyle.Red, color);
            }
        }

        writer.WriteLine();
        if (missingCount == 0 && degradedCount == 0)
        {
            writer.WriteLine(ConsoleStyle.Green("All dependencies are ready.", color));
        }
        else
        {
            var issues = missingCount + degradedCount;
            var noun = issues == 1 ? "component needs" : "components need";
            writer.WriteLine($"{issues} {noun} attention \u00b7 run `zakira-replay doctor --output-format json` for full detail.");
        }
    }

    private static void WriteDetailLines(TextWriter writer, string indent, string? path, string? reason, Func<string, bool, string> reasonColor, bool color)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            writer.WriteLine(indent + ConsoleStyle.Dim(path!, color));
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            writer.WriteLine(indent + reasonColor("\u2192 " + reason, color));
        }
    }

    private static string SourceSuffix(string? source, bool color)
        => string.IsNullOrWhiteSpace(source) ? string.Empty : "  " + ConsoleStyle.Dim(source!, color);

    private static string JoinDetail(string? source, string? path)
    {
        var parts = new[] { source, path }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("  ", parts);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
