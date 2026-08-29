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
    private static (ISessionAwareAgentLoopFactory Factory, FakeExecutionRuntimeProvider ExecutionProvider) BuildFakeAgentLoopFactory(string reply) =>
        BuildFakeAgentLoopFactory(new FakeChatClientProvider(reply));

    private static (ISessionAwareAgentLoopFactory Factory, FakeExecutionRuntimeProvider ExecutionProvider) BuildFakeAgentLoopFactory(IChatClientProvider chatClientProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClientProvider);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var executionProvider = new FakeExecutionRuntimeProvider();
        services.AddSingleton<IExecutionRuntimeProvider>(executionProvider);
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
    public async Task ExecuteAsync_WithNothingPending_NeverSubmitsAnOutcome()
    {
        var hostClient = new FakeHostApiClient([]);
        var (agentLoopFactory, executionProvider) = BuildFakeAgentLoopFactory("unused");
        var service = new PullExecutionBackgroundService(
            hostClient,
            agentLoopFactory,
            executionProvider,
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        Assert.Empty(hostClient.SubmittedFailures);
    }
}
