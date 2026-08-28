using CodeBeaker.Commands.Models;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class CodeBeakerExecutionRuntimeProviderTests
{
    [Fact]
    public async Task CreateSessionAsync_AppliesAdr0009DefaultSecurityPosture()
    {
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager);

        var handle = await provider.CreateSessionAsync(new ExecutionSessionRequest("python"));

        Assert.Equal("fake-session-id", handle.SessionId);
        Assert.Equal("python", sessionManager.LastCreatedConfig!.Language);
        Assert.Equal(CodeBeakerExecutionRuntimeProvider.DefaultMemoryLimitMB, sessionManager.LastCreatedConfig.MemoryLimitMB);
        Assert.True(sessionManager.LastCreatedConfig.Security.EnableSandbox);
        Assert.True(sessionManager.LastCreatedConfig.Security.SandboxRestrictFilesystem);
        Assert.False(sessionManager.LastCreatedConfig.Security.SandboxDisableNetwork);
    }

    [Fact]
    public async Task ExecuteAsync_TranslatesCommandToExecuteShellCommand()
    {
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager);
        var handle = new ExecutionSessionHandle("session-1");

        await provider.ExecuteAsync(handle, new ExecutionCommand("dotnet", ["build"], "/workspace/repo"));

        var (sessionId, command) = sessionManager.LastExecuted!.Value;
        Assert.Equal("session-1", sessionId);
        var shellCommand = Assert.IsType<ExecuteShellCommand>(command);
        Assert.Equal("dotnet", shellCommand.CommandName);
        Assert.Equal(["build"], shellCommand.Args);
        Assert.Equal("/workspace/repo", shellCommand.WorkingDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_MapsCommandResultFields()
    {
        var sessionManager = new FakeSessionManager
        {
            NextResult = CommandResult.Fail("build failed", durationMs: 42),
        };
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager);

        var result = await provider.ExecuteAsync(
            new ExecutionSessionHandle("session-1"), new ExecutionCommand("dotnet", ["test"]));

        Assert.False(result.Success);
        Assert.Equal("build failed", result.Error);
        Assert.Equal(42, result.DurationMs);
    }

    [Fact]
    public async Task CloseSessionAsync_ClosesTheUnderlyingSession()
    {
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager);

        await provider.CloseSessionAsync(new ExecutionSessionHandle("session-1"));

        Assert.Equal("session-1", sessionManager.LastClosedSessionId);
    }
}
