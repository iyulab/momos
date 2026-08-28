using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.DependencyInjection;
using Momos.Worker;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Proves Momos's own agent wiring (<c>AddIronHiveAgentEngine</c>) runs a
/// full turn end-to-end, with a fake provider standing in for the real
/// LLM backend — no network required.
/// </summary>
public class AgentLoopSmokeTests
{
    [Fact]
    public async Task RunAsync_ThroughDiWiredAgentLoop_ReturnsFakeProviderReply()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new FakeChatClientProvider("hello from momos"));
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddIronHiveAgentEngine();
        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IAgentLoopFactory>();
        var agentLoop = await factory.CreateAsync(new AgentLoopFactoryOptions
        {
            Provider = "fake",
            Model = "fake-model",
        });

        var response = await agentLoop.RunAsync("hi");

        Assert.Contains("hello from momos", response.Content);
    }
}
