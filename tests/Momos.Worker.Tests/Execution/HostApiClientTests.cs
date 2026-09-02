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
    public async Task ClaimNextAsync_WithNoPendingRequests_ReturnsResultWithNullRequest()
    {
        // Drain whatever earlier tests in this shared fixture left pending.
        while ((await _client.ClaimNextAsync(CancellationToken.None)).Request is not null)
        {
        }

        var result = await _client.ClaimNextAsync(CancellationToken.None);

        Assert.Null(result.Request);
        Assert.False(result.UpdateRequired);
    }

    [Fact]
    public async Task QueryKnowledgeAsync_ReturnsSnippetsFromHost()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var projectId = await CreateProjectAsync();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/knowledge/documents",
            new { Title = "PRD", Content = "The checkout flow must support Apple Pay." });

        var snippets = await _client.QueryKnowledgeAsync(projectId, "Apple Pay", CancellationToken.None);

        Assert.Contains(snippets, s => s.Contains("Apple Pay"));
    }

    [Fact]
    public async Task FullRoundTrip_ClaimGetProjectSubmitReport_MatchesHostState()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var projectId = await CreateProjectAsync();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest("focus on login", null));

        var result = await _client.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(result.Request);
        Assert.Equal(InspectionRequestStatus.Running, result.Request!.Status);

        var project = await _client.GetProjectAsync(result.Request.ProjectId, CancellationToken.None);
        Assert.Equal(projectId, project.Id);
        Assert.Equal("acme", project.Name);

        await _client.SubmitReportAsync(
            result.Request.Id, [new FindingPayload(Category: 0, Description: "d", Evidence: "e")], CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{result.Request.Id}");
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

        var result = await _client.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(result.Request);

        await _client.SubmitFailureAsync(result.Request!.Id, "agent loop crashed", CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{result.Request.Id}");
        var after = await afterResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Failed, after!.Status);
        Assert.Equal("agent loop crashed", after.FailureReason);
    }

    [Fact]
    public async Task ClaimNextAsync_SendsWorkerVersionInfo_AndHostEchoesNoUpdateRequired()
    {
        var result = await _client.ClaimNextAsync(CancellationToken.None);

        // 이 테스트가 통과한다는 것 자체가 Host가 요청 body(ProtocolVersion=WorkerVersionInfo.ProtocolVersion)를
        // 파싱해 200을 돌려줬다는 뜻 — appsettings.Development.json의 MinSupportedProtocolVersion
        // 기본값(1)과 WorkerVersionInfo.ProtocolVersion(1)이 일치해야 한다.
        Assert.False(result.UpdateRequired);
    }

    [Fact]
    public async Task GetProjectAsync_DeserializesAppInstallFields()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var createResponse = await httpClient.PostAsJsonAsync(
            "/projects",
            new CreateProjectRequest(
                "acme", null, null, "purpose", "vision", "scope",
                AppInstallerUri: "https://example.invalid/setup.exe",
                AppInstallPlatform: "win-x64",
                AppInstallArgs: null,
                AppInstallLaunchCommand: "acme.exe"));
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var project = await _client.GetProjectAsync(created!.Id, CancellationToken.None);

        Assert.Equal("https://example.invalid/setup.exe", project.AppInstallerUri);
        Assert.Equal("win-x64", project.AppInstallPlatform);
        Assert.Null(project.AppInstallArgs);
        Assert.Equal("acme.exe", project.AppInstallLaunchCommand);
    }
}
