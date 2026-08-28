using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests;

/// <summary>
/// Covers the real DI graph <c>AddMomosWorker</c> wires for ADR-0009 decisions 1·2·3(=B) —
/// the other tests in this project exercise the port/adapter logic against fakes
/// (<see cref="Execution.FakeExecutionRuntimeProvider"/>, <c>FakeSessionManager</c>);
/// this one proves the actual registrations (<c>SessionManager</c>, <c>NativeProcessRuntime</c>,
/// <c>InMemorySessionStore</c>) resolve and run a real OS process end to end.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddMomosWorker_ResolvesARealExecutionRuntimeProvider_ThatRunsANativeCommand()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddMomosWorker(configuration);
        await using var provider = services.BuildServiceProvider();

        var executionRuntimeProvider = provider.GetRequiredService<IExecutionRuntimeProvider>();
        var session = await executionRuntimeProvider.CreateSessionAsync(new ExecutionSessionRequest("native"));
        try
        {
            var result = await executionRuntimeProvider.ExecuteAsync(session, new ExecutionCommand("dotnet", ["--version"]));
            Assert.True(result.Success, result.Error);
        }
        finally
        {
            await executionRuntimeProvider.CloseSessionAsync(session);
        }
    }

    /// <summary>
    /// Same real DI graph, but exercising the exact command
    /// <see cref="Momos.Worker.Execution.PullExecutionBackgroundService"/> issues to check
    /// out a target repo — against a local git repo (no network) so the test stays fast
    /// and deterministic while still spawning a real <c>git</c> process.
    /// </summary>
    [Fact]
    public async Task AddMomosWorker_ClonesARealLocalGitRepositoryIntoTheSessionWorkspace()
    {
        var sourceRepo = Directory.CreateTempSubdirectory("momos-clone-source-");
        try
        {
            await RunGitAsync(sourceRepo.FullName, "init");
            await RunGitAsync(sourceRepo.FullName, "commit", "--allow-empty", "-m", "seed", "--author=test <test@example.invalid>");

            var services = new ServiceCollection();
            services.AddMomosWorker(new ConfigurationBuilder().Build());
            await using var provider = services.BuildServiceProvider();
            var executionRuntimeProvider = provider.GetRequiredService<IExecutionRuntimeProvider>();

            var session = await executionRuntimeProvider.CreateSessionAsync(new ExecutionSessionRequest("native"));
            try
            {
                var result = await executionRuntimeProvider.ExecuteAsync(
                    session, new ExecutionCommand("git", ["clone", sourceRepo.FullName, "."]));

                Assert.True(result.Success, result.Error);
            }
            finally
            {
                await executionRuntimeProvider.CloseSessionAsync(session);
            }
        }
        finally
        {
            // git leaves read-only files under .git/objects on Windows —
            // DirectoryInfo.Delete(recursive: true) throws on those otherwise.
            foreach (var file in sourceRepo.GetFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            sourceRepo.Delete(recursive: true);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
