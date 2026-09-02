using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;

namespace Momos.Host.Tests.Endpoints;

public sealed class ProjectEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client;

    public ProjectEndpointsTests(MomosHostFactory factory) => _client = factory.CreateAuthorizedClient();

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
        // A RFC 7807 body (not an empty one) so clients can parse every error the same way.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Post_WithMissingName_Returns400()
    {
        var create = new CreateProjectRequest("", null, null, "purpose", "vision", "scope");

        var response = await _client.PostAsJsonAsync("/projects", create);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostThenGet_RoundTripsAProjectWithAppInstall()
    {
        var create = new CreateProjectRequest(
            "acme", null, null, "purpose", "vision", "scope",
            AppInstallerUri: "https://example.invalid/acme-setup.exe",
            AppInstallPlatform: "win-x64",
            AppInstallArgs: "/silent",
            AppInstallLaunchCommand: "acme.exe");

        var postResponse = await _client.PostAsJsonAsync("/projects", create);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(created);
        Assert.Equal("https://example.invalid/acme-setup.exe", created.AppInstallerUri);
        Assert.Equal("win-x64", created.AppInstallPlatform);
        Assert.Equal("/silent", created.AppInstallArgs);
        Assert.Equal("acme.exe", created.AppInstallLaunchCommand);

        var getResponse = await _client.GetAsync($"/projects/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.Equal("win-x64", fetched!.AppInstallPlatform);
    }

    [Fact]
    public async Task Post_WithPartialAppInstallFields_Returns400()
    {
        var create = new CreateProjectRequest(
            "acme", null, null, "purpose", "vision", "scope",
            AppInstallerUri: "https://example.invalid/acme-setup.exe");
            // AppInstallPlatform and AppInstallLaunchCommand deliberately omitted

        var response = await _client.PostAsJsonAsync("/projects", create);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
