using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests;

/// <summary>
/// Covers the real DI graph <c>AddMomosWorker</c> wires for ADR-0009 decisions 1·2·3(=B) —
/// the other tests in this project exercise the port/adapter logic against fakes
/// (<see cref="Execution.FakeExecutionRuntimeProvider"/>, <c>FakeSessionManager</c>);
/// this one proves the actual registrations (<c>SessionManager</c>, <c>NativeProcessRuntime</c>,
/// <c>InMemorySessionStore</c>) resolve and run a real OS process end to end.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddMomosWorker_ResolvesARealExecutionRuntimeProvider_ThatRunsANativeCommand()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddMomosWorker(configuration);
        await using var provider = services.BuildServiceProvider();

        var executionRuntimeProvider = provider.GetRequiredService<IExecutionRuntimeProvider>();
        var session = await executionRuntimeProvider.CreateSessionAsync(new ExecutionSessionRequest("native"));
        try
        {
            var result = await executionRuntimeProvider.ExecuteAsync(session, new ExecutionCommand("dotnet", ["--version"]));
            Assert.True(result.Success, result.Error);
        }
        finally
        {
            await executionRuntimeProvider.CloseSessionAsync(session);
        }
    }
}
