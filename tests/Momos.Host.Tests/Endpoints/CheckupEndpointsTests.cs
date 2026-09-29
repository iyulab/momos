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
}
