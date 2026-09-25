using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Contracts;
using Momos.Host.Endpoints;
using Momos.Worker.Execution;

namespace Momos.Worker.IntegrationTests.Execution;

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
        Assert.Equal(Momos.Worker.Execution.InspectionRequestKind.Inspection, result.Request.Kind);

        var project = await _client.GetProjectAsync(result.Request.ProjectId, CancellationToken.None);
        Assert.Equal(projectId, project.Id);
        Assert.Equal("acme", project.Name);

        await _client.SubmitReportAsync(
            result.Request.Id, [new FindingPayload(Category: 0, Description: "d", Evidence: "e")], [], CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{result.Request.Id}");
        var after = await afterResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Completed, after!.Status);
    }

    [Fact]
    public async Task SubmitReportAsync_WithToolCalls_PersistsThemOnHost()
    {
        var httpClient = _factory.CreateAuthorizedClient();
        var projectId = await CreateProjectAsync();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest("focus on login", null));
        var result = await _client.ClaimNextAsync(CancellationToken.None);

        await _client.SubmitReportAsync(
            result.Request!.Id,
            findings: [],
            toolCalls: [new ToolCallPayload("RunCommand", "ls -la", Success: true, DurationMs: 42)],
            CancellationToken.None);

        var report = await (await httpClient.GetAsync($"/inspection-requests/{result.Request.Id}/report"))
            .Content.ReadFromJsonAsync<Momos.Host.Contracts.InspectionReportResponse>();
        Assert.Single(report!.ToolCalls);
        Assert.Equal("RunCommand", report.ToolCalls[0].Tool);
        Assert.True(report.ToolCalls[0].Success);
        Assert.Equal(42, report.ToolCalls[0].DurationMs);
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

        await _client.SubmitFailureAsync(result.Request!.Id, "agent loop crashed", [], CancellationToken.None);

        var afterResponse = await httpClient.GetAsync($"/inspection-requests/{result.Request.Id}");
        var after = await afterResponse.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Failed, after!.Status);
        Assert.Equal("agent loop crashed", after.FailureReason);
    }

    [Fact]
    public async Task SubmitFailureAsync_WithToolCalls_PersistsThemOnHost()
    {
        var projectId = await CreateProjectAsync();
        var httpClient = _factory.CreateAuthorizedClient();
        await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var result = await _client.ClaimNextAsync(CancellationToken.None);

        await _client.SubmitFailureAsync(
            result.Request!.Id,
            "agent loop crashed",
            [new ToolCallPayload("RunCommand", "dotnet test", Success: true, DurationMs: 1200)],
            CancellationToken.None);

        // No InspectionReport exists for a failed request — the trace is verified by
        // reading straight from the database, not via the report endpoint (which 404s).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Momos.Host.Data.MomosDbContext>();
        var toolCalls = await db.ToolCalls
            .Where(t => t.InspectionRequestId == result.Request.Id)
            .ToListAsync();
        var toolCall = Assert.Single(toolCalls);
        Assert.Equal("RunCommand", toolCall.Tool);
        Assert.Equal("dotnet test", toolCall.Summary);
        Assert.True(toolCall.Success);
        Assert.Equal(1200, toolCall.DurationMs);
    }

    [Fact]
    public async Task ClaimNextAsync_SendsWorkerVersionInfo_AndHostEchoesNoUpdateRequired()
    {
        var result = await _client.ClaimNextAsync(CancellationToken.None);

        // Passing at all means the Host parsed the request body (ProtocolVersion =
        // WorkerVersionInfo.ProtocolVersion) and accepted it as a supported protocol.
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

    [Fact]
    public void ProtocolVersion_MatchesTheMinimumTheHostShipsWith()
    {
        // Each side's constant is bumped by hand on a wire change; this keeps a bump on one side
        // from shipping without the other.
        Assert.Equal(WorkerVersionInfo.ProtocolVersion, new WorkerCompatibilityOptions().MinSupportedProtocolVersion);
    }

    [Theory]
    [InlineData(typeof(Momos.Worker.Execution.InspectionRequestKind), typeof(Momos.Host.Domain.InspectionRequestKind))]
    [InlineData(typeof(Momos.Worker.Execution.ClaimTier), typeof(Momos.Host.Domain.ClaimTier))]
    [InlineData(typeof(Momos.Worker.Execution.ClaimConfidence), typeof(Momos.Host.Domain.ClaimConfidence))]
    [InlineData(typeof(Momos.Worker.Execution.EvidenceKind), typeof(Momos.Host.Domain.EvidenceKind))]
    [InlineData(typeof(Momos.Worker.Execution.IntentSource), typeof(Momos.Host.Domain.IntentSource))]
    public void MirroredEnums_HaveTheHostsMembersInTheHostsOrder(Type workerEnum, Type hostEnum)
    {
        // Enums travel by name, so a renamed or missing member would only fail at runtime on the
        // one value that uses it — compare the member lists instead.
        Assert.Equal(Enum.GetNames(hostEnum), Enum.GetNames(workerEnum));
    }

    [Fact]
    public async Task SubmitModelAsync_CompletesAClaimedAnalysisRequest()
    {
        while ((await _client.ClaimNextAsync(CancellationToken.None)).Request is not null)
        {
        }

        var httpClient = _factory.CreateAuthorizedClient();
        var project = await httpClient.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-model", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        await httpClient.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        var claimed = (await _client.ClaimNextAsync(CancellationToken.None)).Request!;
        Assert.Equal(Momos.Worker.Execution.InspectionRequestKind.Analysis, claimed.Kind);

        await _client.SubmitModelAsync(claimed.Id, new ProjectModelPayload(
            "abc123",
            [new ComponentPayload("cmp.app", "App", "executable", null, ["clm.app"])],
            [], [], [], [],
            [new ClaimPayload("clm.app", Momos.Worker.Execution.ClaimTier.Fact, "App is a .NET project",
                [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj")], Momos.Worker.Execution.ClaimConfidence.High)]),
            CancellationToken.None);

        var model = await httpClient.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal("abc123", model!.BaseCommit);
        var request = await httpClient.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{claimed.Id}", TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Completed, request!.Status);
    }

    [Fact]
    public async Task SubmitModelAsync_EveryFieldAndEnumValue_RoundTripsThroughTheHostUnchanged()
    {
        while ((await _client.ClaimNextAsync(CancellationToken.None)).Request is not null)
        {
        }

        var httpClient = _factory.CreateAuthorizedClient();
        var project = await httpClient.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-contract", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        await httpClient.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest("main"));
        var claimed = (await _client.ClaimNextAsync(CancellationToken.None)).Request!;
        Assert.Equal("main", claimed.CommitRef);

        var payload = new ProjectModelPayload(
            "0123456789abcdef0123456789abcdef01234567",
            [
                new ComponentPayload("cmp.app", "App", "executable", "Hosts the HTTP API", ["clm.app", "clm.app.refs"]),
                new ComponentPayload("cmp.lib", "Lib", "library", null, ["clm.lib"]),
            ],
            [new RelationPayload("cmp.app", "cmp.lib", "references", ["clm.app.refs"])],
            [new PatternPayload("pat.layers", "Layered", ["cmp.app", "cmp.lib"], ["clm.layers"])],
            [new DecisionPayload("dec.split", "Split the library out", ["Keep one project"], "unrecorded", ["clm.split"])],
            [new IntentPayload("int.api", "Serve an HTTP API", Momos.Worker.Execution.IntentSource.Document, ["clm.app"])],
            [
                new ClaimPayload("clm.app", Momos.Worker.Execution.ClaimTier.Fact, "App is an executable .NET project",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj", Symbol: "OutputType", Lines: "3-5")],
                    Momos.Worker.Execution.ClaimConfidence.High),
                new ClaimPayload("clm.app.refs", Momos.Worker.Execution.ClaimTier.Fact, "App references Lib",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj")],
                    Momos.Worker.Execution.ClaimConfidence.Medium),
                new ClaimPayload("clm.lib", Momos.Worker.Execution.ClaimTier.Fact, "Lib is a class library",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/Lib/Lib.csproj")],
                    Momos.Worker.Execution.ClaimConfidence.High),
                new ClaimPayload("clm.split", Momos.Worker.Execution.ClaimTier.History, "Lib was split out of App",
                    [
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Commit, Sha: "fedcba9876543210fedcba9876543210fedcba98"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.PullRequest, Url: "https://example.invalid/acme/pull/7"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Issue, Url: "https://example.invalid/acme/issues/3"),
                    ],
                    Momos.Worker.Execution.ClaimConfidence.Medium),
                new ClaimPayload("clm.layers", Momos.Worker.Execution.ClaimTier.Assessment, "App and Lib form two layers",
                    [
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Claim, ClaimKey: "clm.app.refs"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Finding, InspectionRequestId: Guid.NewGuid()),
                    ],
                    Momos.Worker.Execution.ClaimConfidence.Low),
            ]);

        await _client.SubmitModelAsync(claimed.Id, payload, CancellationToken.None);

        // Compare JSON rather than records: the point is that the Worker's serialized shape is
        // exactly what the Host stores and serves back, field for field and enum name for enum name.
        var sent = JsonSerializer.SerializeToNode(payload, TestJsonOptions.Value)!.AsObject();
        var served = JsonNode.Parse(await httpClient.GetStringAsync($"/projects/{projectId}/model"))!.AsObject();
        Assert.Equal(sent["baseCommit"]!.GetValue<string>(), served["baseCommit"]!.GetValue<string>());
        foreach (var collection in new[] { "components", "relations", "patterns", "decisions", "intents" })
        {
            Assert.True(JsonNode.DeepEquals(sent[collection], served[collection]),
                $"{collection}: sent {sent[collection]!.ToJsonString()} but served {served[collection]!.ToJsonString()}");
        }

        // The Host serves claims ordered by key, with the correction fields added on top.
        var sentClaims = sent["claims"]!.AsArray().OrderBy(c => c!["key"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
        var servedClaims = served["claims"]!.AsArray().ToList();
        Assert.Equal(sentClaims.Count, servedClaims.Count);
        for (var i = 0; i < sentClaims.Count; i++)
        {
            var servedClaim = servedClaims[i]!.AsObject();
            Assert.Equal("Proposed", servedClaim["status"]!.GetValue<string>());
            var servedSubmittedPart = new JsonObject();
            foreach (var property in sentClaims[i]!.AsObject())
            {
                servedSubmittedPart[property.Key] = servedClaim[property.Key]?.DeepClone();
            }

            Assert.True(JsonNode.DeepEquals(sentClaims[i], servedSubmittedPart),
                $"claim: sent {sentClaims[i]!.ToJsonString()} but served {servedClaim.ToJsonString()}");
        }
    }
}
