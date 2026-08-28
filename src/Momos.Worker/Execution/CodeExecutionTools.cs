using System.ComponentModel;

namespace Momos.Worker.Execution;

/// <summary>
/// Exposes <see cref="IExecutionRuntimeProvider"/>'s sandboxed command execution as a
/// native in-process agent tool (ADR-0009 decision 3 = B) — wrapped with
/// <c>Microsoft.Extensions.AI.AIFunctionFactory.Create</c> at the call site, the same
/// pattern <c>IronHive.Agent</c>'s own built-in tools use for their file/shell
/// capabilities. No MCP transport is involved: the capability is already in-process
/// (code-beaker is a direct project dependency), so nothing needs to cross a process
/// boundary to reach it.
/// </summary>
public sealed class CodeExecutionTools(IExecutionRuntimeProvider runtimeProvider, ExecutionSessionHandle session)
{
    [Description("Run a shell command inside the sandboxed inspection workspace and return its output.")]
    public async Task<string> RunCommand(
        [Description("The command or executable name, e.g. \"dotnet\" or \"ls\".")] string command,
        [Description("Arguments to pass to the command.")] string[]? args = null,
        [Description("Working directory relative to the session workspace root, if not the root itself.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var result = await runtimeProvider.ExecuteAsync(
            session,
            new ExecutionCommand(command, args ?? [], workingDirectory),
            cancellationToken);

        return result.Success
            ? result.Output ?? string.Empty
            : $"Command failed after {result.DurationMs}ms: {result.Error}";
    }
}
