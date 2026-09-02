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
    /// <param name="projectId">
    /// When set, the built loop also gets a <see cref="Momos.Worker.Execution.KnowledgeQueryTools"/>
    /// tool scoped to this project. <see cref="AgentLoopFactoryOptions"/> is <c>IronHive.Agent</c>'s
    /// own type, not Momos's, so this travels as a separate parameter on Momos's own interface
    /// method rather than as a new property on that type.
    /// </param>
    Task<(IAgentLoop Loop, FindingSink Findings)> CreateAsync(
        AgentLoopFactoryOptions options,
        ExecutionSessionHandle session,
        Guid? projectId = null,
        CancellationToken cancellationToken = default);
}
