using System.CommandLine;
using System.CommandLine.Parsing;

namespace Zakira.Replay.Cli;

/// <summary>
/// Three-level verbosity model used by every long-running CLI command:
/// <list type="bullet">
///   <item><see cref="Quiet"/>: suppress all in-flight progress and the final summary. Only
///     <c>error</c>-severity warnings reach stdout/stderr. JSON mode is unaffected (the JSON
///     envelope is always emitted).</item>
///   <item><see cref="Default"/>: suppress in-flight progress. Print a compact start/done
///     summary. Surface <c>warning</c> + <c>error</c> warnings; hide <c>info</c>. This is the
///     baseline a casual <c>dnx Zakira.Replay analyze &lt;url&gt;</c> user sees.</item>
///   <item><see cref="Verbose"/>: stream every progress milestone the pipeline emits. Surface
///     all warning severities including <c>info</c>. Matches the pre-0.14 default behaviour.</item>
/// </list>
/// </summary>
public enum CliVerbosity
{
    Quiet,
    Default,
    Verbose
}

/// <summary>
/// Helpers that resolve <see cref="CliVerbosity"/> from <c>--verbose</c> / <c>--quiet</c> global
/// flags and apply it to the existing <c>SynchronousProgress&lt;string&gt;</c> + warning
/// rendering paths in <see cref="CliApp"/>. The <see cref="VerboseOption"/> /
/// <see cref="QuietOption"/> references are populated by <see cref="CliApp.BuildRootCommand"/>
/// at startup; helpers use them via <see cref="ParseResult.GetValue{T}(Option{T})"/>.
/// </summary>
internal static class CliVerbosityHelpers
{
    /// <summary>Recursive root option, set in <see cref="CliApp.BuildRootCommand"/>.</summary>
    internal static Option<bool>? VerboseOption { get; set; }

    /// <summary>Recursive root option, set in <see cref="CliApp.BuildRootCommand"/>.</summary>
    internal static Option<bool>? QuietOption { get; set; }

    /// <summary>
    /// Reads <c>--verbose</c> / <c>--quiet</c> off any sub-command's parse result. Both set is
    /// a user error; we honour <c>--verbose</c> in that case since it surfaces strictly more
    /// information (the safer side of a contradiction).
    /// </summary>
    public static CliVerbosity Resolve(ParseResult parseResult)
    {
        var verbose = VerboseOption is not null && parseResult.GetValue(VerboseOption);
        var quiet = QuietOption is not null && parseResult.GetValue(QuietOption);
        if (verbose) return CliVerbosity.Verbose;
        if (quiet) return CliVerbosity.Quiet;
        return CliVerbosity.Default;
    }

    /// <summary>
    /// Returns true when <paramref name="severity"/> should be rendered under the resolved
    /// <paramref name="verbosity"/>. Severity strings come from <c>ReplayWarningSeverities</c>;
    /// unknown values are treated as warnings (rendered in Default + Verbose, suppressed in
    /// Quiet).
    /// </summary>
    public static bool ShouldRender(CliVerbosity verbosity, string severity)
    {
        return verbosity switch
        {
            CliVerbosity.Verbose => true,
            CliVerbosity.Quiet => string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase),
            _ => !string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Returns true when the start/done summary block (run id, artifact dir, frame count,
    /// transcript) should be printed. Suppressed in Quiet; shown in Default and Verbose.
    /// </summary>
    public static bool ShouldRenderSummary(CliVerbosity verbosity) => verbosity != CliVerbosity.Quiet;

    /// <summary>
    /// Returns true when every <c>progress?.Report(...)</c> emitted by the pipeline should be
    /// forwarded. Only Verbose qualifies; Default and Quiet swallow the stream. Default still
    /// prints its own start/done summary; Quiet stays silent.
    /// </summary>
    public static bool ShouldStreamProgress(CliVerbosity verbosity) => verbosity == CliVerbosity.Verbose;
}
