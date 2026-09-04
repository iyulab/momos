using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Knowledge;

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

    private async Task<InspectionRequestResponse> ClaimNextAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0"));
        var envelope = await response.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        return envelope!.Request!;
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
        // the POST endpoint enforces) so this test isolates the read side only. Still moves
        // the request to Completed (not left Pending) -- a Pending request with a report
        // already attached is a state the real state machine never produces, and leaving it
        // that way makes this request an eligible-but-poisoned pick for any other test in
        // this shared-fixture class that calls the generic claim-next endpoint.
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
            var trackedRequest = await db.InspectionRequests.FindAsync([inspectionRequest.Id]);
            trackedRequest!.Status = InspectionRequestStatus.Completed;
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

        // Insert deliberately out of intended order — PostgreSQL/EF give no ordering
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
            // See the same-shaped seeding in Get_AfterAReportExists_ReturnsItWithFindings for
            // why this moves the request to Completed rather than leaving it Pending.
            var trackedRequest = await db.InspectionRequests.FindAsync([inspectionRequest!.Id]);
            trackedRequest!.Status = InspectionRequestStatus.Completed;
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
        var claimed = await ClaimNextAsync();

        var submission = new SubmitInspectionReportRequest(
            Findings:
            [
                new SubmitFindingRequest(FindingCategory.FunctionalDefect, "first", "e"),
                new SubmitFindingRequest(FindingCategory.UxConsistency, "second", "e"),
                new SubmitFindingRequest(FindingCategory.FunctionalDefect, "third", "e"),
            ],
            ToolCalls: []);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);
        var created = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["first", "second", "third"], created!.Findings.Select(f => f.Description));

        var fetched = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}/report"))
            .Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["first", "second", "third"], fetched!.Findings.Select(f => f.Description));
    }

    [Fact]
    public async Task Post_WithToolCalls_PersistsAndReturnsThemInSubmittedOrder()
    {
        var projectResponse = await _client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest("acme", null, null, "purpose", "vision", "scope"));
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimed = await ClaimNextAsync();

        var submission = new SubmitInspectionReportRequest(
            Findings: [],
            ToolCalls:
            [
                new SubmitToolCallRequest("RunCommand", "ls -la", Success: true, DurationMs: 42),
                new SubmitToolCallRequest("QueryProjectKnowledge", "login flow", Success: false, DurationMs: 8),
            ]);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["RunCommand", "QueryProjectKnowledge"], created!.ToolCalls.Select(t => t.Tool));
        Assert.Equal(["ls -la", "login flow"], created.ToolCalls.Select(t => t.Summary));
        Assert.Equal([true, false], created.ToolCalls.Select(t => t.Success));
        Assert.Equal([42, 8], created.ToolCalls.Select(t => t.DurationMs));

        var fetched = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}/report"))
            .Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Equal(["RunCommand", "QueryProjectKnowledge"], fetched!.ToolCalls.Select(t => t.Tool));
    }

    [Fact]
    public async Task Post_WithUnknownId_Returns404()
    {
        var request = new SubmitInspectionReportRequest([], []);

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
            $"/inspection-requests/{inspectionRequest!.Id}/report", new SubmitInspectionReportRequest([], []));

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
        var claimed = await ClaimNextAsync();

        var submission = new SubmitInspectionReportRequest(
            Findings:
            [
                new SubmitFindingRequest(FindingCategory.FunctionalDefect, "login button does nothing", "clicked 3x, no navigation"),
            ],
            ToolCalls: []);
        var response = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Single(report!.Findings);

        var requestAfter = await (await _client.GetAsync($"/inspection-requests/{claimed.Id}"))
            .Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Completed, requestAfter!.Status);
    }

    [Fact(Skip = "Known issue: FluxIndex vector-dimension mismatch (PostgreSQLOptions.EmbeddingDimensions defaults to 1536, project uses 1024) breaks knowledge indexing on PostgreSQL. See claudedocs/issues/ISSUE-momos-20260904-knowledge-vector-dimension-mismatch.md.")]
    public async Task Post_IndexesEachFindingIntoTheProjectKnowledgeBase()
    {
        var project = await (await _client.PostAsJsonAsync(
                "/projects", new CreateProjectRequest("acme-knowledge-index-test", null, null, "purpose", "vision", "scope")))
            .Content.ReadFromJsonAsync<ProjectResponse>();
        await _client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimed = await ClaimNextAsync();

        var submission = new SubmitInspectionReportRequest(
            Findings:
            [
                new SubmitFindingRequest(FindingCategory.FunctionalDefect, "Login button does nothing", "console: TypeError at login.js:42"),
            ],
            ToolCalls: []);
        var submit = await _client.PostAsJsonAsync($"/inspection-requests/{claimed!.Id}/report", submission);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // Query by claimed.ProjectId, not the locally-created `project`'s id: claim-next
        // claims the oldest eligible request across this whole (shared-fixture) test class,
        // not necessarily the one this test just created (see Post_WhileStillPending_Returns409,
        // which deliberately leaves an unclaimed Pending request behind) -- the only guarantee
        // is that whatever got claimed is what the finding gets indexed under.
        var query = await _client.PostAsJsonAsync(
            $"/projects/{claimed.ProjectId}/knowledge/query", new QueryKnowledgeRequest("login button", MaxResults: 5));
        var results = await query.Content.ReadFromJsonAsync<QueryKnowledgeResponse>(TestJsonOptions.Value);

        Assert.Contains(results!.Snippets, s => s.Content.Contains("Login button does nothing"));
    }

    [Fact]
    public async Task Post_WhenIndexingAFindingThrows_TheReportIsStillPersistedAndReturned()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IKnowledgeIndex>(new ThrowingKnowledgeIndex())));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MomosHostFactory.WorkerApiKey);

        var project = await (await client.PostAsJsonAsync(
                "/projects", new CreateProjectRequest("acme-indexing-failure-test", null, null, "purpose", "vision", "scope")))
            .Content.ReadFromJsonAsync<ProjectResponse>();
        await client.PostAsJsonAsync(
            $"/projects/{project!.Id}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claimResponse = await client.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 2, WorkerVersion: "0.1.0"));
        var claimed = (await claimResponse.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request!;

        var submission = new SubmitInspectionReportRequest(
            Findings:
            [
                new SubmitFindingRequest(FindingCategory.FunctionalDefect, "login button does nothing", "clicked 3x, no navigation"),
            ],
            ToolCalls: []);
        var response = await client.PostAsJsonAsync($"/inspection-requests/{claimed.Id}/report", submission);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<InspectionReportResponse>(TestJsonOptions.Value);
        Assert.Single(report!.Findings);
    }

    private sealed class ThrowingKnowledgeIndex : IKnowledgeIndex
    {
        public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated knowledge index failure");

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated knowledge index failure");
    }
}
