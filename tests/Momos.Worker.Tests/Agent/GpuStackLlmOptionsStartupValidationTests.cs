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
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMomosWorker(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
