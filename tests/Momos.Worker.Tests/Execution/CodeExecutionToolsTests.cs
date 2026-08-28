using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class CodeExecutionToolsTests
{
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
