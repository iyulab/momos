using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class AnalysisRequestEndpointsTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: TestProtocol.Current, WorkerVersion: "0.1.0");
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    private async Task<Guid> CreateProjectAsync(string? repositoryUrl = "https://example.invalid/acme.git")
    {
        var response = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-analysis", repositoryUrl, null, "purpose", "vision", "scope"));
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
    }

    [Fact]
    public async Task Create_ForAProjectWithARepository_Returns201WithAnalysisKind()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(InspectionRequestKind.Analysis, body!.Kind);
        Assert.Equal(InspectionRequestStatus.Pending, body.Status);
        Assert.Equal($"/analysis-requests/{body.Id}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Create_WithoutABody_Returns201()
    {
        // commitRef is the only field and it is optional, so a caller can reasonably send no body.
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsync($"/projects/{projectId}/analysis-requests", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Null(body!.CommitRef);
    }

    [Fact]
    public async Task Create_ForAProjectWithoutARepository_Returns400()
    {
        var projectId = await CreateProjectAsync(repositoryUrl: null);

        var response = await _client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAMissingProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync($"/projects/{Guid.NewGuid()}/analysis-requests", new CreateAnalysisRequestRequest(null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_ForAnInspectionKindRequest_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var created = await _client.PostAsJsonAsync($"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var inspection = await created.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        var response = await _client.GetAsync($"/analysis-requests/{inspection!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(InspectionRequestKind.Inspection, inspection.Kind);
    }

    [Fact]
    public async Task ClaimNext_HandsOutAnAnalysisRequestWithItsKind()
    {
        // Drain whatever other tests in this shared fixture left pending.
        while ((await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest("abc123"));

        var claim = await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        Assert.Equal(InspectionRequestKind.Analysis, claim!.Request!.Kind);
        Assert.Equal("abc123", claim.Request.CommitRef);
    }

    [Fact]
    public async Task SubmitInspectionReport_ForAnAnalysisRequest_Returns409()
    {
        while ((await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        var claim = await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claim!.Request!.Id}/report",
            new SubmitInspectionReportRequest([], []));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ClaimNext_WithProtocolVersion2_IsNowRefusedWithUpdateRequired()
    {
        var response = await _client.PostAsJsonAsync("/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0"));

        var body = await response.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        Assert.True(body!.UpdateRequired);
        Assert.Null(body.Request);
    }
}
