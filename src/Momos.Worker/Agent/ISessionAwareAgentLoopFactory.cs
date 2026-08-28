using IronHive.Agent.Loop;
using Momos.Worker.Execution;

namespace Momos.Worker.Agent;

/// <summary>
/// Extends <see cref="IAgentLoopFactory"/> with an overload that builds the loop against
/// an execution session the caller already created (and owns the lifecycle of) — for
/// <see cref="PullExecutionBackgroundService"/>, which needs to check out the target repo
/// into the session's workspace before the agent's first turn, so the session has to exist
/// before the loop does. <see cref="IAgentLoopFactory.CreateAsync(AgentLoopFactoryOptions,System.Threading.CancellationToken)"/>
/// still works standalone for callers with no repo to check out first (see
/// <see cref="MomosAgentLoopFactory"/>) — it opens and owns its own session.
/// </summary>
public interface ISessionAwareAgentLoopFactory : IAgentLoopFactory
{
    /// <summary>
    /// Builds a loop against a session the caller owns, plus the <see cref="FindingSink"/>
    /// the loop's <see cref="FindingReportingTools"/> tool reports into — read it after
    /// <see cref="IAgentLoop.RunAsync(string,System.Threading.CancellationToken)"/> completes.
    /// </summary>
    Task<(IAgentLoop Loop, FindingSink Findings)> CreateAsync(
        AgentLoopFactoryOptions options,
        ExecutionSessionHandle session,
        CancellationToken cancellationToken = default);
}
