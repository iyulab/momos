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
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;
using Momos.Worker.Execution;
using Momos.Worker.SelfUpdate;

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

        // code-beaker as an in-process execution sandbox: Docker and native are both
        // registered so RuntimeSelector's Security preference
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

        var agentLoopLimits = configuration.GetSection(AgentLoopLimitsOptions.SectionName).Get<AgentLoopLimitsOptions>()
            ?? new AgentLoopLimitsOptions();
        services.AddIronHiveAgentEngine(agentLoopLimits);

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

        services
            .AddOptions<WorkerSelfUpdateOptions>()
            .Bind(configuration.GetSection(WorkerSelfUpdateOptions.SectionName));
        services.AddHttpClient<IWorkerSelfUpdater, WorkerSelfUpdater>(client =>
        {
            // GitHub's REST API rejects requests with no User-Agent header (returns 403) —
            // unlike IHostApiClient's HttpClient above, this one talks to api.github.com.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("momos-worker", WorkerVersionInfo.Version));
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
    public static IServiceCollection AddIronHiveAgentEngine(this IServiceCollection services, AgentLoopLimitsOptions? limits = null)
    {
        limits ??= new AgentLoopLimitsOptions();

        services.AddIronHiveAgent(o =>
        {
            // AgentLoop never actually reads this (confirmed by reflection: no
            // IUsageLimiter/UsageLimitsConfig field on the type) — set it anyway so the
            // UsageLimiter this registers in DI (below) carries momos's real limit, and so
            // a future IronHive.Agent version that does wire it through picks it up for
            // free. MaxSessionCost is left at the library default: GPUStack's self-hosted
            // model id has no pricing entry (confirmed: EstimatedCostUsd stays 0.00m), so a
            // cost cap would never trip for momos's current deployment — token count is the
            // real guard, see UsageLimitingChatClient.
            o.UsageLimits = new UsageLimitsConfig { MaxSessionTokens = limits.MaxSessionTokens, StopOnLimit = true };
        });

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
            //
            // ChatClientBuilder layers in call order — the *first* .Use()/.UseXxx() call
            // ends up outermost, the *last* ends up closest to the raw client (confirmed
            // empirically: reversing this once made UsageLimitingChatClient wrap the whole
            // multi-iteration FunctionInvokingChatClient call instead of each iteration of
            // it, so the pre-call check only ran once per RunAsync and never tripped).
            // UseFunctionInvocation must therefore come *first* so
            // UsageLimitingChatClient — registered second — ends up wrapping the raw
            // provider client directly, called once per tool-call iteration rather than
            // once per top-level RunAsync; that's the only vantage point that can stop a
            // runaway loop mid-flight instead of after every iteration already ran.
            var usageLimiter = sp.GetRequiredService<UsageLimiter>();
            return new ChatClientFactory(providers, providers.Values.First(),
                client => client.AsBuilder()
                    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = limits.MaxIterationsPerRequest)
                    .Use(inner => new UsageLimitingChatClient(inner, usageLimiter))
                    .Build(sp));
        });
        services.AddSingleton<MomosAgentLoopFactory>();
        services.AddSingleton<IAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());
        services.AddSingleton<ISessionAwareAgentLoopFactory>(sp => sp.GetRequiredService<MomosAgentLoopFactory>());

        return services;
    }
}
