using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests;

/// <summary>
/// Covers the real DI graph <c>AddMomosWorker</c> wires for code-beaker's execution
/// stack — the other tests in this project exercise the port/adapter logic against fakes
/// (<see cref="Execution.FakeExecutionRuntimeProvider"/>, <c>FakeSessionManager</c>);
/// this one proves the actual registrations (<c>SessionManager</c>, <c>NativeProcessRuntime</c>,
/// <c>InMemorySessionStore</c>) resolve and run a real OS process end to end.
///
/// Goes through <see cref="ISessionManager"/> directly, not
/// <see cref="Momos.Worker.Execution.IExecutionRuntimeProvider"/> — the provider treats an
/// unsandboxed runtime as unusable and refuses a session that lands on
/// <c>NativeProcessRuntime</c> (the only runtime <c>AddMomosWorker</c> registers), so
/// exercising that native path end to end now has to bypass the provider's isolation
/// policy. That policy is covered separately by
/// <c>CodeBeakerExecutionRuntimeProviderTests</c>'s fakes-based tests.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddMomosWorker_ResolvesARealSessionManager_ThatRunsANativeCommand()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddMomosWorker(configuration);
        await using var provider = services.BuildServiceProvider();

        var sessionManager = provider.GetRequiredService<ISessionManager>();
        var session = await sessionManager.CreateSessionAsync(new SessionConfig { Language = "native" });
        try
        {
            var result = await sessionManager.ExecuteInSessionAsync(
                session.SessionId, new ExecuteShellCommand { CommandName = "dotnet", Args = ["--version"] });
            Assert.True(result.Success, result.Error);
        }
        finally
        {
            await sessionManager.CloseSessionAsync(session.SessionId);
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
            var sessionManager = provider.GetRequiredService<ISessionManager>();

            var session = await sessionManager.CreateSessionAsync(new SessionConfig { Language = "native" });
            try
            {
                var result = await sessionManager.ExecuteInSessionAsync(
                    session.SessionId,
                    new ExecuteShellCommand { CommandName = "git", Args = ["clone", "--", sourceRepo.FullName, "."] });

                Assert.True(result.Success, result.Error);
            }
            finally
            {
                await sessionManager.CloseSessionAsync(session.SessionId);
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

    /// <summary>
    /// <see cref="Momos.Worker.SelfUpdate.WorkerSelfUpdater"/> carries instance state
    /// (a last-suppressed-notice cache) that is only correct if exactly one instance
    /// backs the process — resolving a second one would let the same warning repeat
    /// forever instead of once. The registration must reflect that, not rely on
    /// <see cref="PullExecutionBackgroundService"/> happening to be the only consumer.
    /// </summary>
    [Fact]
    public void AddMomosWorker_ResolvesTheSameWorkerSelfUpdaterInstanceEveryTime()
    {
        var services = new ServiceCollection();
        services.AddMomosWorker(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IWorkerSelfUpdater>();
        var second = provider.GetRequiredService<IWorkerSelfUpdater>();

        Assert.Same(first, second);
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
