using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zakira.Replay.Core;

/// <summary>
/// Contact sheets: every frame of a run tiled into a handful of grid JPEGs, each cell labelled
/// with its frame index and timestamp, plus a legend JSON that maps cells back to frame ids and
/// paths. Turns "review all 98 frames" from 98 image reads into two for an agent, which is what
/// users were hand-rolling in PowerShell before this existed.
///
/// Rendering is one ffmpeg process per run: the frames are fed through the concat demuxer
/// (1 s per image), scaled/padded to a uniform cell, labelled via <c>sendcmd</c> +
/// <c>drawtext</c> reinit at each frame's pts, and tiled. When drawtext (or a font) is not
/// available the sheet is rendered without labels and the legend remains the source of truth.
/// </summary>
public sealed class ContactSheetService
{
    public const string ManifestFileName = "contact-sheet.json";
    public const string OutputDirectory = "sheets";

    private readonly ArtifactStore artifactStore;
    private readonly IContactSheetRenderer renderer;

    public ContactSheetService(ArtifactStore artifactStore, IContactSheetRenderer renderer)
    {
        this.artifactStore = artifactStore;
        this.renderer = renderer;
    }

    public async Task<ContactSheetResult> BuildAsync(ContactSheetRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ReplayException("Contact sheet requires the id of an existing run.");
        }

        if (!artifactStore.TryLocateRun(request.RunId, out var run))
        {
            throw new ReplayException($"Contact sheet: run '{request.RunId}' was not found under {artifactStore.RootDirectory}.");
        }

        var columns = Math.Clamp(request.Columns ?? 6, 1, 20);
        var thumbWidth = Math.Clamp(request.ThumbWidth ?? 320, 64, 1920);
        var thumbHeight = request.ThumbHeight is { } explicitHeight ? Math.Clamp(explicitHeight, 36, 1080) : (int)Math.Round(thumbWidth * 9.0 / 16.0);
        var maxRows = Math.Clamp(request.MaxRows ?? 8, 1, 40);
        var perSheet = columns * maxRows;

        var frames = await CollectFramesAsync(run, cancellationToken).ConfigureAwait(false);
        if (request.From is { } from)
        {
            frames = frames.Where(frame => frame.TimestampSeconds >= from.TotalSeconds).ToList();
        }
        if (request.To is { } to)
        {
            frames = frames.Where(frame => frame.TimestampSeconds <= to.TotalSeconds).ToList();
        }

        if (frames.Count == 0)
        {
            throw new ReplayException($"Contact sheet: run '{run.Id}' has no frames (nothing in manifest.json / frame-capture.json{(request.From is not null || request.To is not null ? " inside the requested window" : string.Empty)}).");
        }

        var outputDirectory = run.GetPath(OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var baseName = string.IsNullOrWhiteSpace(request.OutputName) ? "sheet" : Slug.Create(request.OutputName, 60);

        var sheets = new List<ContactSheetArtifact>();
        var warnings = new List<ReplayWarning>();
        var chunks = frames.Chunk(perSheet).ToArray();
        var labelsRequested = request.Labels ?? true;
        var labelsRendered = labelsRequested;
        for (var sheetIndex = 0; sheetIndex < chunks.Length; sheetIndex++)
        {
            var chunk = chunks[sheetIndex];
            var rows = (int)Math.Ceiling(chunk.Length / (double)columns);
            var fileName = chunks.Length == 1 ? $"{baseName}.jpg" : $"{baseName}-{sheetIndex + 1:000}.jpg";
            var outputPath = Path.Combine(outputDirectory, fileName);
            var cells = new List<ContactSheetCell>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var frame = chunk[i];
                cells.Add(new ContactSheetCell(
                    Index: sheetIndex * perSheet + i + 1,
                    Row: i / columns,
                    Column: i % columns,
                    FrameId: frame.Id,
                    FramePath: frame.Path,
                    TimestampSeconds: frame.TimestampSeconds,
                    TimestampLabel: frame.TimestampLabel,
                    Label: BuildCellLabel(sheetIndex * perSheet + i + 1, frame)));
            }

            progress?.Report($"Rendering contact sheet {sheetIndex + 1}/{chunks.Length} ({chunk.Length} frame(s), {columns}x{rows}) -> {fileName}");
            var layout = new ContactSheetLayout(columns, rows, thumbWidth, thumbHeight, Padding: 2, Margin: 4);
            var rendered = await renderer.RenderAsync(
                cells.Select(cell => new ContactSheetTile(run.GetPath(cell.FramePath), cell.Label)).ToList(),
                layout,
                outputPath,
                labels: labelsRendered,
                cancellationToken).ConfigureAwait(false);

            if (!rendered.Succeeded && labelsRendered)
            {
                // Retry once without labels: drawtext missing from the ffmpeg build or no usable font.
                warnings.Add(new ReplayWarning(
                    ReplayWarningCodes.ContactSheetLabelsUnavailable,
                    $"ffmpeg could not draw cell labels ({rendered.Error}); rendering without labels. Use the legend in {ManifestFileName} to map cells to frames.",
                    Source: "ffmpeg",
                    Severity: ReplayWarningSeverities.Warning));
                labelsRendered = false;
                rendered = await renderer.RenderAsync(
                    cells.Select(cell => new ContactSheetTile(run.GetPath(cell.FramePath), cell.Label)).ToList(),
                    layout,
                    outputPath,
                    labels: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!rendered.Succeeded)
            {
                throw new ReplayException($"Contact sheet rendering failed for {fileName}: {rendered.Error}");
            }

            sheets.Add(new ContactSheetArtifact(
                Index: sheetIndex + 1,
                Path: Path.Combine(OutputDirectory, fileName).Replace('\\', '/'),
                Columns: columns,
                Rows: rows,
                Width: rendered.Width,
                Height: rendered.Height,
                FirstTimestampSeconds: chunk[0].TimestampSeconds,
                LastTimestampSeconds: chunk[^1].TimestampSeconds,
                Cells: cells));
        }

        var manifest = new ContactSheetManifest(
            SchemaVersion: "0.1",
            RunId: run.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            FrameCount: frames.Count,
            Columns: columns,
            ThumbWidth: thumbWidth,
            ThumbHeight: thumbHeight,
            Labels: labelsRendered,
            Sheets: sheets,
            Warnings: warnings);
        await artifactStore.WriteJsonAsync(run, ManifestFileName, manifest, cancellationToken).ConfigureAwait(false);
        return new ContactSheetResult(run, manifest);
    }

    /// <summary>
    /// Frames known to the run: the analyze manifest's frames plus any ad-hoc captures
    /// (<c>frame-capture.json</c>), de-duplicated by path and ordered by timestamp.
    /// </summary>
    internal async Task<List<FrameArtifact>> CollectFramesAsync(VideoRun run, CancellationToken cancellationToken)
    {
        var frames = new List<FrameArtifact>();
        var manifest = await artifactStore.ReadJsonAsync<ArtifactManifest>(run, "manifest.json", cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            frames.AddRange(manifest.Frames);
        }

        var capture = await artifactStore.ReadJsonAsync<FrameCaptureManifest>(run, "frame-capture.json", cancellationToken).ConfigureAwait(false);
        if (capture is not null)
        {
            frames.AddRange(capture.Frames);
        }

        return frames
            .Where(frame => File.Exists(run.GetPath(frame.Path)))
            .GroupBy(frame => frame.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(frame => frame.TimestampSeconds)
            .ThenBy(frame => frame.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildCellLabel(int index, FrameArtifact frame)
        => $"#{index} {frame.TimestampLabel}";
}

public sealed record ContactSheetRequest(
    string RunId,
    int? Columns = null,
    int? ThumbWidth = null,
    int? ThumbHeight = null,
    int? MaxRows = null,
    TimeSpan? From = null,
    TimeSpan? To = null,
    bool? Labels = null,
    string? OutputName = null);

public sealed record ContactSheetResult(VideoRun Run, ContactSheetManifest Manifest);

public sealed record ContactSheetManifest(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAt,
    int FrameCount,
    int Columns,
    int ThumbWidth,
    int ThumbHeight,
    bool Labels,
    IReadOnlyList<ContactSheetArtifact> Sheets,
    IReadOnlyList<ReplayWarning> Warnings);

public sealed record ContactSheetArtifact(
    int Index,
    string Path,
    int Columns,
    int Rows,
    int? Width,
    int? Height,
    double FirstTimestampSeconds,
    double LastTimestampSeconds,
    IReadOnlyList<ContactSheetCell> Cells);

/// <summary>One grid cell. <see cref="Index"/> is 1-based across all sheets of the run and matches the burned-in label.</summary>
public sealed record ContactSheetCell(
    int Index,
    int Row,
    int Column,
    string FrameId,
    string FramePath,
    double TimestampSeconds,
    string TimestampLabel,
    string Label);

public sealed record ContactSheetTile(string ImagePath, string Label);

public sealed record ContactSheetLayout(int Columns, int Rows, int ThumbWidth, int ThumbHeight, int Padding, int Margin);

public sealed record ContactSheetRenderResult(bool Succeeded, int? Width, int? Height, string? Error)
{
    public static ContactSheetRenderResult Ok(int? width, int? height) => new(true, width, height, null);
    public static ContactSheetRenderResult Failed(string error) => new(false, null, null, error);
}

public interface IContactSheetRenderer
{
    Task<ContactSheetRenderResult> RenderAsync(IReadOnlyList<ContactSheetTile> tiles, ContactSheetLayout layout, string outputPath, bool labels, CancellationToken cancellationToken);
}

/// <summary>ffmpeg-backed renderer: concat demuxer → scale/pad → (sendcmd+drawtext) → tile.</summary>
public sealed class FfmpegContactSheetRenderer : IContactSheetRenderer
{
    private readonly DependencyResolver dependencies;
    private readonly ProcessRunner processRunner;

    public FfmpegContactSheetRenderer(DependencyResolver dependencies, ProcessRunner processRunner)
    {
        this.dependencies = dependencies;
        this.processRunner = processRunner;
    }

    public async Task<ContactSheetRenderResult> RenderAsync(IReadOnlyList<ContactSheetTile> tiles, ContactSheetLayout layout, string outputPath, bool labels, CancellationToken cancellationToken)
    {
        if (tiles.Count == 0)
        {
            return ContactSheetRenderResult.Failed("no tiles");
        }

        var ffmpeg = dependencies.RequireFfmpeg("rendering contact sheets");
        var workDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, ".work");
        Directory.CreateDirectory(workDirectory);
        var token = Guid.NewGuid().ToString("N")[..8];
        var listPath = Path.Combine(workDirectory, $"list-{token}.txt");
        var commandsPath = Path.Combine(workDirectory, $"labels-{token}.txt");

        try
        {
            await File.WriteAllTextAsync(listPath, BuildConcatList(tiles), cancellationToken).ConfigureAwait(false);

            var filter = new StringBuilder();
            filter.Append(CultureInfo.InvariantCulture, $"scale={layout.ThumbWidth}:{layout.ThumbHeight}:force_original_aspect_ratio=decrease,");
            filter.Append(CultureInfo.InvariantCulture, $"pad={layout.ThumbWidth}:{layout.ThumbHeight}:(ow-iw)/2:(oh-ih)/2:color=black");
            if (labels)
            {
                var font = FindFontFile();
                await File.WriteAllTextAsync(commandsPath, BuildSendCmdScript(tiles), cancellationToken).ConfigureAwait(false);
                var fontSize = Math.Clamp(layout.ThumbHeight / 9, 10, 40);
                filter.Append(CultureInfo.InvariantCulture, $",sendcmd=f='{EscapeFilterPath(commandsPath)}'");
                filter.Append(CultureInfo.InvariantCulture, $",drawtext={(font is null ? "font=Sans" : $"fontfile='{EscapeFilterPath(font)}'")}:text='':fontsize={fontSize}:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=3:x=6:y=6");
            }
            filter.Append(CultureInfo.InvariantCulture, $",tile={layout.Columns}x{layout.Rows}:padding={layout.Padding}:margin={layout.Margin}:color=0x202020");

            var result = await processRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-f", "concat",
                    "-safe", "0",
                    "-i", listPath,
                    "-vf", filter.ToString(),
                    "-frames:v", "1",
                    "-q:v", "3",
                    "-y",
                    outputPath
                ],
                timeout: TimeoutsConfig.Resolve(dependencies.Timeouts.FfmpegPassSeconds, 900),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                return ContactSheetRenderResult.Failed(ProcessTimeoutException.Tail(result.StandardError, 5));
            }

            var width = layout.Columns * layout.ThumbWidth + (layout.Columns - 1) * layout.Padding + 2 * layout.Margin;
            var height = layout.Rows * layout.ThumbHeight + (layout.Rows - 1) * layout.Padding + 2 * layout.Margin;
            return ContactSheetRenderResult.Ok(width, height);
        }
        finally
        {
            TryDelete(listPath);
            TryDelete(commandsPath);
            try
            {
                if (Directory.Exists(workDirectory) && !Directory.EnumerateFileSystemEntries(workDirectory).Any())
                {
                    Directory.Delete(workDirectory);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>Concat demuxer script: each image shown for exactly one second so frame i sits at pts i.</summary>
    internal static string BuildConcatList(IReadOnlyList<ContactSheetTile> tiles)
    {
        var builder = new StringBuilder("ffconcat version 1.0\n");
        foreach (var tile in tiles)
        {
            builder.Append("file '").Append(EscapeConcatPath(tile.ImagePath)).Append("'\n");
            builder.Append("duration 1\n");
        }

        // Do NOT repeat the last entry (the usual trick to make the concat demuxer honour the
        // final duration): the repeat is decoded as an extra frame and the tile filter would
        // render it as a duplicate cell. Only the frame count matters here.
        return builder.ToString();
    }

    /// <summary>sendcmd script: re-initialise drawtext with the tile's label at that tile's pts.</summary>
    internal static string BuildSendCmdScript(IReadOnlyList<ContactSheetTile> tiles)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < tiles.Count; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(" drawtext reinit 'text=")
                .Append(EscapeSendCmdText(tiles[i].Label))
                .Append("';\n");
        }

        return builder.ToString();
    }

    internal static string? FindFontFile()
    {
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            candidates.AddRange(new[] { "consola.ttf", "segoeui.ttf", "arial.ttf", "tahoma.ttf" }.Select(name => Path.Combine(fonts, name)));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.AddRange(["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Helvetica.ttc", "/Library/Fonts/Arial.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf"]);
        }
        else
        {
            candidates.AddRange([
                "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf",
                "/usr/share/fonts/truetype/freefont/FreeMono.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
            ]);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EscapeConcatPath(string path) => path.Replace('\\', '/').Replace("'", "'\\''");

    /// <summary>Filter-graph option value: forward slashes, and escape the characters the graph parser treats specially.</summary>
    internal static string EscapeFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    /// <summary>sendcmd command argument: keep to a conservative character set; escape quotes, colons and commas.</summary>
    internal static string EscapeSendCmdText(string text)
        => text.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:").Replace(",", "\\,").Replace(";", "\\;").Replace("%", "%%");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
