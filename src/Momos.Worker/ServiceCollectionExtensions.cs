using IronHive.Agent.Context;
using IronHive.Agent.Extensions;
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Momos.Worker.Agent;

namespace Momos.Worker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMomosWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<AnthropicLlmOptions>()
            .Bind(configuration.GetSection(AnthropicLlmOptions.SectionName));

        services.AddSingleton<IChatClientProvider, AnthropicChatClientProvider>();
        services.AddIronHiveAgentEngine();

        return services;
    }

    /// <summary>
    /// Wires the provider-agnostic <c>ironhive-agent</c> plumbing (ADR-0006)
    /// that <c>AddIronHiveAgent()</c> itself leaves to the consumer — tool
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
        services.AddSingleton<IAgentLoopFactory, MomosAgentLoopFactory>();

        return services;
    }
}
