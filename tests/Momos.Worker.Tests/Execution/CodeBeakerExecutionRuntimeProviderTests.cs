using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class CodeBeakerExecutionRuntimeProviderTests
{
    /// <summary>Captures each formatted log message so a test can assert on what did (or did not) reach the log.</summary>
    private sealed class RecordingLogger : ILogger<CodeBeakerExecutionRuntimeProvider>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task CreateSessionAsync_AppliesAdr0009DefaultSecurityPosture()
    {
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, NullLogger<CodeBeakerExecutionRuntimeProvider>.Instance);

        var handle = await provider.CreateSessionAsync(new ExecutionSessionRequest("python"));

        Assert.Equal("fake-session-id", handle.SessionId);
        Assert.Equal("python", sessionManager.LastCreatedConfig!.Language);
        Assert.Equal(CodeBeakerExecutionRuntimeProvider.DefaultMemoryLimitMB, sessionManager.LastCreatedConfig.MemoryLimitMB);
        Assert.True(sessionManager.LastCreatedConfig.Security.EnableSandbox);
        Assert.True(sessionManager.LastCreatedConfig.Security.SandboxRestrictFilesystem);
        Assert.False(sessionManager.LastCreatedConfig.Security.SandboxDisableNetwork);
    }

    [Fact]
    public async Task CreateSessionAsync_RequestsTheSecurityRuntimePreference()
    {
        // HD-07: a real inspection run on the unsandboxed native runtime killed every
        // dotnet.exe process on a shared host, this session's own Worker included.
        // RuntimePreference.Security picks the most isolated *available* runtime (e.g.
        // Docker) instead of code-beaker's default Balanced preference, which weighs
        // startup/memory so heavily that a near-zero-overhead runtime wins regardless of
        // isolation — and it still falls back to native when nothing more isolated exists,
        // so this never makes a pilot unrunnable on a machine without Docker.
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, NullLogger<CodeBeakerExecutionRuntimeProvider>.Instance);

        await provider.CreateSessionAsync(new ExecutionSessionRequest("dotnet"));

        Assert.Equal(RuntimePreference.Security, sessionManager.LastCreatedConfig!.RuntimePreference);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenNativeRuntimeIsSelected_ThrowsAndClosesTheSession()
    {
        // HD-08: two incidents (cycle-25, cycle-27) happened on this exact fallback —
        // an isolated runtime (e.g. Docker) unavailable, code-beaker falling back to the
        // unsandboxed NativeProcessRuntime. HD-07's response was warn-only; this refuses
        // the fallback outright instead.
        var sessionManager = new FakeSessionManager { NextRuntimeType = RuntimeType.NativeProcess };
        var logger = new RecordingLogger();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, logger);

        await Assert.ThrowsAsync<UnisolatedExecutionRuntimeException>(
            () => provider.CreateSessionAsync(new ExecutionSessionRequest("dotnet")));

        Assert.Equal("fake-session-id", sessionManager.LastClosedSessionId);
        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Warning && m.Message.Contains("Refusing"));
    }

    [Fact]
    public async Task CreateSessionAsync_WhenDockerRuntimeIsSelected_DoesNotWarn()
    {
        var sessionManager = new FakeSessionManager { NextRuntimeType = RuntimeType.Docker };
        var logger = new RecordingLogger();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, logger);

        await provider.CreateSessionAsync(new ExecutionSessionRequest("dotnet"));

        Assert.DoesNotContain(logger.Messages, m => m.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_TranslatesCommandToExecuteShellCommand()
    {
        var sessionManager = new FakeSessionManager();
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, NullLogger<CodeBeakerExecutionRuntimeProvider>.Instance);
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
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, NullLogger<CodeBeakerExecutionRuntimeProvider>.Instance);

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
        var provider = new CodeBeakerExecutionRuntimeProvider(sessionManager, NullLogger<CodeBeakerExecutionRuntimeProvider>.Instance);

        await provider.CloseSessionAsync(new ExecutionSessionHandle("session-1"));

        Assert.Equal("session-1", sessionManager.LastClosedSessionId);
    }
}
