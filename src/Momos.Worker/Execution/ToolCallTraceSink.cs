namespace Momos.Worker.Execution;

/// <summary>One tool invocation, regardless of outcome. Never carries raw command
/// output/error text — see <see cref="CodeExecutionTools"/>'s doc comment for why.</summary>
public sealed record ToolCallEntry(string Tool, string Summary, bool Success, int DurationMs);

/// <summary>
/// Accumulates a <see cref="ToolCallEntry"/> per tool invocation during one agent-loop run —
/// same shape and thread-safety assumption as <see cref="FindingSink"/> (tool calls within a
/// single <c>AgentLoop</c> run sequentially, one <c>FunctionInvokingChatClient</c> turn at a time).
/// </summary>
public sealed class ToolCallTraceSink
{
    private readonly List<ToolCallEntry> _calls = [];

    public void Add(ToolCallEntry entry) => _calls.Add(entry);

    public IReadOnlyList<ToolCallEntry> Calls => _calls;
}
