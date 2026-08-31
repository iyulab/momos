using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Agent;

/// <summary>
/// Wraps the underlying provider chat client so <see cref="UsageLimiter"/> — an
/// IronHive.Agent primitive that <c>AgentServicesOptions.UsageLimits</c> registers in DI but
/// that <c>AgentLoop</c> never actually consults — is checked before, and updated after,
/// every model call. Positioned inside <c>UseFunctionInvocation()</c> in the builder chain
/// (see <c>ServiceCollectionExtensions.AddIronHiveAgentEngine</c>) so this runs once per
/// tool-call iteration, not once per top-level <c>RunAsync</c> — the only vantage point that
/// can stop a runaway loop mid-flight rather than after every iteration already ran.
/// </summary>
public sealed class UsageLimitingChatClient(IChatClient innerClient, UsageLimiter usageLimiter)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfLimitExceeded();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Record(response.Usage);
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Pre-check only — momos has no streaming caller today (RunStreamingAsync is
        // unused), so per-chunk usage accounting would be speculative infrastructure. This
        // still stops a new streaming turn from starting once a prior turn already tripped
        // the limit; add accumulation here if/when a streaming caller shows up.
        ThrowIfLimitExceeded();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void ThrowIfLimitExceeded()
    {
        var result = usageLimiter.CheckLimits();
        if (result.ShouldStop)
        {
            throw new UsageLimitExceededException(result.Message);
        }
    }

    private void Record(UsageDetails? usage)
    {
        if (usage?.TotalTokenCount is { } tokens)
        {
            // Cost is left at 0 — MaxSessionCost is an inert guard for this deployment (see
            // AgentLoopLimitsOptions): GPUStack's self-hosted model id has no pricing entry,
            // so estimated cost stays $0.00 regardless of usage. Token count is the real guard.
            usageLimiter.RecordTokenUsage((int)Math.Min(tokens, int.MaxValue), cost: 0m);
        }
    }
}
