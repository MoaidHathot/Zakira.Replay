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

internal sealed record ReplayInfo(
    string Name,
    string Version,
    string ConfigPath,
    string RunsDirectory,
    string LlmProvider,
    string DefaultModel,
    IReadOnlyList<string> Schemas,
    ReplayInfoDependencies? ResolvedDependencies = null,
    ReplayInfoCapabilities? Capabilities = null);

/// <summary>
/// Resolved on-disk paths and configured names for every optional dependency. Useful as a
/// pre-flight for orchestrators: they can call <c>zakira-replay info --output-format json</c>
/// once and know which optional features are wired up without separately running <c>doctor</c>.
/// </summary>
internal sealed record ReplayInfoDependencies(
    string PortableDirectory,
    string OcrModelDirectory,
    string OcrLanguagePack,
    string OnnxModelDirectory,
    string WhisperModelDirectory,
    string? WhisperModelPath,
    string? WhisperModelSize,
    string DiarizationModelDirectory,
    string? OllamaEndpoint,
    string? OllamaModel,
    string? OllamaVisionModel);

/// <summary>
/// Static capability summary: which optional features are available without launching a
/// daemon, downloading a model, or hitting the network. Booleans here reflect what's
/// installed and reachable at info-time; they do NOT promise the dependency will still be
/// working at analysis-time (use <c>doctor</c> for that).
/// </summary>
internal sealed record ReplayInfoCapabilities(
    bool LocalOcrReady,
    bool LocalWhisperReady,
    bool DiarizationReady,
    bool YtDlpAvailable,
    bool FfmpegAvailable);

internal sealed record DoctorReport(DateTimeOffset CreatedAt, IReadOnlyList<DoctorDependencyReport> Dependencies);

internal sealed record DoctorDependencyReport(
    string Name,
    bool IsFound,
    bool IsRunnable,
    string? Path,
    string? Source,
    string? Message,
    string? RunnableError);
