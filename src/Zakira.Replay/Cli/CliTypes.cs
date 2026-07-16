using System.Text.Json;
using Zakira.Replay.Core;

namespace Zakira.Replay.Cli;

/// <summary>
/// Pure-data records and shared JSON options for the CLI surface. Kept separate from the
/// System.CommandLine wiring so the data shape can be reused by integration tests and
/// downstream tooling without dragging in the parser.
/// </summary>
internal static class AppInfo
{
    public const string Name = "Zakira.Replay";
    public static string Version => ReplayVersion.Current;
}

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
