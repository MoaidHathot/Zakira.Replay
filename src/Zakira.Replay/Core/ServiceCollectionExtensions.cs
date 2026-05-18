using Microsoft.Extensions.DependencyInjection;

namespace Zakira.Replay.Core;

/// <summary>
/// Registers the Zakira.Replay services with a <see cref="IServiceCollection"/>. Both the
/// CLI (System.CommandLine actions) and the MCP server (ModelContextProtocol tool/resource
/// types) consume the same DI container; this is the single registration entry point.
///
/// We resolve config / dependency layout once per scope so subprocesses, ONNX paths, and
/// portable directories are consistent across a single CLI invocation or MCP request.
/// Pipeline and per-source services (ClipExtractionService, FrameCaptureService, …) stay
/// transient because each call may target a different source URL.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReplay(this IServiceCollection services)
    {
        // Configuration + dependency layout
        services.AddSingleton<ConfigStore>();
        services.AddSingleton(provider => provider.GetRequiredService<ConfigStore>().Load());
        services.AddSingleton(provider => new DependencyResolver(provider.GetRequiredService<ReplayConfig>()));
        services.AddSingleton<ProcessRunner>();

        // Artifact store roots at the configured runs directory (env var > config >
        // <cwd>/runs default). Resolving here means every DI consumer — pipeline, MCP host,
        // runs CLI group — sees the same answer.
        services.AddSingleton(provider => new ArtifactStore(
            ArtifactStore.ResolveRootDirectory(provider.GetService<ReplayConfig>())));

        // Subprocess clients
        services.AddSingleton<IYtDlpClient>(provider => new YtDlpClient(
            provider.GetRequiredService<DependencyResolver>(),
            provider.GetRequiredService<ProcessRunner>()));
        services.AddSingleton<IFfmpegClient>(provider => new FfmpegClient(
            provider.GetRequiredService<DependencyResolver>(),
            provider.GetRequiredService<ProcessRunner>()));
        services.AddSingleton<IBrowserVideoCaptureClient>(provider =>
            new PlaywrightVideoCaptureClient(provider.GetRequiredService<DependencyResolver>()));

        // The analysis pipeline owns the orchestration; LLM providers are resolved lazily by
        // name (via LlmProviderFactory) so a single invocation can switch providers per call.
        services.AddTransient(provider => new AnalysisPipeline(
            provider.GetRequiredService<ArtifactStore>(),
            provider.GetRequiredService<IYtDlpClient>(),
            provider.GetRequiredService<IFfmpegClient>(),
            (string? name) => LlmProviderFactory.TryCreate(name),
            provider.GetRequiredService<IBrowserVideoCaptureClient>()));

        // Pipeline factory: the MCP job manager and AnalysisQueue need to spin up an
        // independent pipeline per job, not share one across the host lifetime.
        services.AddSingleton<Func<AnalysisPipeline>>(provider =>
            () => provider.GetRequiredService<AnalysisPipeline>());

        services.AddTransient(provider => new ClipExtractionService(
            provider.GetRequiredService<ArtifactStore>(),
            provider.GetRequiredService<IYtDlpClient>(),
            provider.GetRequiredService<IFfmpegClient>()));

        services.AddTransient(provider => new FrameCaptureService(
            provider.GetRequiredService<ArtifactStore>(),
            provider.GetRequiredService<IYtDlpClient>(),
            provider.GetRequiredService<IFfmpegClient>()));

        services.AddTransient(provider => new DiscoveryService(
            provider.GetRequiredService<DependencyResolver>(),
            provider.GetRequiredService<ProcessRunner>()));

        services.AddTransient<SearchIndexService>();
        services.AddTransient<ChapterBuilder>();
        services.AddTransient<EvidenceAlignmentService>();

        return services;
    }
}
