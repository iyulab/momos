using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class InspectionRequestEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client;

    public InspectionRequestEndpointsTests(MomosHostFactory factory) => _client = factory.CreateClient();

    private async Task<Guid> CreateProjectAsync()
    {
        var create = new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope");
        var response = await _client.PostAsJsonAsync("/projects", create);
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        return project!.Id;
    }

    [Fact]
    public async Task PostThenGet_RoundTripsAnInspectionRequestAsPending()
    {
        var projectId = await CreateProjectAsync();

        var postResponse = await _client.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest("focus on login flow"));
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>();
        Assert.NotNull(created);
        Assert.Equal(projectId, created.ProjectId);
        Assert.Equal(InspectionRequestStatus.Pending, created.Status);

        var getResponse = await _client.GetAsync($"/inspection-requests/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>();
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Post_WithUnknownProjectId_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/projects/{Guid.NewGuid()}/inspection-requests", new CreateInspectionRequestRequest(null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithUnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/inspection-requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
