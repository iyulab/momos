using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Momos.Host.Contracts;
using Momos.Host.Domain;
using Momos.Host.Knowledge;
using Momos.Host.Tests.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace Momos.Host.Tests.Endpoints;

public sealed class ProjectModelKnowledgeTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client = factory.CreateAuthorizedClient();

    private async Task<IReadOnlyList<KnowledgeSnippet>> QueryAsync(Guid projectId, string query) =>
        (await (await _client.PostAsJsonAsync($"/projects/{projectId}/knowledge/query", new QueryKnowledgeRequest(query, MaxResults: 10)))
            .Content.ReadFromJsonAsync<QueryKnowledgeResponse>())!.Snippets;

    private async Task SubmitCreatedAsync(Guid projectId, Guid requestId, SubmitProjectModelRequest model, int version)
    {
        var response = await ProjectModelEndpointsTests.SubmitAsync(_client, requestId, model);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await ModelProjectionServiceTests.WaitIndexedAsync(_client, projectId, version);
    }

    [Fact]
    public async Task SubmittedClaims_AreSearchableThroughTheProjectKnowledgeQuery()
    {
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client);
        await SubmitCreatedAsync(projectId, requestId, ModelFixtures.ValidSubmission(), version: 1);

        var snippets = await QueryAsync(projectId, "App references Lib");

        Assert.Contains(snippets, s => s.Content.Contains("App references Lib") && s.Content.Contains("src/App/App.csproj"));
    }

    [Fact]
    public async Task AClaimDroppedByReanalysis_IsRemovedFromTheIndex()
    {
        var (projectId, first) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client);
        var withExtra = ModelFixtures.ValidSubmission() with
        {
            Components = [.. ModelFixtures.ValidSubmission().Components, new ModelComponentDto("cmp.legacy", "Zanzibarlegacy", "library", null, ["clm.legacy"])],
            Claims = [.. ModelFixtures.ValidSubmission().Claims, new SubmittedClaim("clm.legacy", ClaimTier.Fact, "Zanzibarlegacy is a .NET project",
                [new ClaimEvidenceDto(EvidenceKind.Code, Path: "src/Legacy/Legacy.csproj")], ClaimConfidence.High, ClaimOrigin.Deterministic)],
        };
        await SubmitCreatedAsync(projectId, first, withExtra, version: 1);
        Assert.Contains(await QueryAsync(projectId, "Zanzibarlegacy"), s => s.Content.Contains("Zanzibarlegacy"));

        var (_, second) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client, projectId);
        await SubmitCreatedAsync(projectId, second, ModelFixtures.ValidSubmission(), version: 2);

        Assert.DoesNotContain(await QueryAsync(projectId, "Zanzibarlegacy"), s => s.Content.Contains("Zanzibarlegacy"));
    }

    [Fact]
    public async Task Reanalysis_ReplacesAClaimsDocumentInsteadOfAddingASecondOne()
    {
        var (projectId, first) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client);
        await SubmitCreatedAsync(projectId, first, ModelFixtures.ValidSubmission(), version: 1);
        var (_, second) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client, projectId);
        await SubmitCreatedAsync(projectId, second, ModelFixtures.ValidSubmission(baseCommit: "def456"), version: 2);

        var snippets = await QueryAsync(projectId, "App references Lib");

        Assert.Single(snippets, s => s.Content.Contains("claim clm.ref "));
    }

    [Fact]
    public async Task ACorrection_ReplacesTheClaimsIndexedDocument()
    {
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(_client);
        await SubmitCreatedAsync(projectId, requestId, ModelFixtures.ValidSubmission(), version: 1);

        var response = await _client.PostAsJsonAsync($"/projects/{projectId}/model/claims/clm.ref/corrections",
            new CorrectClaimRequest(ClaimStatus.Corrected, "Quetzalplugin loading happens at run time"), TestJsonOptions.Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ModelProjectionServiceTests.WaitIndexedAsync(_client, projectId);

        var snippets = await QueryAsync(projectId, "Quetzalplugin");
        var snippet = Assert.Single(snippets, s => s.Content.Contains("claim clm.ref "));
        Assert.Contains("Developer correction (authoritative): Quetzalplugin", snippet.Content);
        Assert.Contains("Corrected, extracted deterministically]", snippet.Content);
        Assert.DoesNotContain(await QueryAsync(projectId, "App references Lib"),
            s => s.Content.Contains("claim clm.ref ") && s.Content.Contains("Proposed, extracted deterministically]"));
    }

    [Fact]
    public async Task Submit_WhenProjectingIntoTheIndexThrows_TheModelIsStillStored()
    {
        using var throwingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IKnowledgeIndex>(new ThrowingKnowledgeIndex())));
        var client = throwingFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MomosHostFactory.WorkerApiKey);
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(client);

        var response = await ProjectModelEndpointsTests.SubmitAsync(client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var latest = await client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(1, latest!.ModelVersion);
        Assert.Null(latest.KnowledgeIndexedAt);
    }

    private sealed class ThrowingKnowledgeIndex : IKnowledgeIndex
    {
        public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated knowledge index failure");

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated knowledge index failure");

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated knowledge index failure");
    }
}
