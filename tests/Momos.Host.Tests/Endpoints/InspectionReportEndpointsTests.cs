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
        _client = factory.CreateAuthorizedClient();
    }

    [Fact]
    public async Task Get_BeforeAReportExists_Returns404()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var requestResponse = await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

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
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

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
        var report2 = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Single(report2!.Findings);
        Assert.Equal(FindingCategory.UxConsistency, report2.Findings[0].Category);
    }

    [Fact]
    public async Task Get_WithMultipleFindings_ReturnsThemOrderedByFindingOrderNotInsertionSequence()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var requestResponse = await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        // Insert deliberately out of intended order — SQLite/EF give no ordering
        // guarantee for an unordered Include, so a test that only ever inserts
        // findings in their intended order could pass by insertion-sequence
        // coincidence rather than by the Order column actually being honored.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MomosDbContext>();
            var report = new InspectionReport { InspectionRequestId = inspectionRequest!.Id };
            report.Findings.Add(new Finding
            {
                InspectionReportId = report.Id,
                Order = 2,
                Category = FindingCategory.UxConsistency,
                Description = "third",
                Evidence = "e",
            });
            report.Findings.Add(new Finding
            {
                InspectionReportId = report.Id,
                Order = 0,
                Category = FindingCategory.FunctionalDefect,
                Description = "first",
                Evidence = "e",
            });
            report.Findings.Add(new Finding
            {
                InspectionReportId = report.Id,
                Order = 1,
                Category = FindingCategory.FunctionalDefect,
                Description = "second",
                Evidence = "e",
            });
            db.InspectionReports.Add(report);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/inspection-requests/{inspectionRequest!.Id}/report");

        var report2 = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["first", "second", "third"], report2!.Findings.Select(f => f.Description));
    }

    [Fact]
    public async Task Post_WithMultipleFindings_ReturnsThemInSubmittedOrder()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimed = await (await _client.PostAsync("/inspection-requests/claim-next", content: null))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        var submission = new SubmitInspectionReportRequest(
        [
            new SubmitFindingRequest(FindingCategory.FunctionalDefect, "first", "e"),
            new SubmitFindingRequest(FindingCategory.UxConsistency, "second", "e"),
            new SubmitFindingRequest(FindingCategory.FunctionalDefect, "third", "e"),
        ]);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);
        var created = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["first", "second", "third"], created!.Findings.Select(f => f.Description));

        var fetched = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}/report"))
            .Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["first", "second", "third"], fetched!.Findings.Select(f => f.Description));
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
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var inspectionRequest = await requestResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

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
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimed = await (await _client.PostAsync("/inspection-requests/claim-next", content: null))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);

        var submission = new SubmitInspectionReportRequest(
        [
            new SubmitFindingRequest(FindingCategory.FunctionalDefect, "login button does nothing", "clicked 3x, no navigation"),
        ]);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Single(report!.Findings);

        var requestAfter = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}"))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Completed, requestAfter!.Status);
    }
}
