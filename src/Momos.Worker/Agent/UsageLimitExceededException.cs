namespace Momos.Worker.Agent;

/// <summary>
/// Thrown by <see cref="UsageLimitingChatClient"/> when an inspection session's token usage
/// (<see cref="AgentLoopLimitsOptions.MaxSessionTokens"/>) has been exceeded — surfaces as a
/// failed inspection through <c>PullExecutionBackgroundService</c>'s existing exception
/// handling rather than letting the loop keep calling the shared LLM endpoint indefinitely.
/// </summary>
public sealed class UsageLimitExceededException(string message) : InvalidOperationException(message);
