using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Contracts;
using Momos.Host.Endpoints;
using Momos.Worker.Analysis;
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
    [InlineData(typeof(Momos.Worker.Execution.ClaimOrigin), typeof(Momos.Host.Domain.ClaimOrigin))]
    [InlineData(typeof(Momos.Worker.Execution.OutlineBlockKind), typeof(Momos.Host.Domain.OutlineBlockKind))]
    public void MirroredEnums_HaveTheHostsMembersInTheHostsOrder(Type workerEnum, Type hostEnum)
    {
        // Enums travel by name, so a renamed or missing member would only fail at runtime on the
        // one value that uses it — compare the member lists instead.
        Assert.Equal(Enum.GetNames(hostEnum), Enum.GetNames(workerEnum));
    }

    [Fact]
    public async Task ClaimNextAsync_CarriesACheckupsLanguageToTheWorker()
    {
        while ((await _client.ClaimNextAsync(CancellationToken.None)).Request is not null)
        {
        }

        var httpClient = _factory.CreateAuthorizedClient();
        var project = await httpClient.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-checkup", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        var created = await httpClient.PostAsJsonAsync($"/projects/{projectId}/checkups", new { language = "ko" });
        created.EnsureSuccessStatusCode();

        var claimed = (await _client.ClaimNextAsync(CancellationToken.None)).Request!;

        Assert.Equal(Momos.Worker.Execution.InspectionRequestKind.Analysis, claimed.Kind);
        Assert.Equal("ko", claimed.Language);
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
            [], [], [], [], [], [], [], new CoveragePayload([], [], [], null),
            [new ClaimPayload("clm.app", Momos.Worker.Execution.ClaimTier.Fact, "App is a .NET project",
                [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj")], Momos.Worker.Execution.ClaimConfidence.High,
                Momos.Worker.Execution.ClaimOrigin.Deterministic)]),
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
            [new FlowPayload("flw.build", "Build", [new FlowStepPayload(null, "clm.lib"), new FlowStepPayload("cmp.app", "clm.app")], ["clm.app.refs"])],
            [new InvariantPayload("inv.layers", "Lib never references App", "contract", ["cmp.lib"], ["clm.app.refs"])],
            [
                new OutlineSectionPayload("sec.map", "system-map.md", "System map", "What the parts are", ["clm.app"],
                [
                    // Every block kind once, each pointing at an element of its kind in this payload.
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Claim, "clm.app"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Component, "cmp.app"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Pattern, "pat.layers"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Decision, "dec.split"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Intent, "int.api"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Flow, "flw.build"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Invariant, "inv.layers"),
                    new OutlineBlockPayload(Momos.Worker.Execution.OutlineBlockKind.Map, "*"),
                ]),
                new OutlineSectionPayload("sec.risks", "risks.md", "Risks", "", [], []),
            ],
            new CoveragePayload(
                [new CoverageAreaPayload("project-manifests", "2 of 2 project files")],
                [new CoverageGapPayload("source-files", "not read")],
                [new CoverageRejectionPayload("evidence does not locate", 2)],
                new CoverageGeneratorPayload("model-x", "p-1")),
            [
                new ClaimPayload("clm.app", Momos.Worker.Execution.ClaimTier.Fact, "App is an executable .NET project",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj", Symbol: "OutputType", Lines: "3-5")],
                    Momos.Worker.Execution.ClaimConfidence.High, Momos.Worker.Execution.ClaimOrigin.Deterministic),
                new ClaimPayload("clm.app.refs", Momos.Worker.Execution.ClaimTier.Fact, "App references Lib",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/App/App.csproj")],
                    Momos.Worker.Execution.ClaimConfidence.Medium, Momos.Worker.Execution.ClaimOrigin.Deterministic),
                new ClaimPayload("clm.lib", Momos.Worker.Execution.ClaimTier.Fact, "Lib is a class library",
                    [new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Code, Path: "src/Lib/Lib.csproj")],
                    Momos.Worker.Execution.ClaimConfidence.High, Momos.Worker.Execution.ClaimOrigin.Deterministic),
                new ClaimPayload("clm.split", Momos.Worker.Execution.ClaimTier.History, "Lib was split out of App",
                    [
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Commit, Sha: "fedcba9876543210fedcba9876543210fedcba98"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.PullRequest, Url: "https://example.invalid/acme/pull/7"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Issue, Url: "https://example.invalid/acme/issues/3"),
                    ],
                    Momos.Worker.Execution.ClaimConfidence.Medium, Momos.Worker.Execution.ClaimOrigin.Deterministic),
                new ClaimPayload("clm.layers", Momos.Worker.Execution.ClaimTier.Assessment, "App and Lib form two layers",
                    [
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Claim, ClaimKey: "clm.app.refs"),
                        new EvidencePayload(Momos.Worker.Execution.EvidenceKind.Finding, InspectionRequestId: Guid.NewGuid()),
                    ],
                    Momos.Worker.Execution.ClaimConfidence.Low, Momos.Worker.Execution.ClaimOrigin.Deterministic),
            ]);

        await _client.SubmitModelAsync(claimed.Id, payload, CancellationToken.None);

        // Compare JSON rather than records: the point is that the Worker's serialized shape is
        // exactly what the Host stores and serves back, field for field and enum name for enum name.
        var sent = JsonSerializer.SerializeToNode(payload, TestJsonOptions.Value)!.AsObject();
        var served = JsonNode.Parse(await httpClient.GetStringAsync($"/projects/{projectId}/model"))!.AsObject();
        Assert.Equal(sent["baseCommit"]!.GetValue<string>(), served["baseCommit"]!.GetValue<string>());
        foreach (var collection in new[] { "components", "relations", "patterns", "decisions", "intents", "flows", "invariants", "outline", "coverage" })
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

    private async Task<(Guid ProjectId, Guid AnalysisRequestId)> ClaimAnalysisAsync(string projectName)
    {
        while ((await _client.ClaimNextAsync(CancellationToken.None)).Request is not null)
        {
        }

        var httpClient = _factory.CreateAuthorizedClient();
        var project = await httpClient.PostAsJsonAsync("/projects",
            new CreateProjectRequest(projectName, "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        await httpClient.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        var claimed = (await _client.ClaimNextAsync(CancellationToken.None)).Request!;
        Assert.Equal(Momos.Worker.Execution.InspectionRequestKind.Analysis, claimed.Kind);
        return (projectId, claimed.Id);
    }

    [Fact]
    public async Task ExtractedModel_OfAMultiProjectRepository_IsAcceptedByTheHost()
    {
        var (projectId, analysisRequestId) = await ClaimAnalysisAsync("acme-extracted");
        var repository = new ScriptedRepositoryRuntime(new Dictionary<string, string>
        {
            ["src/App/App.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\..\..\Other\Other.csproj" />
                    <ProjectReference Include="$(RepoRoot)\eng\Build.csproj" />
                    <ProjectReference Include="..\Broken\Broken.csproj" />
                  </ItemGroup>
                </Project>
                """,
            ["src/Lib/Lib.csproj"] = """<Project Sdk="Microsoft.NET.Sdk" />""",
            ["src/Api/Api.csproj"] = """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><ProjectReference Include="..\lib\lib.csproj" /></ItemGroup></Project>""",
            ["tests/App.Tests/App.Tests.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="..\..\src\App\App.csproj" /></ItemGroup></Project>""",
            ["src/Broken/Broken.csproj"] = "<Project",
        });

        var extracted = await new ProjectModelExtractor(repository).ExtractAsync(new ExecutionSessionHandle("s"), CancellationToken.None);
        // Throws on any non-success status, so this line is the Host's validator accepting the model.
        await _client.SubmitModelAsync(analysisRequestId, extracted, CancellationToken.None);

        var httpClient = _factory.CreateAuthorizedClient();
        var model = await httpClient.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(repository.Head, model!.BaseCommit);
        Assert.Equal(["Api", "App", "App.Tests", "Lib"], model.Components.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3, model.Relations.Count);
        Assert.Equal(extracted.Claims.Count, model.Claims.Count);
        Assert.Equal(8, model.Claims.Count(c => c.Tier == Momos.Host.Domain.ClaimTier.History));

        // The history reaches the report: the component's page links its history claim, whose page cites the commits.
        var report = await httpClient.GetFromJsonAsync<ProjectModelReportResponse>($"/projects/{projectId}/model/report", TestJsonOptions.Value);
        var historyKey = ModelIds.Claim("history|src/Lib/Lib.csproj");
        Assert.Contains($"claims/{historyKey}.md", Assert.Single(report!.Documents, d => d.Path == $"components/{ModelIds.Component("src/Lib/Lib.csproj")}.md").Content);
        var claimPage = Assert.Single(report.Documents, d => d.Path == $"claims/{historyKey}.md").Content;
        Assert.Contains("| History | High | Deterministic | Proposed |", claimPage);
        Assert.Contains($"commit `{repository.Head}`", claimPage);
        var request = await httpClient.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{analysisRequestId}", TestJsonOptions.Value);
        Assert.Equal(Momos.Host.Domain.InspectionRequestStatus.Completed, request!.Status);
    }

    [Fact]
    public async Task ExtractedModel_OfARepositoryWithNoProjects_IsAcceptedByTheHostAsAnEmptyModel()
    {
        var (projectId, analysisRequestId) = await ClaimAnalysisAsync("acme-empty");
        var repository = new ScriptedRepositoryRuntime(new Dictionary<string, string>());

        var extracted = await new ProjectModelExtractor(repository).ExtractAsync(new ExecutionSessionHandle("s"), CancellationToken.None);
        await _client.SubmitModelAsync(analysisRequestId, extracted, CancellationToken.None);

        var model = await _factory.CreateAuthorizedClient()
            .GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Empty(model!.Components);
        Assert.Empty(model.Claims);
    }

    /// <summary>Answers the extractor's git commands from an in-memory file set, as a checked-out
    /// repository would.</summary>
    private sealed class ScriptedRepositoryRuntime(IReadOnlyDictionary<string, string> files) : IExecutionRuntimeProvider
    {
        public string Head { get; } = "0123456789abcdef0123456789abcdef01234567";

        public Task<ExecutionSessionHandle> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionSessionHandle("s"));

        public Task<ExecutionCommandResult> ExecuteAsync(ExecutionSessionHandle session, ExecutionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExecutionCommandResult>((command.Name, command.Args) switch
            {
                ("git", ["rev-parse", "HEAD"]) => new(true, Head + "\n", null, 1),
                ("git", ["ls-files", "-z", "--", "*.csproj"]) => new(true, string.Concat(files.Keys.Select(k => k + "\0")), null, 1),
                ("git", ["show", var spec]) when spec.StartsWith("HEAD:", StringComparison.Ordinal) && files.TryGetValue(spec[5..], out var content)
                    => new(true, content, null, 1),

                // Every component and every name has the same two-commit history.
                ("git", ["rev-list", "--count", ..]) => new(true, "2\n", null, 1),
                ("git", ["log", ..]) => new(true, $"{Head}\u001f2026-09-20\u001fSecond\0{new string('f', 40)}\u001f2026-09-19\u001fFirst\0", null, 1),
                _ => new(false, null, $"unexpected command: {command.Name} {string.Join(' ', command.Args)}", 1),
            });

        public Task CloseSessionAsync(ExecutionSessionHandle session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
