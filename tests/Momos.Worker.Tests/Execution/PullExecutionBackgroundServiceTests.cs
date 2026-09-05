using IronHive.Agent.Extensions;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Agent;

namespace Momos.Worker.Tests.Execution;

public sealed class PullExecutionBackgroundServiceTests
{
    /// <summary>Captures each formatted log message so a test can assert on what did (or did not) reach the log.</summary>
    private sealed class RecordingLogger : ILogger<PullExecutionBackgroundService>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }

    private static (ISessionAwareAgentLoopFactory Factory, FakeExecutionRuntimeProvider ExecutionProvider) BuildFakeAgentLoopFactory(string reply) =>
        BuildFakeAgentLoopFactory(new FakeChatClientProvider(reply));

    private static (ISessionAwareAgentLoopFactory Factory, FakeExecutionRuntimeProvider ExecutionProvider) BuildFakeAgentLoopFactory(
        IChatClientProvider chatClientProvider, IHostApiClient? hostApiClient = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var executionProvider = new FakeExecutionRuntimeProvider();
        services.AddSingleton<IExecutionRuntimeProvider>(executionProvider);
        // MomosAgentLoopFactory (registered by AddIronHiveAgentEngine as
        // ISessionAwareAgentLoopFactory) needs an IHostApiClient to build the
        // knowledge-query tool -- none of this file's tests exercise that tool, so an
        // empty fake is enough to satisfy the DI graph.
        services.AddSingleton(hostApiClient ?? new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ISessionAwareAgentLoopFactory>(), executionProvider);
    }

    [Fact]
    public async Task ExecuteAsync_ClaimedRequest_RunsAgentLoopAndSubmitsEmptyFindingsReport()
    {
        var hostClient = new FakeHostApiClient(
        [
            new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), "focus", null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null),
        ]);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var report = Assert.Single(hostClient.SubmittedReports);
        Assert.Empty(report.Findings);
        Assert.Empty(hostClient.SubmittedFailures);
        // The session opened for this request must close once the run is done,
        // success or not.
        Assert.Single(executionProvider.ClosedSessions);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGetProjectThrows_SubmitsFailureWithTheExceptionMessage()
    {
        var requestId = Guid.NewGuid();
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(requestId, Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            getProjectThrows: true);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        var failure = Assert.Single(hostClient.SubmittedFailures);
        Assert.Equal(requestId, failure.Id);
        Assert.Equal("boom", failure.Reason);
        // GetProjectAsync threw before a session was ever opened, and
        // RunInspectionAsync's finally block must not choke on that.
        Assert.Empty(executionProvider.ClosedSessions);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextThrows_SurvivesAndClaimsOnTheNextPoll()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            claimNextThrowsForFirstNCalls: 2);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        // Proves the transient failures didn't take the whole loop down: it keeps
        // polling and eventually claims and completes the queued request.
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Single(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextThrowsOperationCanceledButStoppingTokenIsNotCancelled_SurvivesAndClaimsOnTheNextPoll()
    {
        // Reproduces an HttpClient.Timeout expiring mid-request (e.g. Host briefly
        // unreachable during a redeploy): that surfaces as an OperationCanceledException
        // even though nobody asked this service to stop. A discriminating regression: with
        // the old `when (ex is not OperationCanceledException)` filter this test fails —
        // the exception escapes ExecuteAsync instead of being retried.
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            claimNextThrowsCanceledForFirstNCalls: 2);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Single(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WithRepositoryUrl_ClonesItBeforeRunningTheAgentLoop()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            repositoryUrl: "https://example.invalid/acme/repo.git");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var (_, command) = executionProvider.LastExecuted!.Value;
        Assert.Equal("git", command.Name);
        Assert.Equal(["clone", "--", "https://example.invalid/acme/repo.git", "."], command.Args);
        Assert.Single(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WithRepositoryUrlAndCommitRef_ChecksOutTheCommitAfterCloning()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, "abc123def^", DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            repositoryUrl: "https://example.invalid/acme/repo.git");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, executionProvider.ExecutedCommands.Count);
        var clone = executionProvider.ExecutedCommands[0].Command;
        Assert.Equal(["clone", "--", "https://example.invalid/acme/repo.git", "."], clone.Args);
        var checkout = executionProvider.ExecutedCommands[1].Command;
        Assert.Equal("git", checkout.Name);
        Assert.Equal(["checkout", "--detach", "abc123def^"], checkout.Args);
        Assert.Single(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WithCommitRefStartingWithDash_SubmitsFailureWithoutExecutingCheckout()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, "--upload-pack=evil", DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            repositoryUrl: "https://example.invalid/acme/repo.git");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Only the clone ran — the malformed ref was rejected before git ever saw it.
        Assert.Single(executionProvider.ExecutedCommands);
        Assert.Empty(hostClient.SubmittedReports);
        var failure = Assert.Single(hostClient.SubmittedFailures);
        Assert.Contains("Invalid commit ref", failure.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoRepositoryUrl_SkipsCloneAndStillRuns()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)]);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Null(executionProvider.LastExecuted);
        Assert.Single(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCloneFails_SubmitsFailureAndNeverRunsTheAgentLoop()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            repositoryUrl: "https://example.invalid/acme/private-repo.git");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        executionProvider.NextResult = new ExecutionCommandResult(false, null, "Authentication failed", 50);
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        var failure = Assert.Single(hostClient.SubmittedFailures);
        Assert.Contains("Authentication failed", failure.Reason);
        // The session opened for the clone attempt still closes even though the
        // agent loop itself never ran.
        Assert.Single(executionProvider.ClosedSessions);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheAgentReportsAFinding_SubmitsItInTheReport()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)]);
        var toolCall = new FunctionCallContent(
            "call-1",
            nameof(FindingReportingTools.ReportFinding),
            new Dictionary<string, object?>
            {
                ["category"] = FindingCategory.FunctionalDefect,
                ["description"] = "App crashes on startup",
                ["evidence"] = "$ dotnet run\nUnhandled exception: NullReferenceException",
            });
        var chatClientProvider = new FakeChatClientProvider(
            responsesBeforeFinal: [new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall]))],
            finalResponse: new ChatResponse(new ChatMessage(ChatRole.Assistant, "reported the crash")));
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory(chatClientProvider);
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var report = Assert.Single(hostClient.SubmittedReports);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingCategory.FunctionalDefect, finding.Category);
        Assert.Equal("App crashes on startup", finding.Description);
        Assert.Contains("NullReferenceException", finding.Evidence);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitsTheToolCallTraceAlongsideFindings()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)]);
        var runCommandCall = new FunctionCallContent(
            "call-1",
            nameof(CodeExecutionTools.RunCommand),
            new Dictionary<string, object?> { ["command"] = "dotnet", ["args"] = new[] { "build" } });
        var queryKnowledgeCall = new FunctionCallContent(
            "call-2",
            nameof(KnowledgeQueryTools.QueryProjectKnowledge),
            new Dictionary<string, object?> { ["query"] = "known issues" });
        var chatClientProvider = new FakeChatClientProvider(
            responsesBeforeFinal: [new ChatResponse(new ChatMessage(ChatRole.Assistant, [runCommandCall, queryKnowledgeCall]))],
            finalResponse: new ChatResponse(new ChatMessage(ChatRole.Assistant, "no issues found")));
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory(chatClientProvider);
        executionProvider.NextResult = new ExecutionCommandResult(true, "build succeeded", null, 100);
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Both tool calls landing in the same report is only possible if
        // CodeExecutionTools and KnowledgeQueryTools shared one ToolCallTraceSink --
        // two separate sinks would each capture only its own tool's entry.
        var report = Assert.Single(hostClient.SubmittedReports);
        Assert.Equal(2, report.ToolCalls.Count);
        Assert.Contains(report.ToolCalls, c => c.Tool == nameof(CodeExecutionTools.RunCommand) && c.Success);
        Assert.Contains(report.ToolCalls, c => c.Tool == nameof(KnowledgeQueryTools.QueryProjectKnowledge));
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheAgentLoopFailsAfterAToolCall_SubmitsTheTraceAlongsideTheFailure()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)]);
        var runCommandCall = new FunctionCallContent(
            "call-1",
            nameof(CodeExecutionTools.RunCommand),
            new Dictionary<string, object?> { ["command"] = "dotnet", ["args"] = new[] { "build" } });
        var chatClientProvider = new FakeChatClientProvider(
            responsesBeforeFinal: [new ChatResponse(new ChatMessage(ChatRole.Assistant, [runCommandCall]))],
            finalException: new InvalidOperationException("simulated LLM-provider failure"));
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory(chatClientProvider);
        executionProvider.NextResult = new ExecutionCommandResult(true, "build succeeded", null, 100);
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Before this fix, the tool call made before the failure was discarded entirely
        // (see claudedocs/issues/ISSUE-momos-20260903-failed-inspection-trace-discarded.md) —
        // it must now travel alongside the failure report.
        Assert.Empty(hostClient.SubmittedReports);
        var failure = Assert.Single(hostClient.SubmittedFailures);
        Assert.Contains("simulated LLM-provider failure", failure.Reason);
        var toolCall = Assert.Single(failure.ToolCalls);
        Assert.Equal(nameof(CodeExecutionTools.RunCommand), toolCall.Tool);
        Assert.True(toolCall.Success);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextFailsRepeatedly_DowngradesToACriticalLogAtTheThresholdAndThenSuppresses()
    {
        var hostClient = new FakeHostApiClient([], claimNextThrowsForFirstNCalls: 5);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var logger = new RecordingLogger();
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(5), ConsecutiveFailureLogThreshold = 3 }),
            logger);

        await service.StartAsync(CancellationToken.None);
        // Condition-based wait (not a fixed sleep): the 6th call is the first one past
        // the 5 configured failures, so by the time it lands the loop has already run
        // its course through 3 failures (crossing the threshold) and recovered.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hostClient.ClaimCallCount < 6 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        await service.StopAsync(CancellationToken.None);

        Assert.True(hostClient.ClaimCallCount >= 6, "pull loop never reached a 6th claim-next call");
        var errors = logger.Messages.Where(m => m.Level == LogLevel.Error).ToList();
        var criticals = logger.Messages.Where(m => m.Level == LogLevel.Critical).ToList();
        // Threshold is 3: the 1st and 2nd failures log at Error, the 3rd crosses the
        // threshold and logs at Critical instead — and nothing past that, even though
        // 2 more failures (4th, 5th) still happened before the 6th call recovered.
        Assert.Equal(2, errors.Count);
        var critical = Assert.Single(criticals);
        Assert.Contains("3", critical.Message);
        Assert.Contains("suppressing", critical.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithNothingPending_NeverSubmitsAnOutcome()
    {
        var hostClient = new FakeHostApiClient([]);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        Assert.Empty(hostClient.SubmittedFailures);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextSignalsUpdateRequired_SkipsWorkAndTriggersSelfUpdateImmediately()
    {
        var hostClient = new FakeHostApiClient([], updateRequiredForFirstNCalls: 1, recommendedWorkerVersion: "0.2.0");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var selfUpdater = new FakeWorkerSelfUpdater();
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            selfUpdater,
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (selfUpdater.RequestedVersions.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        await service.StopAsync(CancellationToken.None);

        var requested = Assert.Single(selfUpdater.RequestedVersions);
        Assert.Equal("0.2.0", requested);
        Assert.Empty(hostClient.SubmittedReports);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextReturnsOptionalUpdateHint_FinishesWorkThenSelfUpdates()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            recommendedWorkerVersion: "0.2.0");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("looked around, nothing conclusive");
        var selfUpdater = new FakeWorkerSelfUpdater();
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            selfUpdater,
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (selfUpdater.RequestedVersions.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        await service.StopAsync(CancellationToken.None);

        // 일감을 먼저 끝낸 뒤(SubmittedReports에 1건) self-update가 걸려야 한다.
        Assert.Single(hostClient.SubmittedReports);
        var requested = Assert.Single(selfUpdater.RequestedVersions);
        Assert.Equal("0.2.0", requested);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSelfUpdateFailsRepeatedly_DowngradesToACriticalLogAtTheThresholdAndThenSuppresses()
    {
        // Host is refusing this Worker work until it updates, so a failing update is all the loop
        // does — every poll interval, against the same release API, for as long as it runs.
        var hostClient = new FakeHostApiClient([], updateRequiredForFirstNCalls: 5, recommendedWorkerVersion: "0.2.0");
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var logger = new RecordingLogger();
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            new FakeWorkerSelfUpdater(throwsForFirstNCalls: 5),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(5), ConsecutiveFailureLogThreshold = 3 }),
            logger);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hostClient.ClaimCallCount < 6 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        await service.StopAsync(CancellationToken.None);

        Assert.True(hostClient.ClaimCallCount >= 6, "pull loop never reached a 6th claim-next call");
        // The claim itself succeeded every time and only the update failed, so this also pins down
        // when the counter resets: were it cleared on a successful claim, each iteration would
        // start over at one, the threshold would never be reached, and all 5 failures would log at
        // Error with nothing ever suppressed.
        Assert.Equal(2, logger.Messages.Count(m => m.Level == LogLevel.Error));
        var critical = Assert.Single(logger.Messages, m => m.Level == LogLevel.Critical);
        Assert.Contains("suppressing", critical.Message);
    }
}
