namespace Momos.Worker.Execution;

/// <summary>
/// Thrown when no isolated runtime (e.g. Docker) is available and code-beaker would
/// otherwise fall back to the unsandboxed <c>NativeProcessRuntime</c>. A command run
/// there is exactly as exposed as any other process on the host — able to affect
/// unrelated processes and data on a shared machine — so the fallback is refused
/// rather than merely logged.
/// </summary>
public sealed class UnisolatedExecutionRuntimeException(string message) : InvalidOperationException(message);
