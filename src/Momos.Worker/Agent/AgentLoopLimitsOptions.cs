namespace Momos.Worker.Agent;

/// <summary>
/// Safety caps on a single inspection's agent loop — bound how much of the shared LLM
/// endpoint one runaway session can consume. <see cref="MaxIterationsPerRequest"/> feeds
/// <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c> (Microsoft.Extensions.AI's
/// own tool-call loop breaker); <see cref="MaxSessionTokens"/> feeds
/// <see cref="UsageLimitingChatClient"/>, since IronHive.Agent's own
/// <c>AgentServicesOptions.UsageLimits</c> registers a <c>UsageLimiter</c> in DI that
/// <c>AgentLoop</c> never actually consults (confirmed by reflection: no such field on the
/// type) — the enforcement point has to live on the momos side of the chat client pipeline.
/// </summary>
public sealed class AgentLoopLimitsOptions
{
    public const string SectionName = "Momos:Worker:AgentLoopLimits";

    /// <summary>
    /// Upper bound on tool-call round trips within one <c>RunAsync</c> call. 40 is
    /// <c>FunctionInvokingChatClient</c>'s own default (confirmed empirically) — made
    /// explicit here rather than left to an implicit framework default that could change
    /// silently on a future Microsoft.Extensions.AI upgrade.
    /// </summary>
    public int MaxIterationsPerRequest { get; set; } = 40;

    /// <summary>
    /// Hard stop on total tokens (input+output) one inspection session may consume against
    /// the shared LLM endpoint. Provisional: no measured worst-case session size exists yet
    /// (Walking Skeleton hasn't run a pilot long enough to observe one) — 1,000,000 is a
    /// conservative backstop, not a tuned figure; revisit once real session token counts are
    /// observed.
    /// </summary>
    public int MaxSessionTokens { get; set; } = 1_000_000;
}
