namespace Momos.Worker.Execution;

/// <summary>
/// Accumulates the <see cref="FindingPayload"/>s <see cref="FindingReportingTools"/> reports
/// during one agent-loop run. Tool calls within a single <c>AgentLoop</c> run sequentially
/// (one <c>FunctionInvokingChatClient</c> turn at a time), so no synchronization is needed.
/// </summary>
public sealed class FindingSink
{
    private readonly List<FindingPayload> _findings = [];

    public void Add(FindingPayload finding) => _findings.Add(finding);

    public IReadOnlyList<FindingPayload> Findings => _findings;
}
