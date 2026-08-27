using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Momos.Worker;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Proves <c>AddMomosWorker</c>'s <c>ValidateOnStart()</c> actually surfaces a
/// missing GPUStack configuration at host boot, not only on the first agent-loop
/// creation.
/// </summary>
public class GpuStackLlmOptionsStartupValidationTests
{
    [Fact]
    public async Task StartAsync_WithMissingConfig_ThrowsOptionsValidationException()
    {
        // Fully qualified: this project also references Momos.Host (for the ADR-0008
        // Worker-side e2e test), and the Momos.Host namespace now resolves via
        // enclosing-namespace lookup ahead of the `using Microsoft.Extensions.Hosting`
        // import — an unqualified `Host` here would bind to that namespace, not this class.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMomosWorker(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
