namespace Momos.Worker.Execution;

/// <summary>
/// Thrown when no isolated runtime (e.g. Docker) is available and code-beaker would
/// otherwise fall back to the unsandboxed <c>NativeProcessRuntime</c> (HD-08). A command
/// run there is exactly as exposed as any other process on the host — the fallback is
/// refused rather than merely logged, after two incidents on the identical fallback
/// (cycle-25: killed every dotnet.exe on a shared host; cycle-27: extracted an unrelated
/// project's live-container credentials via <c>docker exec</c>).
/// </summary>
public sealed class UnisolatedExecutionRuntimeException(string message) : InvalidOperationException(message);
