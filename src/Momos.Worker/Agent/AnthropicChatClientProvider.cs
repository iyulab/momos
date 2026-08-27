using IronHive.Agent.Providers;
using IronHive.Core.Microsoft;
using IronHive.Providers.Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Momos.Worker.Agent;

/// <summary>
/// Bridges IronHive's own Anthropic message generator (<c>IronHive.Providers.Anthropic</c>)
/// into <c>IronHive.Agent</c>'s <see cref="IChatClientProvider"/> extension point
/// (ADR-0006) via <see cref="ChatClientAdapter"/> — the composition
/// <c>IronHive.Agent</c> expects consumers to bring themselves, since it ships
/// no provider implementations of its own.
/// </summary>
public sealed class AnthropicChatClientProvider(IOptions<AnthropicLlmOptions> options) : IChatClientProvider
{
    private AnthropicMessageGenerator? _generator;

    public string ProviderName => "anthropic";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                $"'{AnthropicLlmOptions.SectionName}:ApiKey' is required to create the Anthropic chat client.");
        }

        var model = modelOverride ?? config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"'{AnthropicLlmOptions.SectionName}:Model' is required to create the Anthropic chat client.");
        }

        _generator ??= new AnthropicMessageGenerator(new AnthropicConfig { ApiKey = config.ApiKey });

        IChatClient chatClient = new ChatClientAdapter(_generator, model, ProviderName);
        return Task.FromResult(chatClient);
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAvailable);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Model listing is out of scope for the Walking Skeleton (cycle-06) — no caller needs it yet.");

    public ValueTask DisposeAsync()
    {
        _generator?.Dispose();
        return ValueTask.CompletedTask;
    }
}
