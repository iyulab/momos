using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public class AnthropicChatClientProviderTests
{
    [Fact]
    public async Task GetChatClientAsync_WithMissingApiKey_ThrowsInvalidOperationException()
    {
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicLlmOptions
        {
            ApiKey = string.Empty,
            Model = "claude-sonnet-4-5",
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithMissingModel_ThrowsInvalidOperationException()
    {
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicLlmOptions
        {
            ApiKey = "sk-ant-test",
            Model = string.Empty,
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithValidConfig_ReturnsChatClient()
    {
        // No network call happens here — this only proves the IronHive.Core →
        // ChatClientAdapter wiring (ADR-0006) assembles correctly. A live call
        // needs a real API key (BLOCKED-ITEM, cycle-06 — see cycle log).
        var provider = new AnthropicChatClientProvider(Options.Create(new AnthropicLlmOptions
        {
            ApiKey = "sk-ant-test",
            Model = "claude-sonnet-4-5",
        }));

        var chatClient = await provider.GetChatClientAsync();

        Assert.NotNull(chatClient);
    }

    [Fact]
    public void IsAvailable_ReflectsWhetherApiKeyIsConfigured()
    {
        var withKey = new AnthropicChatClientProvider(Options.Create(new AnthropicLlmOptions
        {
            ApiKey = "sk-ant-test",
            Model = "claude-sonnet-4-5",
        }));
        var withoutKey = new AnthropicChatClientProvider(Options.Create(new AnthropicLlmOptions
        {
            ApiKey = string.Empty,
            Model = "claude-sonnet-4-5",
        }));

        Assert.True(withKey.IsAvailable);
        Assert.False(withoutKey.IsAvailable);
    }
}
