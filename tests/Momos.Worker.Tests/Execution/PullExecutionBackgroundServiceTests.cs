using IronHive.Agent.Extensions;
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Agent;

namespace Momos.Worker.Tests.Execution;

public sealed class PullExecutionBackgroundServiceTests
{
    private static IAgentLoopFactory BuildFakeAgentLoopFactory(string reply)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new FakeChatClientProvider(reply));
        services.AddIronHiveAgentEngine();
        return services.BuildServiceProvider().GetRequiredService<IAgentLoopFactory>();
    }

    [Fact]
    public async Task ExecuteAsync_ClaimedRequest_RunsAgentLoopAndSubmitsEmptyFindingsReport()
    {
        var hostClient = new FakeHostApiClient(
        [
            new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), "focus", DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null),
        ]);
        var service = new PullExecutionBackgroundService(
            hostClient,
            BuildFakeAgentLoopFactory("looked around, nothing conclusive"),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var report = Assert.Single(hostClient.SubmittedReports);
        Assert.Empty(report.Findings);
        Assert.Empty(hostClient.SubmittedFailures);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGetProjectThrows_SubmitsFailureWithTheExceptionMessage()
    {
        var requestId = Guid.NewGuid();
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(requestId, Guid.NewGuid(), null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            getProjectThrows: true);
        var service = new PullExecutionBackgroundService(
            hostClient,
            BuildFakeAgentLoopFactory("unused"),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await hostClient.WaitForOutcomeAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        var failure = Assert.Single(hostClient.SubmittedFailures);
        Assert.Equal(requestId, failure.Id);
        Assert.Equal("boom", failure.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimNextThrows_SurvivesAndClaimsOnTheNextPoll()
    {
        var hostClient = new FakeHostApiClient(
            [new ClaimedInspectionRequest(Guid.NewGuid(), Guid.NewGuid(), null, DateTimeOffset.UtcNow, InspectionRequestStatus.Running, null)],
            claimNextThrowsForFirstNCalls: 2);
        var service = new PullExecutionBackgroundService(
            hostClient,
            BuildFakeAgentLoopFactory("looked around, nothing conclusive"),
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
    public async Task ExecuteAsync_WithNothingPending_NeverSubmitsAnOutcome()
    {
        var hostClient = new FakeHostApiClient([]);
        var service = new PullExecutionBackgroundService(
            hostClient,
            BuildFakeAgentLoopFactory("unused"),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(20) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(hostClient.SubmittedReports);
        Assert.Empty(hostClient.SubmittedFailures);
    }
}
