using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public class GpuStackChatClientProviderTests
{
    private static GpuStackLlmOptions ValidOptions() => new()
    {
        Endpoint = "http://gpustack.example.internal:9443",
        ApiKey = "gpustack-test-key",
        Model = "qwen2.5-coder",
    };

    [Fact]
    public async Task GetChatClientAsync_WithMissingEndpoint_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.Endpoint = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithMissingApiKey_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.ApiKey = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithMissingModel_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.Model = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithValidConfig_ReturnsChatClient()
    {
        // No network call happens here — this only proves the IronHive.Core →
        // ChatClientAdapter wiring assembles correctly.
        var provider = new GpuStackChatClientProvider(Options.Create(ValidOptions()));

        var chatClient = await provider.GetChatClientAsync();

        Assert.NotNull(chatClient);
    }

    [Fact]
    public void IsAvailable_ReflectsWhetherEndpointAndApiKeyAreConfigured()
    {
        var withBoth = new GpuStackChatClientProvider(Options.Create(ValidOptions()));

        var missingEndpoint = ValidOptions();
        missingEndpoint.Endpoint = string.Empty;
        var withoutEndpoint = new GpuStackChatClientProvider(Options.Create(missingEndpoint));

        var missingKey = ValidOptions();
        missingKey.ApiKey = string.Empty;
        var withoutKey = new GpuStackChatClientProvider(Options.Create(missingKey));

        Assert.True(withBoth.IsAvailable);
        Assert.False(withoutEndpoint.IsAvailable);
        Assert.False(withoutKey.IsAvailable);
    }
}
