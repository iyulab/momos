using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Momos.Worker.Execution;

namespace Momos.Worker.IntegrationTests.Execution;

/// <summary>
/// Runs commands as local processes, each session in its own fresh, empty temporary directory —
/// the same starting point a sandbox session gives the Worker's clone. git is isolated from this
/// machine's global and system configuration and given a fixed identity, so a run behaves the same
/// on a developer machine and on a CI runner that has no git configuration at all. Output is
/// returned exactly as the process wrote it: nothing is trimmed, so NUL-separated listings survive.
/// </summary>
internal sealed class LocalProcessExecutionRuntimeProvider : IExecutionRuntimeProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    public Task<ExecutionSessionHandle> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken cancellationToken = default)
    {
        var directory = Directory.CreateTempSubdirectory("momos-session-").FullName;
        var handle = new ExecutionSessionHandle(Guid.NewGuid().ToString("N"));
        _sessions[handle.SessionId] = directory;
        return Task.FromResult(handle);
    }

    public Task<ExecutionCommandResult> ExecuteAsync(ExecutionSessionHandle session, ExecutionCommand command, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(session.SessionId, out var directory))
        {
            throw new InvalidOperationException($"Unknown session {session.SessionId}.");
        }

        return RunAsync(command.WorkingDirectory is null ? directory : Path.Combine(directory, command.WorkingDirectory), command, cancellationToken);
    }

    public Task CloseSessionAsync(ExecutionSessionHandle session, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(session.SessionId, out var directory))
        {
            DeleteDirectory(directory);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var directory in _sessions.Values)
        {
            DeleteDirectory(directory);
        }

        _sessions.Clear();
    }

    /// <summary>Runs one command in <paramref name="workingDirectory"/> under the same isolated git
    /// environment the sessions use — also how a test builds the repository it analyzes.</summary>
    public static async Task<ExecutionCommandResult> RunAsync(string workingDirectory, ExecutionCommand command, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(command.Name)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var arg in command.Args)
        {
            start.ArgumentList.Add(arg);
        }

        // A path that does not exist reads as an empty configuration, which is exactly the point.
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetTempPath(), $"momos-no-gitconfig-{Guid.NewGuid():N}");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "test";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {command.Name}.");

        // Both streams at once: reading one to the end first can deadlock once the other fills its pipe.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(output, error);
        await process.WaitForExitAsync(cancellationToken);

        return new ExecutionCommandResult(
            process.ExitCode == 0,
            output.Result,
            error.Result.Length == 0 ? null : error.Result,
            (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Deletes a directory tree that may hold git objects, which are read-only on Windows.</summary>
    public static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
