using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Exercises the real <c>IConfiguration.Bind</c> path (<see cref="ServiceCollectionExtensions.AddMomosWorker"/>
/// uses this, not <see cref="Options.Create{TOptions}"/>) — every other test in
/// this project bypasses binding entirely, which never proved the
/// <see langword="required"/> properties on <see cref="AnthropicLlmOptions"/>
/// behave as expected against a missing or partial configuration section.
/// </summary>
public class AnthropicLlmOptionsBindingTests
{
    private static IOptions<AnthropicLlmOptions> BindFrom(IDictionary<string, string?> configData)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services
            .AddOptions<AnthropicLlmOptions>()
            .Bind(configuration.GetSection(AnthropicLlmOptions.SectionName));

        return services.BuildServiceProvider().GetRequiredService<IOptions<AnthropicLlmOptions>>();
    }

    [Fact]
    public void Bind_WithNoSection_DoesNotThrowAndLeavesFieldsNull()
    {
        // The binder does not enforce `required` at runtime (that's a compile-time/
        // object-initializer construct) — it leaves unmatched properties at their
        // type default (null, not string.Empty). Confirmed here so
        // AnthropicChatClientProvider's string.IsNullOrWhiteSpace checks are known
        // to be the only guard — and that they're null-safe, not a backstop for a
        // binder-level exception that never happens.
        var options = BindFrom(new Dictionary<string, string?>());

        Assert.Null(options.Value.ApiKey);
        Assert.Null(options.Value.Model);
    }

    [Fact]
    public void Bind_WithFullSection_PopulatesFields()
    {
        var options = BindFrom(new Dictionary<string, string?>
        {
            [$"{AnthropicLlmOptions.SectionName}:ApiKey"] = "sk-ant-test",
            [$"{AnthropicLlmOptions.SectionName}:Model"] = "claude-sonnet-4-5",
        });

        Assert.Equal("sk-ant-test", options.Value.ApiKey);
        Assert.Equal("claude-sonnet-4-5", options.Value.Model);
    }
}
