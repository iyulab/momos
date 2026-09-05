using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Momos.Host.Contracts;

namespace Momos.Host.Tests.Endpoints;

/// <summary>
/// Covers the <see cref="Momos.Host.Endpoints.WorkerApiKeyFilter"/> guard on the
/// worker-facing write endpoints. Every other test in this project goes through
/// <see cref="MomosHostFactory.CreateAuthorizedClient()"/>, which always carries a matching key —
/// this class is the one place that deliberately doesn't, to prove the guard actually
/// rejects requests without it.
/// </summary>
public sealed class WorkerAuthTests : IClassFixture<MomosHostFactory>
{
    private readonly MomosHostFactory _factory;

    public WorkerAuthTests(MomosHostFactory factory) => _factory = factory;

    [Fact]
    public async Task ClaimNext_WithoutAuthorizationHeader_ReturnsUnauthorized()
    {
        var client = _factory.CreateDefaultClient();

        var response = await client.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimNext_WithWrongApiKey_ReturnsUnauthorized()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-configured-key");

        var response = await client.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimNext_WithoutAuthorizationHeaderAndNoBody_ReturnsBadRequestNotUnauthorized()
    {
        // Documents current (surprising) behavior, not a desired one — see the ordering note
        // on WorkerApiKeyFilter. Required-body model binding runs before endpoint filters, so
        // a missing body short-circuits with 400 before WorkerApiKeyFilter ever inspects the
        // (also missing) Authorization header. If a future change makes the body optional and
        // this flips to 401, this test should start failing and needs updating deliberately —
        // not silently drift unnoticed either way.
        var client = _factory.CreateDefaultClient();

        var response = await client.PostAsync("/inspection-requests/claim-next", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitReport_WithoutAuthorizationHeader_ReturnsUnauthorized()
    {
        var unauthenticated = _factory.CreateDefaultClient();
        var authenticated = _factory.CreateAuthorizedClient();
        var projectResponse = await authenticated.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        await authenticated.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest("focus", null));
        var claimed = await (await authenticated.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0")))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        var response = await unauthenticated.PostAsJsonAsync(
            $"/inspection-requests/{claimed!.Request!.Id}/report", new SubmitInspectionReportRequest([], []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Fail_WithoutAuthorizationHeader_ReturnsUnauthorized()
    {
        var unauthenticated = _factory.CreateDefaultClient();

        var response = await unauthenticated.PostAsJsonAsync(
            $"/inspection-requests/{Guid.NewGuid()}/fail", new FailInspectionRequestRequest("boom", []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetInspectionReport_WithoutAuthorizationHeader_StillSucceeds()
    {
        // The read-side report/project/inspection-request endpoints are a separate
        // integration surface (external callers, not workers) and are deliberately
        // outside this filter's scope.
        var unauthenticated = _factory.CreateDefaultClient();

        var response = await unauthenticated.GetAsync($"/inspection-requests/{Guid.NewGuid()}/report");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
