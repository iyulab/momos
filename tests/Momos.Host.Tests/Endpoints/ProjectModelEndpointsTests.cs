using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class ProjectModelEndpointsTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: 3, WorkerVersion: "0.1.0");
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    /// <summary>Creates a project + analysis request and claims it, leaving it Running. Drains
    /// the shared queue first so the claim returns this request.</summary>
    internal static async Task<(Guid ProjectId, Guid RequestId)> StartAnalysisAsync(HttpClient client, Guid? projectId = null)
    {
        while ((await (await client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        if (projectId is null)
        {
            var project = await client.PostAsJsonAsync("/projects",
                new CreateProjectRequest("acme-model", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
            projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        }

        await client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        var claim = await (await client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        return (projectId.Value, claim!.Request!.Id);
    }

    internal static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid requestId, SubmitProjectModelRequest model) =>
        await client.PostAsJsonAsync($"/analysis-requests/{requestId}/model", model, TestJsonOptions.Value);

    [Fact]
    public async Task Submit_AValidModel_StoresVersion1AndCompletesTheRequest()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);

        var response = await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var model = await response.Content.ReadFromJsonAsync<ProjectModelResponse>(TestJsonOptions.Value);
        Assert.Equal(1, model!.ModelVersion);
        Assert.Equal("abc123", model.BaseCommit);
        Assert.All(model.Claims, c => Assert.Equal(ClaimStatus.Proposed, c.Status));
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Completed, request!.Status);

        var latest = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(model.ModelVersion, latest!.ModelVersion);
        Assert.Equal(ModelDecision.Unrecorded, Assert.Single(latest.Decisions).Rationale);
    }

    [Fact]
    public async Task Submit_AClaimWithNoEvidence_Returns400AndLeavesTheRequestRunning()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);
        var invalid = ModelFixtures.ValidSubmission() with
        {
            Claims = [.. ModelFixtures.ValidSubmission().Claims, new SubmittedClaim("clm.bare", ClaimTier.Assessment, "looks fine", [], ClaimConfidence.Low)],
        };

        var response = await SubmitAsync(_client, requestId, invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Running, request!.Status);
    }

    [Fact]
    public async Task Submit_WithAnOmittedList_Returns400AndLeavesTheRequestRunning()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);

        // A body that binds with Claims left null — a malformed submission, not a server fault.
        var response = await _client.PostAsJsonAsync($"/analysis-requests/{requestId}/model",
            new
            {
                baseCommit = "abc123",
                components = Array.Empty<object>(),
                relations = Array.Empty<object>(),
                patterns = Array.Empty<object>(),
                decisions = Array.Empty<object>(),
                intents = Array.Empty<object>()
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Running, request!.Status);
    }

    [Fact]
    public async Task Submit_ForAnInspectionKindRequest_Returns409()
    {
        while ((await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-mismatch", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        await _client.PostAsJsonAsync($"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claim = await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        var response = await SubmitAsync(_client, claim!.Request!.Id, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Submit_Twice_ForTheSameRequest_Returns409TheSecondTime()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var second = await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetModel_ForAProjectNeverAnalyzed_Returns404()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-none", null, null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;

        var response = await _client.GetAsync($"/projects/{projectId}/model");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
