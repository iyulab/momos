using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;

namespace Momos.Host.Tests.Endpoints;

public sealed class ProjectEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client;

    public ProjectEndpointsTests(MomosHostFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task PostThenGet_RoundTripsAProject()
    {
        var create = new CreateProjectRequest("acme", "https://example.invalid/acme.git", null, "purpose", "vision", "scope");

        var postResponse = await _client.PostAsJsonAsync("/projects", create);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(created);
        Assert.Equal("acme", created.Name);

        var getResponse = await _client.GetAsync($"/projects/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Get_WithUnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/projects/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithMissingName_Returns400()
    {
        var create = new CreateProjectRequest("", null, null, "purpose", "vision", "scope");

        var response = await _client.PostAsJsonAsync("/projects", create);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
