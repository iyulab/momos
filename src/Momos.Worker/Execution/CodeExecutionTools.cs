using System.ComponentModel;
using Microsoft.Extensions.Logging;

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
public sealed class CodeExecutionTools(
    IExecutionRuntimeProvider runtimeProvider,
    ExecutionSessionHandle session,
    ILogger<CodeExecutionTools> logger)
{
    /// <summary>Info-level log cap per command — enough to judge what happened without flooding the log.</summary>
    private const int LogPreviewLength = 500;

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

        logger.LogInformation(
            "RunCommand result: {Command} success={Success} ({DurationMs}ms) — {Preview}",
            command, result.Success, result.DurationMs, Truncate(result.Success ? result.Output : result.Error));

        return result.Success
            ? result.Output ?? string.Empty
            : $"Command failed after {result.DurationMs}ms: {result.Error}";
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        return text.Length <= LogPreviewLength ? text : text[..LogPreviewLength] + "…";
    }
}
