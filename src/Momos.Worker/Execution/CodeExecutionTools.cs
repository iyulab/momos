using System.ComponentModel;
using System.Diagnostics;
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
/// <para>
/// <paramref name="maxOutputChars"/>, when set, caps what one command returns to the agent. An
/// inspection leaves it unset — a finding quotes the output it saw. An analysis reads far more than
/// it runs, and every returned output stays in the conversation the model is sent again on each
/// turn, so there a whole file listing or source file would crowd out the chapter being written.
/// </para>
/// </summary>
public sealed class CodeExecutionTools(
    IExecutionRuntimeProvider runtimeProvider,
    ExecutionSessionHandle session,
    ToolCallTraceSink trace,
    ILogger<CodeExecutionTools> logger,
    int? maxOutputChars = null)
{
    // The command runs as a program with an argument list, not as a shell line — said plainly,
    // because an agent otherwise puts a whole line, pipes included, into command and it fails.
    [Description("Run one program inside the sandboxed workspace and return its output. No shell reads the call: put the program in " +
        "command and each argument in args, e.g. command \"git\", args [\"log\", \"--oneline\", \"-40\"]. For pipes, redirection or wildcards, " +
        "run a shell yourself: command \"sh\", args [\"-c\", \"grep -rn Foo src | head -20\"].")]
    public async Task<string> RunCommand(
        [Description("The program alone, e.g. \"dotnet\", \"ls\" or \"sh\" — never a whole command line.")] string command,
        [Description("The program's arguments, one per element.")] string[]? args = null,
        [Description("Working directory relative to the session workspace root, if not the root itself.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var argsJoined = string.Join(' ', args ?? []);
        logger.LogInformation(
            "RunCommand: {Command} {Args} (cwd: {WorkingDirectory})",
            command, argsJoined, workingDirectory ?? "(workspace root)");

        var stopwatch = Stopwatch.StartNew();
        try
        {
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
            trace.Add(new ToolCallEntry(nameof(RunCommand), $"{command} {argsJoined}".TrimEnd(), result.Success, result.DurationMs));

            return Cap(result.Success
                ? result.Output ?? string.Empty
                : FormatFailure(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            trace.Add(new ToolCallEntry(nameof(RunCommand), $"{command} {argsJoined}".TrimEnd(), Success: false, (int)stopwatch.ElapsedMilliseconds));
            throw;
        }
    }

    private string Cap(string output) =>
        maxOutputChars is { } max && output.Length > max
            ? $"{output[..max]}\n[Output truncated: {output.Length} characters in all, the first {max} shown. Read a narrower part — a line range (command \"sed\", args [\"-n\", \"120,180p\", \"<file>\"]), grep for what you need, or head/tail.]"
            : output;

    /// <summary>
    /// A failed command's stdout is kept alongside its error: tools such as test runners
    /// report their failures on stdout with an empty stderr, leaving only the exit code as the
    /// error — and the output is the evidence the agent has to cite in a finding.
    /// </summary>
    private static string FormatFailure(ExecutionCommandResult result)
    {
        var summary = $"Command failed after {result.DurationMs}ms: {result.Error}";
        return string.IsNullOrEmpty(result.Output)
            ? summary
            : $"{summary}\nstdout:\n{result.Output}";
    }
}
