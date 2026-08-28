using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class CodeExecutionToolsTests
{
    /// <summary>Captures each formatted log message so a test can assert on what did (or did not) reach the log.</summary>
    private sealed class RecordingLogger : ILogger<CodeExecutionTools>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task RunCommand_Success_DoesNotLogCommandOutput()
    {
        // HD-05: the inspected target is untrusted and its output can carry secrets
        // (.env contents, credentials) — the Worker's own log must never carry a preview
        // of it. "What ran" stays auditable; "what it showed" is the agent's call via
        // ReportFinding's Evidence field, not something every RunCommand call broadcasts.
        var provider = new FakeExecutionRuntimeProvider
        {
            NextResult = new ExecutionCommandResult(true, "SECRET_API_KEY=sk-abcdef123456", null, 42),
        };
        var logger = new RecordingLogger();
        var tools = new CodeExecutionTools(provider, new ExecutionSessionHandle("session-1"), logger);

        await tools.RunCommand("cat", [".env"]);

        Assert.All(logger.Messages, message => Assert.DoesNotContain("SECRET_API_KEY", message));
        Assert.Contains(logger.Messages, message => message.Contains("cat") && message.Contains("success=True"));
    }

    [Fact]
    public async Task RunCommand_Failure_DoesNotLogCommandError()
    {
        var provider = new FakeExecutionRuntimeProvider
        {
            NextResult = new ExecutionCommandResult(false, null, "printenv: SECRET_TOKEN=xyz", 5),
        };
        var logger = new RecordingLogger();
        var tools = new CodeExecutionTools(provider, new ExecutionSessionHandle("session-1"), logger);

        await tools.RunCommand("printenv");

        Assert.All(logger.Messages, message => Assert.DoesNotContain("SECRET_TOKEN", message));
    }

    [Fact]
    public async Task RunCommand_Success_ReturnsOutput()
    {
        var provider = new FakeExecutionRuntimeProvider { NextResult = new ExecutionCommandResult(true, "build succeeded", null, 123) };
        var session = new ExecutionSessionHandle("session-1");
        var tools = new CodeExecutionTools(provider, session, NullLogger<CodeExecutionTools>.Instance);

        var output = await tools.RunCommand("dotnet", ["build"], "/workspace/repo");

        Assert.Equal("build succeeded", output);
        var (executedSession, command) = provider.LastExecuted!.Value;
        Assert.Equal(session, executedSession);
        Assert.Equal("dotnet", command.Name);
        Assert.Equal(["build"], command.Args);
        Assert.Equal("/workspace/repo", command.WorkingDirectory);
    }

    [Fact]
    public async Task RunCommand_Failure_ReturnsFailureMessageWithError()
    {
        var provider = new FakeExecutionRuntimeProvider { NextResult = new ExecutionCommandResult(false, null, "no such file", 5) };
        var tools = new CodeExecutionTools(provider, new ExecutionSessionHandle("session-1"), NullLogger<CodeExecutionTools>.Instance);

        var output = await tools.RunCommand("dotnet", ["build"]);

        Assert.Contains("no such file", output);
        Assert.Contains("5ms", output);
    }

    [Fact]
    public async Task RunCommand_NoArgs_PassesEmptyArgsList()
    {
        var provider = new FakeExecutionRuntimeProvider();
        var tools = new CodeExecutionTools(provider, new ExecutionSessionHandle("session-1"), NullLogger<CodeExecutionTools>.Instance);

        await tools.RunCommand("ls");

        Assert.Empty(provider.LastExecuted!.Value.Command.Args);
    }

    [Fact]
    public async Task AsAIFunctionTool_InvokesThroughToTheExecutionProvider()
    {
        var provider = new FakeExecutionRuntimeProvider { NextResult = new ExecutionCommandResult(true, "42 tests passed", null, 10) };
        var session = new ExecutionSessionHandle("session-1");
        var tool = AIFunctionFactory.Create(new CodeExecutionTools(provider, session, NullLogger<CodeExecutionTools>.Instance).RunCommand);

        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "dotnet",
            ["args"] = new[] { "test" },
        });

        Assert.Equal("42 tests passed", result?.ToString());
        Assert.Equal("dotnet", provider.LastExecuted!.Value.Command.Name);
    }
}
