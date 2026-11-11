using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

/// <summary>
/// Exercises claim-next's reclaim of a <see cref="InspectionRequestStatus.Running"/> request
/// whose worker went away — owns its own <see cref="MomosHostFactory"/> (rather than sharing
/// <see cref="InspectionRequestEndpointsTests"/>'s) so it can wire in a
/// <see cref="FakeTimeProvider"/> and advance it deterministically past the reclaim timeout,
/// instead of relying on a real-time delay racing the claim-next round trip (that raced under
/// load — see git history on this file).
/// </summary>
public sealed class InspectionRequestReclaimTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private readonly MomosHostFactory _factory;
    private HttpClient _client = null!;

    public InspectionRequestReclaimTests()
    {
        _factory = new MomosHostFactory { TimeProvider = _time };
    }

    // Not an IClassFixture, so xUnit won't drive the factory's own IAsyncLifetime — this class
    // owns that lifecycle itself: start the factory's containers before the client is created,
    // and tear them down (async containers, then the base WebApplicationFactory's own sync
    // disposal) after each test.
    public async Task InitializeAsync()
    {
        await _factory.InitializeAsync();
        _client = _factory.CreateAuthorizedClient();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_factory).DisposeAsync();
        _factory.Dispose();
    }

    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: TestProtocol.Current, WorkerVersion: "0.1.0");

    private async Task<ClaimNextResponse> ClaimNextAsync()
    {
        var response = await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker);
        return (await response.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!;
    }

    private async Task<InspectionRequestResponse> CreateAndClaimAsync()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));

        var claimed = await ClaimNextAsync();
        return claimed.Request!;
    }

    [Fact]
    public async Task ClaimNext_WhenClaimedRequestIsWithinReclaimTimeout_DoesNotReclaimIt()
    {
        var claimed = await CreateAndClaimAsync();
        Assert.NotNull(claimed.ClaimedAt);

        var response = await ClaimNextAsync();

        Assert.Null(response.Request);
    }

    [Fact]
    public async Task ClaimNext_WhenClaimedRequestIsPastReclaimTimeout_ReclaimsItWithANewerClaimedAt()
    {
        var claimed = await CreateAndClaimAsync();
        _time.Advance(_factory.InspectionClaimReclaimTimeout + TimeSpan.FromSeconds(1));

        var response = await ClaimNextAsync();

        Assert.NotNull(response.Request);
        var reclaimed = response.Request;
        Assert.Equal(claimed.Id, reclaimed.Id);
        Assert.Equal(InspectionRequestStatus.Running, reclaimed.Status);
        Assert.True(reclaimed.ClaimedAt > claimed.ClaimedAt);
    }

    private async Task<InspectionRequestResponse> GetAsync(Guid id) =>
        (await _client.GetFromJsonAsync<InspectionRequestResponse>($"/inspection-requests/{id}", TestJsonOptions.Value))!;

    private async Task StaleAsync() => _time.Advance(_factory.InspectionClaimReclaimTimeout + TimeSpan.FromSeconds(1));

    [Fact]
    public async Task ARequestClaimedThreeTimesWhoseWorkerKeepsStopping_IsFailedInsteadOfReclaimedAgain()
    {
        var first = await CreateAndClaimAsync();
        for (var claim = 2; claim <= 3; claim++)
        {
            await StaleAsync();
            Assert.Equal(first.Id, (await ClaimNextAsync()).Request!.Id);
        }

        await StaleAsync();
        var response = await ClaimNextAsync();

        Assert.Null(response.Request);
        var ended = await GetAsync(first.Id);
        Assert.Equal(InspectionRequestStatus.Failed, ended.Status);
        Assert.Equal("Abandoned after 3 attempts: the Worker running it stopped reporting each time.", ended.FailureReason);
    }

    [Fact]
    public async Task ARequestStillWithinItsClaims_IsReclaimedAsBefore()
    {
        var first = await CreateAndClaimAsync();
        await StaleAsync();
        Assert.Equal(first.Id, (await ClaimNextAsync()).Request!.Id);
        await StaleAsync();

        Assert.Equal(first.Id, (await ClaimNextAsync()).Request!.Id);
        Assert.Equal(InspectionRequestStatus.Running, (await GetAsync(first.Id)).Status);
    }

    [Fact]
    public async Task AnAbandonedCheckupExam_EndsAsNotRun_AndTheCheckupCompletes()
    {
        var project = await (await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", "https://example.invalid/acme.git", null, "purpose", "vision", "scope")))
            .Content.ReadFromJsonAsync<ProjectResponse>();
        var created = await _client.PostAsJsonAsync($"/projects/{project!.Id}/checkups", new { });
        var checkup = (await created.Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value))!;
        Assert.Equal(checkup.Exams[0].RequestId, (await ClaimNextAsync()).Request!.Id);
        for (var claim = 2; claim <= 3; claim++)
        {
            await StaleAsync();
            Assert.NotNull((await ClaimNextAsync()).Request);
        }

        await StaleAsync();
        Assert.Null((await ClaimNextAsync()).Request);

        var ended = (await _client.GetFromJsonAsync<CheckupResponse>($"/checkups/{checkup.Id}", TestJsonOptions.Value))!;
        Assert.Equal(CheckupStatus.Completed, ended.Status);
        var exam = Assert.Single(ended.Exams);
        Assert.Equal(ExamRunStatus.NotRun, exam.Status);
        Assert.StartsWith("Abandoned after 3 attempts", exam.Reason, StringComparison.Ordinal);
    }
}
