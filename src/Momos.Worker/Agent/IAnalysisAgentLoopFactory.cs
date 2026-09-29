using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using Momos.Worker.Execution;

namespace Momos.Worker.Agent;

/// <summary>
/// Builds the agent loop for one analysis pass: the same command and knowledge tools an
/// inspection has, the pass's proposal tools, and no way to report a finding — an analysis
/// describes a project, it does not judge it. Every tool of the pass, its own and the proposal
/// tools, is watched by <c>monitor</c>.
/// </summary>
public interface IAnalysisAgentLoopFactory
{
    Task<IAgentLoop> CreateAnalysisLoopAsync(
        ExecutionSessionHandle session,
        Guid projectId,
        IReadOnlyList<AIFunction> proposalTools,
        string? model,
        string systemPrompt,
        AnalysisContextOptions context,
        AnalysisToolMonitor monitor,
        CancellationToken cancellationToken);
}
