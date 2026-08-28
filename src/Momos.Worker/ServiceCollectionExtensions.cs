using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Sessions;
using CodeBeaker.Core.Storage;
using CodeBeaker.Runtimes.Native;
using IronHive.Agent.Context;
using IronHive.Agent.Extensions;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;
using Momos.Worker.Execution;

namespace Momos.Worker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMomosWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<GpuStackLlmOptions>()
            .Bind(configuration.GetSection(GpuStackLlmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IChatClientProvider, GpuStackChatClientProvider>();

        // ADR-0009 decisions 1·2·3(=B): code-beaker as an in-process execution sandbox.
        // Only NativeProcessRuntime is registered — Worker doesn't detect a checked-out
        // repo's language yet, so registering Docker/Node/Python runtimes now would add
        // untestable, unreachable code (YAGNI).
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IExecutionRuntime, NativeProcessRuntime>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IExecutionRuntimeProvider, CodeBeakerExecutionRuntimeProvider>();

        services.AddIronHiveAgentEngine();

        // Empty by default — connects only what an operator explicitly lists.
        // Enabling a Computer Use tool and its permission posture is a
        // separate, human decision this wiring does not make (see
        // McpPluginStartupService).
        services
            .AddOptions<McpPluginsConfig>()
            .Bind(configuration.GetSection(McpPluginStartupService.SectionName));
        services.AddHostedService<McpPluginStartupService>();

        services
            .AddOptions<HostClientOptions>()
            .Bind(configuration.GetSection(HostClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services
            .AddOptions<PullExecutionOptions>()
            .Bind(configuration.GetSection(PullExecutionOptions.SectionName));

        services.AddHttpClient<IHostApiClient, HostApiClient>((sp, client) =>
        {
            var hostOptions = sp.GetRequiredService<IOptions<HostClientOptions>>().Value;
            client.BaseAddress = new Uri(hostOptions.BaseUrl);
        });
        services.AddHostedService<PullExecutionBackgroundService>();

        return services;
    }

    /// <summary>
    /// Wires the provider-agnostic <c>ironhive-agent</c> plumbing that
    /// <c>AddIronHiveAgent()</c> itself leaves to the consumer — tool
    /// retrieval and chat-client resolution across the registered
    /// <see cref="IChatClientProvider"/>s. Callers must register at least one
    /// <see cref="IChatClientProvider"/> before calling this.
    /// </summary>
    public static IServiceCollection AddIronHiveAgentEngine(this IServiceCollection services)
    {
        services.AddIronHiveAgent();

        services.AddSingleton<IToolRetriever, KeywordToolRetriever>();
        services.AddSingleton<IChatClientFactory>(sp =>
        {
            var providers = sp.GetServices<IChatClientProvider>().ToDictionary(p => p.ProviderName);
            if (providers.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No {nameof(IChatClientProvider)} is registered — register at least one before resolving {nameof(IChatClientFactory)}.");
            }

            return new ChatClientFactory(providers, providers.Values.First(), static client => client);
        });
        services.AddSingleton<MomosAgentLoopFactory>();
        services.AddSingleton<IAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());
        services.AddSingleton<ISessionAwareAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());

        return services;
    }
}
