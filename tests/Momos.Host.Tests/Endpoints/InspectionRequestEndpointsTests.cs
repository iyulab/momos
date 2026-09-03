using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class InspectionRequestEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client;

    public InspectionRequestEndpointsTests(MomosHostFactory factory) => _client = factory.CreateAuthorizedClient();

    private async Task<Guid> CreateProjectAsync(string? repositoryUrl = null)
    {
        var create = new CreateProjectRequest("acme", repositoryUrl, null, "purpose", "vision", "scope");
        var response = await _client.PostAsJsonAsync("/projects", create);
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        return project!.Id;
    }

    [Fact]
    public async Task PostThenGet_RoundTripsAnInspectionRequestAsPending()
    {
        var projectId = await CreateProjectAsync();

        var postResponse = await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest("focus on login flow", null));
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.NotNull(created);
        Assert.Equal(projectId, created.ProjectId);
        Assert.Equal(InspectionRequestStatus.Pending, created.Status);

        var getResponse = await _client.GetAsync($"/inspection-requests/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task PostWithCommitRef_RoundTripsIt()
    {
        var projectId = await CreateProjectAsync(repositoryUrl: "https://example.invalid/acme/repo.git");

        var postResponse = await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, "abc123^"));

        var created = await postResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal("abc123^", created!.CommitRef);

        var fetched = await (await _client.GetAsync($"/inspection-requests/{created.Id}"))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal("abc123^", fetched!.CommitRef);
    }

    [Fact]
    public async Task PostWithCommitRefButNoRepositoryUrl_Returns400()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, "abc123^"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithUnknownProjectId_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/projects/{Guid.NewGuid()}/inspection-requests", new CreateInspectionRequestRequest(null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithUnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/inspection-requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: 2, WorkerVersion: "0.1.0");

    private async Task<ClaimNextResponse> ClaimNextAsync()
    {
        var response = await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker);
        return (await response.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!;
    }

    // The class fixture's SQLite DB is shared (and test order unspecified) across every
    // [Fact] in this class — drain whatever other tests left Pending before asserting an
    // empty-queue precondition, rather than requiring DB isolation this suite doesn't have.
    private async Task DrainClaimQueueAsync()
    {
        while ((await ClaimNextAsync()).Request is not null)
        {
        }
    }

    [Fact]
    public async Task ClaimNext_WithNoPendingRequests_ReturnsNoRequest()
    {
        await DrainClaimQueueAsync();

        var claimed = await ClaimNextAsync();

        Assert.Null(claimed.Request);
        Assert.False(claimed.UpdateRequired);
    }

    [Fact]
    public async Task ClaimNext_WithAPendingRequest_TransitionsItToRunningAndReturnsIt()
    {
        var projectId = await CreateProjectAsync();
        var created = await (await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null)))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        var claimed = await ClaimNextAsync();

        Assert.NotNull(claimed.Request);
        Assert.Equal(created!.Id, claimed.Request.Id);
        Assert.Equal(InspectionRequestStatus.Running, claimed.Request.Status);
        Assert.NotNull(claimed.Request.ClaimedAt);
    }

    [Fact]
    public async Task ClaimNext_CalledTwiceWithOnlyOnePending_SecondCallReturnsNoRequest()
    {
        await DrainClaimQueueAsync();
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));

        var first = await ClaimNextAsync();
        var second = await ClaimNextAsync();

        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
    }

    [Fact]
    public async Task Fail_WithUnknownId_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/inspection-requests/{Guid.NewGuid()}/fail", new FailInspectionRequestRequest("boom"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Fail_WhileStillPending_Returns409()
    {
        var projectId = await CreateProjectAsync();
        var created = await (await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null)))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        var response = await _client.PostAsJsonAsync(
            $"/inspection-requests/{created!.Id}/fail", new FailInspectionRequestRequest("boom"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Fail_AfterClaim_TransitionsToFailedWithReason()
    {
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimed = await ClaimNextAsync();

        var response = await _client.PostAsJsonAsync(
            $"/inspection-requests/{claimed.Request!.Id}/fail", new FailInspectionRequestRequest("agent loop crashed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var failed = await response.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Failed, failed!.Status);
        Assert.Equal("agent loop crashed", failed.FailureReason);
    }
}
