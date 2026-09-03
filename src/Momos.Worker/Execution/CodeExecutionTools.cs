using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Execution;

/// <summary>
/// Exposes <see cref="IExecutionRuntimeProvider"/>'s sandboxed command execution as a
/// native in-process agent tool — wrapped with
/// <c>Microsoft.Extensions.AI.AIFunctionFactory.Create</c> at the call site, the same
/// pattern <c>IronHive.Agent</c>'s own built-in tools use for their file/shell
/// capabilities. No MCP transport is involved: the capability is already in-process
/// (code-beaker is a direct project dependency), so nothing needs to cross a process
/// boundary to reach it.
/// </summary>
public sealed class CodeExecutionTools(
    IExecutionRuntimeProvider runtimeProvider,
    ExecutionSessionHandle session,
    ToolCallTraceSink trace,
    ILogger<CodeExecutionTools> logger)
{
    [Description("Run a shell command inside the sandboxed inspection workspace and return its output.")]
    public async Task<string> RunCommand(
        [Description("The command or executable name, e.g. \"dotnet\" or \"ls\".")] string command,
        [Description("Arguments to pass to the command.")] string[]? args = null,
        [Description("Working directory relative to the session workspace root, if not the root itself.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "RunCommand: {Command} {Args} (cwd: {WorkingDirectory})",
            command, string.Join(' ', args ?? []), workingDirectory ?? "(workspace root)");

        var result = await runtimeProvider.ExecuteAsync(
            session,
            new ExecutionCommand(command, args ?? [], workingDirectory),
            cancellationToken);

        // Deliberately no output/error preview here — the inspected target is untrusted
        // and its command output can carry secrets (.env contents, credentials) that must
        // not land in the Worker's own logs, nor in the trace entry below (it is persisted
        // on Host and returned by a public endpoint). The audit trail this log line and
        // trace entry provide is "what ran and whether it succeeded"; "what it showed" is a
        // separate concern that belongs to a finding's Evidence field (ReportFinding), which
        // the agent populates deliberately from output it decided was relevant.
        logger.LogInformation(
            "RunCommand result: {Command} success={Success} ({DurationMs}ms)",
            command, result.Success, result.DurationMs);
        trace.Add(new ToolCallEntry(nameof(RunCommand), $"{command} {string.Join(' ', args ?? [])}".TrimEnd(), result.Success, result.DurationMs));

        return result.Success
            ? result.Output ?? string.Empty
            : $"Command failed after {result.DurationMs}ms: {result.Error}";
    }
}
