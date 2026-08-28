using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Covers ADR-0009 decision 3 = B's wiring: one code-beaker session per
/// <see cref="IAgentLoopFactory.CreateAsync(CancellationToken)"/> call (decision 2),
/// closed when the returned loop is disposed (<see cref="SessionScopedAgentLoop"/>).
/// </summary>
public class MomosAgentLoopFactoryTests
{
    private static (IAgentLoopFactory Factory, FakeExecutionRuntimeProvider ExecutionProvider) Build(string reply)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new FakeChatClientProvider(reply));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var executionProvider = new FakeExecutionRuntimeProvider();
        services.AddSingleton<IExecutionRuntimeProvider>(executionProvider);
        services.AddIronHiveAgentEngine();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IAgentLoopFactory>(), executionProvider);
    }

    [Fact]
    public async Task CreateAsync_OpensExactlyOneExecutionSession()
    {
        var (factory, executionProvider) = Build("hi");

        await factory.CreateAsync();

        var session = Assert.Single(executionProvider.CreatedSessions);
        Assert.Equal("native", session.Language);
    }

    [Fact]
    public async Task CreateAsync_CalledTwice_OpensASeparateSessionEachTime()
    {
        var (factory, executionProvider) = Build("hi");

        await factory.CreateAsync();
        await factory.CreateAsync();

        Assert.Equal(2, executionProvider.CreatedSessions.Count);
    }

    [Fact]
    public async Task DisposingTheReturnedLoop_ClosesTheSessionItOpened()
    {
        var (factory, executionProvider) = Build("hi");
        var agentLoop = await factory.CreateAsync();

        var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(agentLoop);
        await disposable.DisposeAsync();

        var closed = Assert.Single(executionProvider.ClosedSessions);
        Assert.Equal("fake-session-1", closed.SessionId);
    }

    [Fact]
    public async Task RunAsync_StillReturnsTheFakeProviderReply()
    {
        var (factory, _) = Build("hello from momos");
        var agentLoop = await factory.CreateAsync();

        var response = await agentLoop.RunAsync("hi");

        Assert.Contains("hello from momos", response.Content);
    }

    /// <summary>
    /// Proves the tool survives <c>IToolRetriever</c> (registered as
    /// <c>KeywordToolRetriever</c> — see <see cref="ServiceCollectionExtensions.AddIronHiveAgentEngine"/>)
    /// on its way from <c>AgentOptions.Tools</c> to what the chat client actually receives.
    /// Wiring the tool into <c>AgentOptions</c> alone would be dead code if a keyword-based
    /// retriever filtered it back out for a prompt that shares no keywords with its name/description.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheChatClientActuallyReceivesTheCodeExecutionTool()
    {
        var services = new ServiceCollection();
        var chatClientProvider = new FakeChatClientProvider("hi");
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();
        var agentLoop = await factory.CreateAsync();

        await agentLoop.RunAsync("look for problems in this repository");

        var tools = chatClientProvider.LastClient?.LastOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Contains(tools!, t => t.Name == nameof(CodeExecutionTools.RunCommand));
    }

    /// <summary>
    /// Receipt on <c>ChatOptions.Tools</c> (the test above) is necessary but not
    /// sufficient — nothing in <c>AgentLoop.RunAsync</c> itself invokes a requested
    /// <see cref="FunctionCallContent"/>; it only extracts it into an (unexecuted)
    /// <c>ToolCallResult</c>. Actual invocation is <c>Microsoft.Extensions.AI</c>'s
    /// <c>FunctionInvokingChatClient</c> middleware's job, which only runs if the chat
    /// client passed to <see cref="IronHive.Agent.Providers.IChatClientFactory"/> is
    /// wrapped with it (<see cref="Momos.Worker.ServiceCollectionExtensions.AddIronHiveAgentEngine"/>).
    /// This drives a real tool call through a real <see cref="AgentLoop"/> end to end and
    /// checks the execution provider actually ran it — the one signal the tool-retrieval
    /// test above cannot give.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTheChatClientRequestsTheCodeExecutionTool_ActuallyExecutesIt()
    {
        var toolCall = new FunctionCallContent(
            "call-1",
            nameof(CodeExecutionTools.RunCommand),
            new Dictionary<string, object?> { ["command"] = "dotnet", ["args"] = new[] { "build" } });
        var chatClientProvider = new FakeChatClientProvider(
            responsesBeforeFinal: [new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall]))],
            finalResponse: new ChatResponse(new ChatMessage(ChatRole.Assistant, "no issues found")));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var executionProvider = new FakeExecutionRuntimeProvider
        {
            NextResult = new ExecutionCommandResult(true, "build succeeded", null, 100),
        };
        services.AddSingleton<IExecutionRuntimeProvider>(executionProvider);
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();
        var agentLoop = await factory.CreateAsync();

        var response = await agentLoop.RunAsync("look for problems in this repository");

        Assert.NotNull(executionProvider.LastExecuted);
        Assert.Equal("dotnet", executionProvider.LastExecuted!.Value.Command.Name);
        Assert.Contains("no issues found", response.Content);
    }
}
