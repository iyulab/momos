using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Knowledge;
using Momos.Host.Tests.Endpoints;

namespace Momos.Host.Tests.Knowledge;

public sealed class ModelProjectionServiceTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    internal static async Task<ProjectModelResponse> WaitIndexedAsync(HttpClient client, Guid projectId, int? version = null)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var model = await client.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
            if (model!.KnowledgeIndexedAt is not null && (version is null || model.ModelVersion == version))
            {
                return model;
            }

            Assert.True(DateTime.UtcNow < until, "the knowledge index did not catch up within 30s");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Submit_AnswersBeforeASlowIndexFinishes_AndTheIndexCatchesUp()
    {
        var slow = new DelayingKnowledgeIndex(TimeSpan.FromSeconds(2));
        using var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IKnowledgeIndex>(slow)));
        var client = Authorized(f);
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(client);

        var started = DateTime.UtcNow;
        var response = await ProjectModelEndpointsTests.SubmitAsync(client, requestId, ModelFixtures.ValidSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "submit waited for the index");
        Assert.NotNull((await WaitIndexedAsync(client, projectId)).KnowledgeIndexedAt);
    }

    [Fact]
    public async Task WhileTheIndexKeepsFailing_TheModelStaysUnindexed_AndIsRetried()
    {
        var flaky = new FailingThenWorkingKnowledgeIndex(failures: 3);
        using var f = factory.WithWebHostBuilder(b => b
            .UseSetting("Momos:Host:Knowledge:ProjectionRetryDelay", "00:00:00.200")
            .ConfigureServices(s => s.AddSingleton<IKnowledgeIndex>(flaky)));
        var client = Authorized(f);
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(client);
        await ProjectModelEndpointsTests.SubmitAsync(client, requestId, ModelFixtures.ValidSubmission());

        var model = await WaitIndexedAsync(client, projectId);

        Assert.True(flaky.Attempts > 3);
        Assert.NotNull(model.KnowledgeIndexedAt);
    }

    [Fact]
    public async Task TwoUnindexedVersions_OnlyTheLatestIsIndexed_AndStaleClaimsAreRemoved()
    {
        var recording = new RecordingKnowledgeIndex();
        using var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IKnowledgeIndex>(recording)));
        var client = Authorized(f);
        var projectId = await SeedTwoUnindexedVersionsAsync(f, withLegacyClaimOnlyInV1: true);

        f.Services.GetRequiredService<ModelProjectionSignal>().Notify();
        await WaitIndexedAsync(client, projectId, version: 2);

        Assert.Contains(ModelKnowledgeProjector.DocumentId(projectId, "clm.legacy"), recording.Deleted);
        Assert.DoesNotContain(recording.Indexed, id => id == ModelKnowledgeProjector.DocumentId(projectId, "clm.legacy"));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MomosDbContext>();
        Assert.All(await db.ProjectModels.Where(m => m.ProjectId == projectId).ToListAsync(), m => Assert.NotNull(m.KnowledgeIndexedAt));
    }

    [Fact]
    public async Task AModelLeftUnindexedAtStartup_IsIndexedByTheService()
    {
        // Seed straight into the database (as if the Host died mid-projection), then boot a
        // fresh host over the same database: the service's startup sweep must pick it up.
        var projectId = await SeedTwoUnindexedVersionsAsync(factory, withLegacyClaimOnlyInV1: false);
        using var fresh = factory.WithWebHostBuilder(_ => { });

        Assert.NotNull((await WaitIndexedAsync(Authorized(fresh), projectId, version: 2)).KnowledgeIndexedAt);
    }

    [Fact]
    public async Task ACorrectionSavedWhileTheModelIsBeingIndexed_IsIndexedToo()
    {
        var gated = new GatedKnowledgeIndex();
        using var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IKnowledgeIndex>(gated)));
        var client = Authorized(f);
        var (projectId, requestId) = await ProjectModelEndpointsTests.StartAnalysisAsync(client);
        await ProjectModelEndpointsTests.SubmitAsync(client, requestId, ModelFixtures.ValidSubmission());

        // The service has read the model and is inside the index when the correction lands.
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        var correction = await client.PostAsJsonAsync($"/projects/{projectId}/model/claims/clm.ref/corrections",
            new CorrectClaimRequest(ClaimStatus.Corrected, "Lib is loaded at run time"), TestJsonOptions.Value);
        Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        gated.Release();

        await WaitIndexedAsync(client, projectId);

        Assert.Contains("Developer correction (authoritative): Lib is loaded at run time",
            gated.LastContent(ModelKnowledgeProjector.DocumentId(projectId, "clm.ref")));
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MomosHostFactory.WorkerApiKey);
        return c;
    }

    /// <summary>Stores a project with two model versions that were never indexed — v1 carrying
    /// <c>clm.ref</c> (and optionally <c>clm.legacy</c>), v2 carrying only <c>clm.ref</c> —
    /// straight through the database, bypassing the endpoints and their signal.</summary>
    private static async Task<Guid> SeedTwoUnindexedVersionsAsync(WebApplicationFactory<Program> f, bool withLegacyClaimOnlyInV1)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MomosDbContext>();

        var project = new Project
        {
            Name = "acme-seeded",
            RepositoryUrl = "https://example.invalid/acme.git",
            Purpose = "purpose",
            Vision = "vision",
            Scope = "scope",
        };
        db.Projects.Add(project);

        ProjectModel Version(int version, params string[] claimKeys)
        {
            var request = new InspectionRequest
            {
                ProjectId = project.Id,
                Kind = InspectionRequestKind.Analysis,
                Status = InspectionRequestStatus.Completed,
            };
            db.InspectionRequests.Add(request);

            var model = new ProjectModel
            {
                ProjectId = project.Id,
                ModelVersion = version,
                BaseCommit = $"commit{version}",
                AnalysisRequestId = request.Id,
            };
            foreach (var key in claimKeys)
            {
                model.Claims.Add(new ModelClaim
                {
                    ProjectModelId = model.Id,
                    Key = key,
                    Tier = ClaimTier.Fact,
                    Statement = $"{key} statement",
                    Evidence = [new ClaimEvidence(EvidenceKind.Code, Path: "src/App/App.csproj")],
                    Confidence = ClaimConfidence.High,
                    Origin = ClaimOrigin.Deterministic,
                });
            }

            return model;
        }

        db.ProjectModels.Add(withLegacyClaimOnlyInV1 ? Version(1, "clm.ref", "clm.legacy") : Version(1, "clm.ref"));
        db.ProjectModels.Add(Version(2, "clm.ref"));
        await db.SaveChangesAsync();
        return project.Id;
    }

    private sealed class DelayingKnowledgeIndex(TimeSpan delay) : IKnowledgeIndex
    {
        public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeSearchHit>>([]);

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    private sealed class FailingThenWorkingKnowledgeIndex(int failures) : IKnowledgeIndex
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _attempts) <= failures
                ? throw new InvalidOperationException("simulated knowledge index outage")
                : Task.CompletedTask;

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeSearchHit>>([]);

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingKnowledgeIndex : IKnowledgeIndex
    {
        public ConcurrentQueue<string> Indexed { get; } = new();

        public ConcurrentQueue<string> Deleted { get; } = new();

        public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken)
        {
            Indexed.Enqueue(documentId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeSearchHit>>([]);

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken)
        {
            Deleted.Enqueue(documentId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the first index write until released, so a test can change the database
    /// while a projection is in flight; remembers the last content written per document.</summary>
    private sealed class GatedKnowledgeIndex : IKnowledgeIndex
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, string> _content = new();

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public string LastContent(string documentId) => _content.GetValueOrDefault(documentId, "");

        public async Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            _content[documentId] = content;
        }

        public Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeSearchHit>>([]);

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
