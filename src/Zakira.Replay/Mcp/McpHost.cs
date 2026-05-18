using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Zakira.Replay.Core;

namespace Zakira.Replay.Mcp;

/// <summary>
/// Builds and runs the Zakira.Replay MCP server. Replaces the hand-rolled
/// JSON-RPC dispatcher from 0.8.x with the official <c>ModelContextProtocol</c>
/// SDK. Supported transports:
///
/// <list type="bullet">
///   <item><description><c>stdio</c> (default): one MCP session over the current
///     process's standard input/output. Used by Claude Desktop / Cursor /
///     VS Code Copilot.</description></item>
///   <item><description><c>http</c>: Streamable HTTP transport hosted on
///     <c>http://localhost:&lt;port&gt;/</c>. Use for hosted agent platforms.</description></item>
///   <item><description><c>sse</c>: same as <c>http</c> (the SDK exposes Streamable HTTP
///     which the spec subsumes the SSE transport into); kept as an alias for
///     legacy clients.</description></item>
/// </list>
/// </summary>
public static class McpHost
{
    public const string TransportStdio = "stdio";
    public const string TransportHttp = "http";
    public const string TransportSse = "sse";

    public const int DefaultHttpPort = 8765;

    public static Task<int> RunAsync(
        string transport,
        int port,
        CancellationToken cancellationToken)
    {
        return transport switch
        {
            TransportHttp or TransportSse => RunHttpAsync(port, cancellationToken),
            TransportStdio or "" or null => RunStdioAsync(cancellationToken),
            _ => throw new ReplayException($"Unknown MCP transport '{transport}'. Use one of: stdio, http, sse.")
        };
    }

    private static async Task<int> RunStdioAsync(CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();

        // Stdio is the LLM channel; all logs must go to stderr or they would corrupt
        // the JSON-RPC framing on stdout.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services.AddReplay();
        builder.Services.AddSingleton<McpJobManager>(provider => new McpJobManager(
            () => provider.GetRequiredService<AnalysisPipeline>()));

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Zakira.Replay",
                    Version = ReplayVersion.Current
                };
            })
            .WithStdioServerTransport()
            .WithTools<ReplayTools>()
            .WithResources<ReplayResources>();

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunHttpAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddReplay();
        builder.Services.AddSingleton<McpJobManager>(provider => new McpJobManager(
            () => provider.GetRequiredService<AnalysisPipeline>()));

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Zakira.Replay",
                    Version = ReplayVersion.Current
                };
            })
            .WithHttpTransport(httpOptions =>
            {
                // Stateless is fine here: every tool/resource is idempotent or owns its
                // own persistence (run directories, job manager). Stateless makes load
                // balancing across replicas trivial for hosted agent platforms.
                httpOptions.Stateless = true;
            })
            .WithTools<ReplayTools>()
            .WithResources<ReplayResources>();

        var app = builder.Build();
        app.MapMcp();

        var endpoint = $"http://127.0.0.1:{port}";
        await app.RunAsync(endpoint).WaitAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
