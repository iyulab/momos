using IronHive.Agent.Providers;
using IronHive.Core.Microsoft;
using IronHive.Providers.OpenAI.Compatible.GpuStack;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Momos.Worker.Agent;

/// <summary>
/// Bridges IronHive's GPUStack message generator (<c>IronHive.Providers.OpenAI.Compatible</c>)
/// into <c>IronHive.Agent</c>'s <see cref="IChatClientProvider"/> extension point
/// via <see cref="ChatClientAdapter"/> — the composition
/// <c>IronHive.Agent</c> expects consumers to bring themselves, since it ships
/// no provider implementations of its own.
/// </summary>
public sealed class GpuStackChatClientProvider(IOptions<GpuStackLlmOptions> options) : IChatClientProvider
{
    private GpuStackMessageGenerator? _generator;

    public string ProviderName => "gpustack";

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(options.Value.Endpoint) && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            throw new InvalidOperationException(
                $"'{GpuStackLlmOptions.SectionName}:Endpoint' is required to create the GPUStack chat client.");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                $"'{GpuStackLlmOptions.SectionName}:ApiKey' is required to create the GPUStack chat client.");
        }

        var model = modelOverride ?? config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"'{GpuStackLlmOptions.SectionName}:Model' is required to create the GPUStack chat client.");
        }

        _generator ??= new GpuStackMessageGenerator(new GpuStackConfig
        {
            BaseUrl = config.Endpoint,
            ApiKey = config.ApiKey,
        });

        IChatClient chatClient = new ChatClientAdapter(_generator, model, ProviderName);
        return Task.FromResult(chatClient);
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAvailable);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Model listing is not implemented — no caller needs it yet.");

    public ValueTask DisposeAsync()
    {
        _generator?.Dispose();
        return ValueTask.CompletedTask;
    }
}
