using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Endpoints;

public sealed class CheckupEndpointsTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private async Task<Guid> ProjectAsync(HttpClient client, string? reportLanguage = null)
    {
        var r = await client.PostAsJsonAsync("/projects", new { name = "acme", repositoryUrl = "https://example.invalid/acme.git", purpose = "p", vision = "v", scope = "s", reportLanguage });
        return (await r.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Value))!.Id;
    }

    [Fact]
    public async Task ACheckup_QueuesOneDesignAnalysisRequest_InItsLanguage()
    {
        var client = factory.CreateAuthorizedClient();
        var projectId = await ProjectAsync(client, reportLanguage: "ko");

        var created = await client.PostAsJsonAsync($"/projects/{projectId}/checkups", new { commitRef = "abc1234" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var checkup = (await created.Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value))!;
        Assert.Equal("ko", checkup.Language);
        Assert.Equal(CheckupStatus.Running, checkup.Status);
        var exam = Assert.Single(checkup.Exams);
        Assert.Equal(ExamProgram.DesignAnalysis, exam.Program);
        Assert.Equal(ExamRunStatus.Pending, exam.Status);

        await using var db = factory.CreateDbContext();
        var request = await db.InspectionRequests.SingleAsync(r => r.Id == exam.RequestId);
        Assert.Equal(InspectionRequestKind.Analysis, request.Kind);
        Assert.Equal("abc1234", request.CommitRef);
        Assert.Equal("ko", request.Language);
    }

    [Fact]
    public async Task TheRequestLanguage_OverridesTheProject_AndTheHostDefaultFillsIn()
    {
        var client = factory.CreateAuthorizedClient();
        var named = await ProjectAsync(client, reportLanguage: "ko");
        var unnamed = await ProjectAsync(client);

        var overridden = await (await client.PostAsJsonAsync($"/projects/{named}/checkups", new { language = "en" })).Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value);
        var defaulted = await (await client.PostAsJsonAsync($"/projects/{unnamed}/checkups", new { })).Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value);

        Assert.Equal("en", overridden!.Language);
        Assert.Equal("en", defaulted!.Language); // the test Host keeps the code default
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("korean")]
    public async Task AnUnsupportedLanguage_IsRefused(string language)
    {
        var client = factory.CreateAuthorizedClient();
        var projectId = await ProjectAsync(client);

        var r = await client.PostAsJsonAsync($"/projects/{projectId}/checkups", new { language });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ACheckupNeedsARepository_AndAnExistingProject()
    {
        var client = factory.CreateAuthorizedClient();
        var noRepo = (await (await client.PostAsJsonAsync("/projects", new { name = "x", purpose = "p", vision = "v", scope = "s" })).Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Value))!.Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/projects/{noRepo}/checkups", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/projects/{Guid.NewGuid()}/checkups", new { })).StatusCode);
    }

    [Fact]
    public async Task CheckupsAreReadBackOneByOne_AndListedNewestFirst()
    {
        var client = factory.CreateAuthorizedClient();
        var projectId = await ProjectAsync(client);
        var first = (await (await client.PostAsJsonAsync($"/projects/{projectId}/checkups", new { })).Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value))!;
        var second = (await (await client.PostAsJsonAsync($"/projects/{projectId}/checkups", new { })).Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value))!;

        Assert.Equal(first.Id, (await client.GetFromJsonAsync<CheckupResponse>($"/checkups/{first.Id}", TestJsonOptions.Value))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/checkups/{Guid.NewGuid()}")).StatusCode);
        var list = await client.GetFromJsonAsync<List<CheckupResponse>>($"/projects/{projectId}/checkups", TestJsonOptions.Value);
        Assert.Equal([second.Id, first.Id], list!.Select(c => c.Id));
    }

    private async Task<(HttpClient Client, HttpClient Worker, CheckupResponse Checkup)> StartCheckupAsync()
    {
        var client = factory.CreateAuthorizedClient();
        var worker = factory.CreateAuthorizedClient();
        var claimNext = new ClaimNextRequest(ProtocolVersion: TestProtocol.Current, WorkerVersion: "0.1.0");
        while ((await (await worker.PostAsJsonAsync("/inspection-requests/claim-next", claimNext))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value))!.Request is not null)
        {
        }

        var projectId = await ProjectAsync(client);
        var checkup = (await (await client.PostAsJsonAsync($"/projects/{projectId}/checkups", new { })).Content.ReadFromJsonAsync<CheckupResponse>(TestJsonOptions.Value))!;
        var claim = await (await worker.PostAsJsonAsync("/inspection-requests/claim-next", claimNext))
            .Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        Assert.Equal(checkup.Exams[0].RequestId, claim!.Request!.Id);
        return (client, worker, checkup);
    }

    [Fact]
    public async Task SubmittingTheModel_EndsTheExam_AndTheCheckup()
    {
        var (client, worker, checkup) = await StartCheckupAsync();

        var submitted = await worker.PostAsJsonAsync($"/analysis-requests/{checkup.Exams[0].RequestId}/model", ModelFixtures.ValidSubmission(), TestJsonOptions.Value);
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        var after = await client.GetFromJsonAsync<CheckupResponse>($"/checkups/{checkup.Id}", TestJsonOptions.Value);
        Assert.Equal(CheckupStatus.Completed, after!.Status);
        Assert.NotNull(after.CompletedAt);
        Assert.NotNull(after.ModelVersion);
        Assert.NotNull(after.BaseCommit);
        Assert.Contains(after.Exams[0].Status, new[] { ExamRunStatus.Completed, ExamRunStatus.Partial });
        Assert.Equal(after.ModelVersion, after.Exams[0].ModelVersion);

        // A second submission for the same request is refused and changes nothing.
        var again = await worker.PostAsJsonAsync($"/analysis-requests/{checkup.Exams[0].RequestId}/model", ModelFixtures.ValidSubmission(), TestJsonOptions.Value);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(after.CompletedAt, (await client.GetFromJsonAsync<CheckupResponse>($"/checkups/{checkup.Id}", TestJsonOptions.Value))!.CompletedAt);
    }

    [Fact]
    public async Task AFailedDesignAnalysis_EndsTheCheckupWithTheExamNotRun_AndItsReason()
    {
        var (client, worker, checkup) = await StartCheckupAsync();

        var failed = await worker.PostAsJsonAsync($"/inspection-requests/{checkup.Exams[0].RequestId}/fail", new { reason = "clone failed", toolCalls = Array.Empty<object>() }, TestJsonOptions.Value);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        var after = await client.GetFromJsonAsync<CheckupResponse>($"/checkups/{checkup.Id}", TestJsonOptions.Value);
        Assert.Equal(CheckupStatus.Completed, after!.Status);
        Assert.Equal(ExamRunStatus.NotRun, after.Exams[0].Status);
        Assert.Equal("clone failed", after.Exams[0].Reason);
    }

    [Fact]
    public async Task AnAnalysisOutsideACheckup_StillCompletesAsBefore()
    {
        // Regression guard: the progress hook must ignore requests no exam run owns.
        var client = factory.CreateAuthorizedClient();
        var (_, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(client);

        var submitted = await ProjectModelEndpointsTests.SubmitAsync(client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ExamRuns.AnyAsync(e => e.RequestId == requestId));
    }

    [Fact]
    public async Task TheReport_WaitsForTheCheckupToEnd_ThenStartsAtItsIndex()
    {
        var (client, worker, checkup) = await StartCheckupAsync();

        var running = await client.GetAsync($"/checkups/{checkup.Id}/report");
        Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);
        Assert.Contains("The checkup is still running.", await running.Content.ReadAsStringAsync());

        await worker.PostAsJsonAsync($"/analysis-requests/{checkup.Exams[0].RequestId}/model", ModelFixtures.ValidSubmission(), TestJsonOptions.Value);
        var report = await client.GetFromJsonAsync<CheckupReportResponse>($"/checkups/{checkup.Id}/report", TestJsonOptions.Value);

        Assert.Equal(checkup.Id, report!.CheckupId);
        Assert.Equal("en", report.Language);
        Assert.Equal("index.md", report.Documents[0].Path);
        Assert.Contains(report.Documents, d => d.Path.StartsWith("evidence/claims/", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/checkups/{Guid.NewGuid()}/report")).StatusCode);
    }

    [Fact]
    public async Task ACheckupWhoseAnalysisNeverRan_StillHasAReport_ThatSaysWhy()
    {
        var (client, worker, checkup) = await StartCheckupAsync();
        await worker.PostAsJsonAsync($"/inspection-requests/{checkup.Exams[0].RequestId}/fail", new { reason = "clone failed", toolCalls = Array.Empty<object>() }, TestJsonOptions.Value);

        var report = await client.GetFromJsonAsync<CheckupReportResponse>($"/checkups/{checkup.Id}/report", TestJsonOptions.Value);

        Assert.Contains("clone failed", report!.Documents.Single(d => d.Path == "manual/index.md").Content);
    }
}
