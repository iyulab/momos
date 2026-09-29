using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Agent;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Covers the factory's session wiring: one code-beaker session per
/// <see cref="IAgentLoopFactory.CreateAsync(CancellationToken)"/> call,
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
        // MomosAgentLoopFactory needs an IHostApiClient to build the knowledge-query tool --
        // this file's tests don't exercise that tool, so an empty fake is enough.
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
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
    /// The provider sends no thinking controls when <see cref="ChatOptions.Reasoning"/> is unset,
    /// which lets a reasoning model think before every reply. The engine's chat-client pipeline
    /// must therefore ask for no reasoning explicitly on every model call the loop makes.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheChatClientIsAskedForNoReasoning()
    {
        var services = new ServiceCollection();
        var chatClientProvider = new FakeChatClientProvider("hi");
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();

        var agentLoop = await factory.CreateAsync();
        await agentLoop.RunAsync("look for problems in this repository");

        var reasoning = chatClientProvider.LastClient?.LastOptions?.Reasoning;
        Assert.NotNull(reasoning);
        Assert.Equal(ReasoningEffort.None, reasoning!.Effort);
    }

    /// <summary>
    /// Proves the tool survives <c>IToolRetriever</c> (registered as
    /// <c>KeywordToolRetriever</c> — see <see cref="ServiceCollectionExtensions.AddIronHiveAgentEngine"/>)
    /// on its way from <c>AgentOptions.Tools</c> to what the chat client actually receives.
    /// Wiring the tool into <c>AgentOptions</c> alone would be dead code if a keyword-based
    /// retriever filtered it back out for a prompt that shares no keywords with its name/description.
    /// </summary>
    /// <summary>
    /// Mirrors <see cref="RunAsync_TheChatClientActuallyReceivesTheCodeExecutionTool"/> for the
    /// knowledge-query tool: proves that passing a non-null <c>projectId</c> to the
    /// session-aware <c>CreateAsync</c> overload actually gets <c>KnowledgeQueryTools</c> onto
    /// the chat client's tool list, not just into <c>AgentOptions.Tools</c> where a keyword-based
    /// retriever could silently drop it (see <c>ToolRetrievalOptions.AlwaysInclude</c> in
    /// <see cref="MomosAgentLoopFactory"/>).
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithProjectId_GivesTheChatClientTheKnowledgeQueryTool()
    {
        var services = new ServiceCollection();
        var chatClientProvider = new FakeChatClientProvider("hi");
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISessionAwareAgentLoopFactory>();
        var session = new ExecutionSessionHandle("test-session");

        var (agentLoop, _, _) = await factory.CreateAsync(new AgentLoopFactoryOptions(), session, projectId: Guid.NewGuid());
        await agentLoop.RunAsync("look for problems in this repository");

        var tools = chatClientProvider.LastClient?.LastOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Contains(tools!, t => t.Name == nameof(KnowledgeQueryTools.QueryProjectKnowledge));
    }

    /// <summary>
    /// Same setup, but through the plain <c>CreateAsync(options, cancellationToken)</c> overload
    /// (no session, no <c>projectId</c>) -- proves the tool is genuinely conditional on a
    /// project being known, not always present regardless of what the previous test's
    /// assertion alone could show.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithoutAProjectId_DoesNotGiveTheChatClientTheKnowledgeQueryTool()
    {
        var services = new ServiceCollection();
        var chatClientProvider = new FakeChatClientProvider("hi");
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();

        var agentLoop = await factory.CreateAsync();
        await agentLoop.RunAsync("look for problems in this repository");

        var tools = chatClientProvider.LastClient?.LastOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools!, t => t.Name == nameof(KnowledgeQueryTools.QueryProjectKnowledge));
    }

    [Fact]
    public async Task RunAsync_TheChatClientActuallyReceivesTheCodeExecutionTool()
    {
        var services = new ServiceCollection();
        var chatClientProvider = new FakeChatClientProvider("hi");
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
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
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();
        var agentLoop = await factory.CreateAsync();

        var response = await agentLoop.RunAsync("look for problems in this repository");

        Assert.NotNull(executionProvider.LastExecuted);
        Assert.Equal("dotnet", executionProvider.LastExecuted!.Value.Command.Name);
        Assert.Contains("no issues found", response.Content);
    }

    /// <summary>
    /// <see cref="AgentLoop"/> itself has no field referencing a usage limiter (confirmed by
    /// reflection — <c>AgentServicesOptions.UsageLimits</c> registers a
    /// <c>IronHive.Agent.Tracking.UsageLimiter</c> in DI that the loop never reads), so the
    /// only place a session-token cap can actually stop a runaway tool-call loop is the chat
    /// client itself. This drives a real multi-turn loop past its configured
    /// <see cref="AgentLoopLimitsOptions.MaxSessionTokens"/> and checks the *next* turn is
    /// refused before it ever reaches the fake provider — proving
    /// <see cref="UsageLimitingChatClient"/> is actually wired into the request path
    /// (<see cref="ServiceCollectionExtensions.AddIronHiveAgentEngine"/>), not just present
    /// as an unused class.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenSessionTokenUsageExceedsTheConfiguredLimit_StopsBeforeTheNextModelCall()
    {
        var toolCall = new FunctionCallContent(
            "call-1",
            nameof(CodeExecutionTools.RunCommand),
            new Dictionary<string, object?> { ["command"] = "dotnet", ["args"] = new[] { "build" } });
        var overLimitResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall]))
        {
            Usage = new UsageDetails { TotalTokenCount = 1_000 },
        };
        var chatClientProvider = new FakeChatClientProvider(
            responsesBeforeFinal: [overLimitResponse],
            finalResponse: new ChatResponse(new ChatMessage(ChatRole.Assistant, "should never be reached")));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider
        {
            NextResult = new ExecutionCommandResult(true, "build succeeded", null, 100),
        });
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine(new AgentLoopLimitsOptions { MaxSessionTokens = 10 });
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();
        var agentLoop = await factory.CreateAsync();

        await Assert.ThrowsAsync<UsageLimitExceededException>(
            () => agentLoop.RunAsync("look for problems in this repository"));
    }

    private static readonly AnalysisContextOptions DefaultContext =
        new(MaxCommandOutputChars: 8_000, MaxContextTokens: 32_000, ProtectedToolRounds: 4);

    private static IAnalysisAgentLoopFactory AnalysisFactory(FakeChatClientProvider chatClientProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider());
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        return services.BuildServiceProvider().GetRequiredService<IAnalysisAgentLoopFactory>();
    }

    [Fact]
    public async Task AnAnalysisLoop_GetsTheProposalToolsButNeverReportFinding()
    {
        var chatClientProvider = new FakeChatClientProvider("hi");
        var proposal = AIFunctionFactory.Create((string topic) => "Recorded.", "ProposeClaim");

        var loop = await AnalysisFactory(chatClientProvider).CreateAnalysisLoopAsync(
            new ExecutionSessionHandle("s"), Guid.NewGuid(), [proposal], model: null, "rules", DefaultContext, CancellationToken.None);
        await loop.RunAsync("zzz unrelated words");

        var names = chatClientProvider.LastClient!.LastOptions!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("ProposeClaim", names);
        Assert.Contains(nameof(CodeExecutionTools.RunCommand), names);
        Assert.Contains(nameof(KnowledgeQueryTools.QueryProjectKnowledge), names);
        Assert.DoesNotContain(nameof(FindingReportingTools.ReportFinding), names);
    }

    [Fact]
    public async Task AnAnalysisLoop_SendsTheSystemPromptItWasGiven()
    {
        var chatClientProvider = new FakeChatClientProvider("hi");

        var loop = await AnalysisFactory(chatClientProvider).CreateAnalysisLoopAsync(
            new ExecutionSessionHandle("s"), Guid.NewGuid(), [], model: null, "You write a manual.", DefaultContext, CancellationToken.None);
        await loop.RunAsync("go");

        Assert.Contains(chatClientProvider.LastClient!.LastMessages!, m => m.Role == ChatRole.System && m.Text.Contains("You write a manual.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pass is one user message followed by many tool rounds, all inside function invocation. Every
    /// round re-sends the whole conversation, so without per-round masking each earlier command output
    /// rides along in full on every later model call.
    /// </summary>
    [Fact]
    public async Task AnAnalysisLoop_MasksOldToolOutputs_BeforeLaterModelCalls()
    {
        var big = new string('x', 5_000);
        var turns = Enumerable.Range(0, 6)
            .Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "Echo", new Dictionary<string, object?> { ["text"] = big })])))
            .ToList();
        var chat = new FakeChatClient(turns, new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        var echo = AIFunctionFactory.Create((string text) => text, "Echo");

        var loop = await AnalysisFactory(new FakeChatClientProvider([chat])).CreateAnalysisLoopAsync(
            new ExecutionSessionHandle("s"), Guid.NewGuid(), [echo], model: null, "system",
            new AnalysisContextOptions(MaxCommandOutputChars: 8_000, MaxContextTokens: 32_000, ProtectedToolRounds: 2), CancellationToken.None);
        await loop.RunAsync("go");

        var toolTexts = chat.LastMessages!.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "").ToList();
        Assert.Equal(6, toolTexts.Count);
        Assert.True(toolTexts.Take(4).All(t => t.Length < big.Length), "tool outputs older than the protected rounds were sent in full");
        Assert.True(toolTexts.Skip(4).All(t => t.Length == big.Length), "the protected recent rounds were masked");
    }

    /// <summary>An inspection loop keeps sending its tool outputs as they are: only analysis passes
    /// get a per-round reducer.</summary>
    [Fact]
    public async Task AnInspectionLoop_SendsEveryToolOutputInFull()
    {
        var toolCall = new FunctionCallContent("c0", nameof(CodeExecutionTools.RunCommand),
            new Dictionary<string, object?> { ["command"] = "dotnet", ["args"] = new[] { "build" } });
        var output = new string('x', 5_000);
        var turns = Enumerable.Range(0, 6)
            .Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", toolCall.Name, toolCall.Arguments)])))
            .ToList();
        var chat = new FakeChatClient(turns, new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new FakeChatClientProvider([chat]));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(new FakeExecutionRuntimeProvider
        {
            NextResult = new ExecutionCommandResult(true, output, null, 100),
        });
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var factory = services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();

        var loop = await factory.CreateAsync();
        await loop.RunAsync("look for problems in this repository");

        var toolTexts = chat.LastMessages!.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "").ToList();
        Assert.Equal(6, toolTexts.Count);
        Assert.All(toolTexts, t => Assert.Contains(output, t, StringComparison.Ordinal));
    }
}
