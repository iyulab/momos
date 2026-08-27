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

        // No API creates reports yet (that's the Worker-execution increment, out of
        // scope here) — seed one directly through the DbContext to test the read side.
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
}
