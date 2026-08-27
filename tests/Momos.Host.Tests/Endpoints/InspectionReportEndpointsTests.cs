using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class InspectionReportEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly MomosHostFactory _factory;
    private readonly HttpClient _client;

    public InspectionReportEndpointsTests(MomosHostFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_BeforeAReportExists_Returns404()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var requestResponse = await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>();

        var response = await _client.GetAsync($"/inspection-requests/{inspectionRequest!.Id}/report");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_AfterAReportExists_ReturnsItWithFindings()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var requestResponse = await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>();

        // Seed directly through the DbContext (bypassing the Running-status precondition
        // the POST endpoint enforces) so this test isolates the read side only.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MomosDbContext>();
            var report = new InspectionReport { InspectionRequestId = inspectionRequest!.Id };
            report.Findings.Add(new Finding
            {
                InspectionReportId = report.Id,
                Category = FindingCategory.UxConsistency,
                Description = "inconsistent button labels",
                Evidence = "screen A uses 'Submit', screen B uses 'Send'",
            });
            db.InspectionReports.Add(report);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/inspection-requests/{inspectionRequest!.Id}/report");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report2 = await response.Content.ReadFromJsonAsync<InspectionReportResponse>();
        Assert.Single(report2!.Findings);
        Assert.Equal(FindingCategory.UxConsistency, report2.Findings[0].Category);
    }

    [Fact]
    public async Task Post_WithUnknownId_Returns404()
    {
        var request = new SubmitInspectionReportRequest([]);

        var response = await _client.PostAsJsonAsync($"/inspection-requests/{Guid.NewGuid()}/report", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhileStillPending_Returns409()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        var requestResponse = await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>();

        var response = await _client.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequest!.Id}/report", new SubmitInspectionReportRequest([]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_AfterClaim_CreatesReportAndCompletesTheRequest()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null));
        var claimed = await (await _client.PostAsync("/inspection-requests/claim-next", content: null))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>();

        var submission = new SubmitInspectionReportRequest(
        [
            new SubmitFindingRequest(FindingCategory.FunctionalDefect, "login button does nothing", "clicked 3x, no navigation"),
        ]);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<InspectionReportResponse>();
        Assert.Single(report!.Findings);

        var requestAfter = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}"))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>();
        Assert.Equal(InspectionRequestStatus.Completed, requestAfter!.Status);
    }
}
