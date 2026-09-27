using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class ProjectModelEndpointsTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private static readonly ClaimNextRequest ClaimNextAsCurrentWorker = new(ProtocolVersion: 3, WorkerVersion: "0.1.0");
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    /// <summary>Creates a project + analysis request and claims it, leaving it Running. Drains
    /// the shared queue first so the claim returns this request.</summary>
    internal static async Task<(Guid ProjectId, Guid RequestId)> StartAnalysisAsync(HttpClient client, Guid? projectId = null)
    {
        while ((await (await client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        if (projectId is null)
        {
            var project = await client.PostAsJsonAsync("/projects",
                new CreateProjectRequest("acme-model", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
            projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        }

        await client.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        var claim = await (await client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        return (projectId.Value, claim!.Request!.Id);
    }

    internal static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid requestId, SubmitProjectModelRequest model) =>
        await client.PostAsJsonAsync($"/analysis-requests/{requestId}/model", model, TestJsonOptions.Value);

    [Fact]
    public async Task Submit_AValidModel_StoresVersion1AndCompletesTheRequest()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);

        var response = await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var model = await response.Content.ReadFromJsonAsync<ProjectModelResponse>(TestJsonOptions.Value);
        Assert.Equal(1, model!.ModelVersion);
        Assert.Equal("abc123", model.BaseCommit);
        Assert.All(model.Claims, c => Assert.Equal(ClaimStatus.Proposed, c.Status));
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Completed, request!.Status);

        var latest = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(model.ModelVersion, latest!.ModelVersion);
        Assert.Equal(ModelDecision.Unrecorded, Assert.Single(latest.Decisions).Rationale);
    }

    [Fact]
    public async Task Submit_AClaimWithNoEvidence_Returns400AndLeavesTheRequestRunning()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);
        var invalid = ModelFixtures.ValidSubmission() with
        {
            Claims = [.. ModelFixtures.ValidSubmission().Claims, new SubmittedClaim("clm.bare", ClaimTier.Assessment, "looks fine", [], ClaimConfidence.Low, ClaimOrigin.Deterministic)],
        };

        var response = await SubmitAsync(_client, requestId, invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Running, request!.Status);
    }

    [Fact]
    public async Task Submit_WithAnOmittedList_Returns400AndLeavesTheRequestRunning()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);

        // A body that binds with Claims left null — a malformed submission, not a server fault.
        var body = ModelFixtures.AnonymousBody(claims: []);
        body.Remove("claims");
        var response = await _client.PostAsJsonAsync($"/analysis-requests/{requestId}/model", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var request = await _client.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
        Assert.Equal(InspectionRequestStatus.Running, request!.Status);
    }

    [Fact]
    public async Task Submit_AClaimWithoutAnOrigin_Returns400NamingIt()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);

        // A body whose claim omits origin: a submitter that doesn't say how it produced a claim
        // must not be taken as a deterministic extractor by default.
        var response = await _client.PostAsJsonAsync($"/analysis-requests/{requestId}/model",
            ModelFixtures.AnonymousBody(claims:
            [
                new
                {
                    key = "clm.a",
                    tier = "Fact",
                    statement = "A is a project",
                    evidence = new[] { new { kind = "Code", path = "src/A/A.csproj" } },
                    confidence = "High",
                },
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The problem body is JSON, which escapes the quotes around the key — assert on the parts.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("clm.a", body);
        Assert.Contains(".Origin", body);
        // Origin is a scalar field, not a list — the "send an empty list" hint only makes sense
        // for the list fields this same helper also names.
        Assert.DoesNotContain("empty list", body);
    }

    [Theory]
    [InlineData("tier", ".Tier")]
    [InlineData("confidence", ".Confidence")]
    public async Task Submit_AClaimOmittingAScalarGrade_Returns400NamingIt(string omitted, string named)
    {
        var (_, requestId) = await StartAnalysisAsync(_client);

        // Both enums have a first member (Fact, High) that an omitted value would silently bind to —
        // the most authoritative grade. An omission has to be named, not defaulted.
        var claim = new Dictionary<string, object>
        {
            ["key"] = "clm.a",
            ["tier"] = "Fact",
            ["statement"] = "A is a project",
            ["evidence"] = new[] { new { kind = "Code", path = "src/A/A.csproj" } },
            ["confidence"] = "High",
            ["origin"] = "Deterministic",
        };
        claim.Remove(omitted);

        var response = await _client.PostAsJsonAsync($"/analysis-requests/{requestId}/model",
            ModelFixtures.AnonymousBody(claims: [claim]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("clm.a", body);
        Assert.Contains(named, body);
        Assert.DoesNotContain("empty list", body);
    }

    [Fact]
    public async Task Submit_AClaimWithAnUnknownOrigin_Returns400NamingIt()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);

        // A body whose claim's origin is a value the enum never named — must not silently bind
        // and be stored as though it were one of the known origins.
        var response = await _client.PostAsJsonAsync($"/analysis-requests/{requestId}/model",
            ModelFixtures.AnonymousBody(claims:
            [
                new
                {
                    key = "clm.a",
                    tier = "Fact",
                    statement = "A is a project",
                    evidence = new[] { new { kind = "Code", path = "src/A/A.csproj" } },
                    confidence = "High",
                    origin = 7,
                },
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("clm.a", body);
        Assert.Contains("unknown origin", body);
    }

    [Fact]
    public async Task Submit_KeepsEachClaimsOrigin()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        var submission = ModelFixtures.ValidSubmission() with
        {
            Claims =
            [
                .. ModelFixtures.ValidSubmission().Claims,
                new SubmittedClaim("clm.why", ClaimTier.Assessment, "App and Lib look deliberately layered",
                    [new ClaimEvidenceDto(EvidenceKind.Claim, ClaimKey: "clm.ref")], ClaimConfidence.Low, ClaimOrigin.Synthesized),
            ],
        };

        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(_client, requestId, submission)).StatusCode);

        var model = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(ClaimOrigin.Synthesized, Assert.Single(model!.Claims, c => c.Key == "clm.why").Origin);
        Assert.Equal(ClaimOrigin.Deterministic, Assert.Single(model.Claims, c => c.Key == "clm.app").Origin);
    }

    [Fact]
    public async Task Submit_ForAnInspectionKindRequest_Returns409()
    {
        while ((await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-mismatch", "https://example.invalid/acme.git", null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
        await _client.PostAsJsonAsync($"/projects/{projectId}/inspection-requests", new CreateInspectionRequestRequest(null, null));
        var claim = await (await _client.PostAsJsonAsync("/inspection-requests/claim-next", ClaimNextAsCurrentWorker))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        var response = await SubmitAsync(_client, claim!.Request!.Id, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Submit_Twice_ForTheSameRequest_Returns409TheSecondTime()
    {
        var (_, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var second = await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid projectId, string claimKey, CorrectClaimRequest verdict) =>
        client.PostAsJsonAsync($"/projects/{projectId}/model/claims/{Uri.EscapeDataString(claimKey)}/corrections", verdict, TestJsonOptions.Value);

    [Fact]
    public async Task Reanalysis_CarriesACorrectionForward_OnlyWhileTheStatementIsUnchanged()
    {
        var (projectId, first) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, first, ModelFixtures.ValidSubmission());
        var corrected = await CorrectAsync(_client, projectId, "clm.ref",
            new CorrectClaimRequest(ClaimStatus.Corrected, "Lib is a plugin loaded at run time, not a compile-time dependency"));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CorrectAsync(_client, projectId, "clm.app", new CorrectClaimRequest(ClaimStatus.Confirmed, null))).StatusCode);
        // Read back from storage, so both sides of the comparison below carry the stored precision.
        var correctedAt = (await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value))!
            .Claims.Single(c => c.Key == "clm.ref").CorrectedAt;
        Assert.NotNull(correctedAt);

        var (_, second) = await StartAnalysisAsync(_client, projectId);
        var response = await SubmitAsync(_client, second, ModelFixtures.ValidSubmission(baseCommit: "def456", componentStatement: "App is a .NET web service"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var model = await response.Content.ReadFromJsonAsync<ProjectModelResponse>(TestJsonOptions.Value);
        Assert.Equal(2, model!.ModelVersion);
        var carried = model.Claims.Single(c => c.Key == "clm.ref");
        Assert.Equal(ClaimStatus.Corrected, carried.Status);
        Assert.Equal("Lib is a plugin loaded at run time, not a compile-time dependency", carried.Correction);
        Assert.Equal(correctedAt, carried.CorrectedAt);
        // The statement behind clm.app changed, so the old confirmation no longer describes it.
        var restarted = model.Claims.Single(c => c.Key == "clm.app");
        Assert.Equal(ClaimStatus.Proposed, restarted.Status);
        Assert.Null(restarted.CorrectedAt);
        Assert.Equal(ClaimStatus.Proposed, model.Claims.Single(c => c.Key == "clm.lib").Status);

        // The stored latest model agrees with the submission response.
        var latest = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(ClaimStatus.Corrected, latest!.Claims.Single(c => c.Key == "clm.ref").Status);
    }

    [Fact]
    public async Task Correct_AClaim_KeepsTheOriginalStatementAndRecordsTheCorrection()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var response = await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(ClaimStatus.Corrected, "  Lib is loaded as a plugin  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ClaimResponse>(TestJsonOptions.Value);
        Assert.Equal("clm.ref", claim!.Key);
        Assert.Equal("App references Lib", claim.Statement);
        Assert.Equal(ClaimStatus.Corrected, claim.Status);
        Assert.Equal("Lib is loaded as a plugin", claim.Correction);
        Assert.NotNull(claim.CorrectedAt);

        var latest = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        var stored = latest!.Claims.Single(c => c.Key == "clm.ref");
        Assert.Equal(ClaimStatus.Corrected, stored.Status);
        Assert.Equal("Lib is loaded as a plugin", stored.Correction);
        Assert.Equal("App references Lib", stored.Statement);
    }

    [Fact]
    public async Task Confirm_AfterACorrection_ClearsTheCorrection()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());
        await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(ClaimStatus.Corrected, "Lib is loaded as a plugin"));

        var response = await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(ClaimStatus.Confirmed, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ClaimResponse>(TestJsonOptions.Value);
        Assert.Equal(ClaimStatus.Confirmed, claim!.Status);
        Assert.Null(claim.Correction);
    }

    [Theory]
    [InlineData(ClaimStatus.Corrected, null)]
    [InlineData(ClaimStatus.Corrected, "   ")]
    [InlineData(ClaimStatus.Proposed, null)]
    public async Task Correct_WithAnInvalidVerdict_Returns400(ClaimStatus status, string? correction)
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var response = await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(status, correction));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var latest = await _client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(ClaimStatus.Proposed, latest!.Claims.Single(c => c.Key == "clm.ref").Status);
    }

    [Fact]
    public async Task Correct_AnUnknownClaim_Returns404()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var response = await CorrectAsync(_client, projectId, "clm.nope", new CorrectClaimRequest(ClaimStatus.Confirmed, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Correct_AClaimKeyThatDiffersOnlyInCase_Returns404()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var response = await CorrectAsync(_client, projectId, "CLM.REF", new CorrectClaimRequest(ClaimStatus.Confirmed, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Correct_OnAProjectWithNoModel_Returns404()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-uncorrected", null, null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;

        var response = await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(ClaimStatus.Confirmed, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReport_ReturnsTheDocumentTree()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());

        var report = await _client.GetFromJsonAsync<ProjectModelReportResponse>($"/projects/{projectId}/model/report", TestJsonOptions.Value);

        Assert.Equal(1, report!.ModelVersion);
        Assert.Equal("abc123", report.BaseCommit);
        var index = Assert.Single(report.Documents, d => d.Path == "index.md").Content;
        Assert.Contains("# acme-model", index);
        Assert.Contains("```mermaid", index);
        Assert.Contains(report.Documents, d => d.Path == "claims/clm.ref.md");
    }

    [Fact]
    public async Task GetReport_AfterACorrection_ShowsTheVerdictBesideTheOriginalClaim()
    {
        var (projectId, requestId) = await StartAnalysisAsync(_client);
        await SubmitAsync(_client, requestId, ModelFixtures.ValidSubmission());
        await CorrectAsync(_client, projectId, "clm.ref", new CorrectClaimRequest(ClaimStatus.Corrected, "Lib is loaded as a plugin"));

        var report = await _client.GetFromJsonAsync<ProjectModelReportResponse>($"/projects/{projectId}/model/report", TestJsonOptions.Value);

        var claim = Assert.Single(report!.Documents, d => d.Path == "claims/clm.ref.md").Content;
        Assert.Contains("App references Lib", claim);
        Assert.Contains("> Lib is loaded as a plugin", claim);
        Assert.Contains("Corrected", claim);
        Assert.Contains("| Corrected |", Assert.Single(report.Documents, d => d.Path == "index.md").Content);
    }

    [Fact]
    public async Task GetReport_ForAProjectNeverAnalyzed_Returns404()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-unreported", null, null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;

        var response = await _client.GetAsync($"/projects/{projectId}/model/report");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReport_ForAnUnknownProject_Returns404()
    {
        var response = await _client.GetAsync($"/projects/{Guid.NewGuid()}/model/report");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetModel_ForAProjectNeverAnalyzed_Returns404()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-none", null, null, "purpose", "vision", "scope"));
        var projectId = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;

        var response = await _client.GetAsync($"/projects/{projectId}/model");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
