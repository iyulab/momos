using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Exercises the real <c>IConfiguration.Bind</c> path (<see cref="ServiceCollectionExtensions.AddMomosWorker"/>
/// uses this, not <see cref="Options.Create{TOptions}"/>) — every other test in
/// this project bypasses binding entirely, which never proved the
/// <see langword="required"/> properties on <see cref="GpuStackLlmOptions"/>
/// behave as expected against a missing or partial configuration section.
/// </summary>
public class GpuStackLlmOptionsBindingTests
{
    private static IOptions<GpuStackLlmOptions> BindFrom(IDictionary<string, string?> configData)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services
            .AddOptions<GpuStackLlmOptions>()
            .Bind(configuration.GetSection(GpuStackLlmOptions.SectionName));

        return services.BuildServiceProvider().GetRequiredService<IOptions<GpuStackLlmOptions>>();
    }

    [Fact]
    public void Bind_WithNoSection_DoesNotThrowAndLeavesFieldsNull()
    {
        var options = BindFrom(new Dictionary<string, string?>());

        Assert.Null(options.Value.Endpoint);
        Assert.Null(options.Value.ApiKey);
        Assert.Null(options.Value.Model);
    }

    [Fact]
    public void Bind_WithFullSection_PopulatesFields()
    {
        var options = BindFrom(new Dictionary<string, string?>
        {
            [$"{GpuStackLlmOptions.SectionName}:Endpoint"] = "http://gpustack.example.internal:9443",
            [$"{GpuStackLlmOptions.SectionName}:ApiKey"] = "gpustack-test-key",
            [$"{GpuStackLlmOptions.SectionName}:Model"] = "qwen2.5-coder",
        });

        Assert.Equal("http://gpustack.example.internal:9443", options.Value.Endpoint);
        Assert.Equal("gpustack-test-key", options.Value.ApiKey);
        Assert.Equal("qwen2.5-coder", options.Value.Model);
    }
}
