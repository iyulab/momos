using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public sealed class HostApiClientTests : IClassFixture<TestMomosHostFactory>
{
    private readonly TestMomosHostFactory _factory;
    private readonly HostApiClient _client;

    public HostApiClientTests(TestMomosHostFactory factory)
    {
        _factory = factory;
        _client = new HostApiClient(factory.CreateAuthorizedClient());
    }

    private async Task<Guid> CreateProjectAsync()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var response = await httpClient.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        return project!.Id;
    }

    [Fact]
    public async Task ClaimNextAsync_WithNoPendingRequests_ReturnsNull()
    {
        // Drain whatever earlier tests in this shared fixture left pending.
        while (await _client.ClaimNextAsync(CancellationToken.None) is not null)
        {
        }

        var claimed = await _client.ClaimNextAsync(CancellationToken.None);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task FullRoundTrip_ClaimGetProjectSubmitReport_MatchesHostState()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var projectId = await CreateProjectAsync();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest("focus on login", null));

        var claimed = await _client.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(InspectionRequestStatus.Running, claimed!.Status);

        var project = await _client.GetProjectAsync(claimed.ProjectId, CancellationToken.None);
        Assert.Equal(projectId, project.Id);
        Assert.Equal("acme", project.Name);

        await _client.SubmitReportAsync(
            claimed.Id, [new FindingPayload(Category: 0, Description: "d", Evidence: "e")], CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{claimed.Id}");
        var after = await afterResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Completed, after!.Status);
    }

    [Fact]
    public async Task SubmitFailureAsync_TransitionsRequestToFailedWithReason()
    {
        var projectId = await CreateProjectAsync();
        var httpClient = _factory.CreateAuthorizedClient();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));

        var claimed = await _client.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        await _client.SubmitFailureAsync(claimed!.Id, "agent loop crashed", CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{claimed.Id}");
        var after = await afterResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Failed, after!.Status);
        Assert.Equal("agent loop crashed", after.FailureReason);
    }
}
