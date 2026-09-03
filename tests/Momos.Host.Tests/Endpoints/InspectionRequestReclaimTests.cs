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

    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: 2, WorkerVersion: "0.1.0");

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
}
