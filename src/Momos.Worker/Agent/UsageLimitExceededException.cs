namespace Momos.Worker.Agent;

/// <summary>
/// Thrown by <see cref="UsageLimitingChatClient"/> when a token limit has been exceeded — an
/// inspection session's (<see cref="AgentLoopLimitsOptions.MaxSessionTokens"/>) or the current
/// <see cref="TokenBudget"/>'s — surfaces as a
/// failed inspection through <c>PullExecutionBackgroundService</c>'s existing exception
/// handling rather than letting the loop keep calling the shared LLM endpoint indefinitely.
/// </summary>
public sealed class UsageLimitExceededException(string message) : InvalidOperationException(message);
