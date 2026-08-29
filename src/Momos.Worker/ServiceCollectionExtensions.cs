using System.Net.Http.Headers;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Sessions;
using CodeBeaker.Core.Storage;
using CodeBeaker.Runtimes.Docker;
using CodeBeaker.Runtimes.Native;
using IronHive.Agent.Context;
using IronHive.Agent.Extensions;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
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
        // Docker and native are both registered so RuntimeSelector's Security preference
        // has an isolated runtime to actually pick when one is reachable, instead of
        // always resolving to the unsandboxed native fallback — leaving only native
        // registered made that preference a no-op regardless of host state. Node/Python
        // stay unregistered: Worker doesn't detect a checked-out repo's language yet
        // (still "dotnet" below), so they'd add untestable, unreachable code (YAGNI).
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<IExecutionRuntime, NativeProcessRuntime>();
        services.AddSingleton<IExecutionRuntime, DockerRuntime>();
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
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hostOptions.ApiKey);
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

            // Without UseFunctionInvocation, AgentLoop.RunAsync only extracts a requested
            // FunctionCallContent into an unexecuted ToolCallResult — nothing ever calls
            // the AITool or reports its result back to the model. This decorator is what
            // actually runs CodeExecutionTools/FindingReportingTools and feeds their
            // results back for the next turn.
            return new ChatClientFactory(providers, providers.Values.First(),
                client => client.AsBuilder().UseFunctionInvocation().Build(sp));
        });
        services.AddSingleton<MomosAgentLoopFactory>();
        services.AddSingleton<IAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());
        services.AddSingleton<ISessionAwareAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());

        return services;
    }
}
