using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

/// <summary>
/// Exercises claim-next's reclaim of a <see cref="InspectionRequestStatus.Running"/> request
/// whose worker went away — owns its own <see cref="MomosHostFactory"/> (rather than sharing
/// <see cref="InspectionRequestEndpointsTests"/>'s) so it can set a far shorter
/// <c>ReclaimTimeout</c> than production, to observe a claim going stale within a test's
/// lifetime.
/// </summary>
public sealed class InspectionRequestReclaimTests : IDisposable
{
    private readonly MomosHostFactory _factory = new() { InspectionClaimReclaimTimeout = TimeSpan.FromMilliseconds(100) };
    private readonly HttpClient _client;

    public InspectionRequestReclaimTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<InspectionRequestResponse> CreateAndClaimAsync()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));

        var claimed = await (await _client.PostAsync("/inspection-requests/claim-next", content: null))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        return claimed!;
    }

    [Fact]
    public async Task ClaimNext_WhenClaimedRequestIsWithinReclaimTimeout_DoesNotReclaimIt()
    {
        var claimed = await CreateAndClaimAsync();
        Assert.NotNull(claimed.ClaimedAt);

        var response = await _client.PostAsync("/inspection-requests/claim-next", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ClaimNext_WhenClaimedRequestIsPastReclaimTimeout_ReclaimsItWithANewerClaimedAt()
    {
        var claimed = await CreateAndClaimAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var response = await _client.PostAsync("/inspection-requests/claim-next", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reclaimed = await response.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(claimed.Id, reclaimed!.Id);
        Assert.Equal(InspectionRequestStatus.Running, reclaimed.Status);
        Assert.True(reclaimed.ClaimedAt > claimed.ClaimedAt);
    }
}
