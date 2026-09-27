using System.Net.Http.Json;
using System.Text.RegularExpressions;
using IronHive.Agent.Loop;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Host.Contracts;
using Momos.Worker.Agent;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using HostClaimStatus = Momos.Host.Domain.ClaimStatus;
using HostRequestStatus = Momos.Host.Domain.InspectionRequestStatus;

namespace Momos.Worker.IntegrationTests.Execution;

/// <summary>
/// The project-model walking skeleton, end to end against a real Host, a real git repository, the
/// real pull loop and the real extractor: request an analysis → the Worker claims it, clones the
/// repository into an empty session, extracts and submits the model → the model and its report are
/// served → a developer corrects a claim → a re-analysis keeps the correction, and the correction is
/// what the inspection agent's knowledge query returns.
/// </summary>
/// <remarks>Keep this the only test in the class: the pull loop claims whatever is pending on the
/// class's Host, so a sibling test's request would be picked up by it.</remarks>
public sealed partial class WalkingSkeletonTests(TestMomosHostFactory factory) : IClassFixture<TestMomosHostFactory>, IDisposable
{
    private const string CorrectionText = "Lib is a Xylophonic plugin loaded at run time";

    private readonly string _repository = Directory.CreateTempSubdirectory("momos-skeleton-").FullName;
    private readonly LocalProcessExecutionRuntimeProvider _runtime = new();

    [Fact]
    public async Task AnalyzeReportCorrectReanalyze_TheCorrectionSurvivesAndReachesTheInspectionAgentsKnowledgeQuery()
    {
        await CreateRepositoryAsync();
        var http = factory.CreateAuthorizedClient();
        var host = new HostApiClient(factory.CreateAuthorizedClient());

        // 1: register the project, pointing at the repository the Worker will clone.
        var created = await http.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-skeleton", _repository, null, "purpose", "vision", "scope"));
        created.EnsureSuccessStatusCode();
        var projectId = (await created.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;

        // 2–4: request an analysis; the pull loop claims it, clones, extracts and submits.
        await AnalyzeAsync(http, host, projectId);

        var model = await http.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(1, model!.ModelVersion);
        Assert.Matches("^[0-9a-f]{40}$", model.BaseCommit);
        Assert.Equal(["App", "Lib"], model.Components.Select(c => c.Name).Order(StringComparer.Ordinal));
        var relation = Assert.Single(model.Relations);
        var referenceKey = Assert.Single(relation.Claims);
        var referenceClaim = model.Claims.Single(c => c.Key == referenceKey);
        Assert.Equal("App references Lib (project reference).", referenceClaim.Statement);
        Assert.Equal(HostClaimStatus.Proposed, referenceClaim.Status);

        // 5: the report — a tree of markdown documents whose every relative link lands on a document in it.
        var report = await http.GetFromJsonAsync<ProjectModelReportResponse>($"/projects/{projectId}/model/report", TestJsonOptions.Value);
        var index = report!.Documents.Single(d => d.Path == "index.md").Content;
        Assert.Contains("c0 -->|references| c1", index); // components are ordered by path, so App is c0
        Assert.Contains(report.Documents, d => d.Path.StartsWith("claims/", StringComparison.Ordinal) && d.Content.Contains("App references Lib"));
        AssertEveryLinkResolves(report.Documents);

        // 6: correct the reference claim.
        var corrected = await http.PostAsJsonAsync($"/projects/{projectId}/model/claims/{referenceKey}/corrections",
            new CorrectClaimRequest(HostClaimStatus.Corrected, CorrectionText), TestJsonOptions.Value);
        corrected.EnsureSuccessStatusCode();

        // 7: what the inspection agent's QueryProjectKnowledge tool gets back.
        await AssertKnowledgeCarriesCorrectionAsync(host, projectId);

        // Re-analysis of the unchanged repository: the statement is the same, so the verdict carries over.
        await AnalyzeAsync(http, host, projectId);

        var reanalyzed = await http.GetFromJsonAsync<ProjectModelResponse>($"/projects/{projectId}/model", TestJsonOptions.Value);
        Assert.Equal(2, reanalyzed!.ModelVersion);
        var carried = reanalyzed.Claims.Single(c => c.Key == referenceKey);
        Assert.Equal(HostClaimStatus.Corrected, carried.Status);
        Assert.Equal(CorrectionText, carried.Correction);
        await AssertKnowledgeCarriesCorrectionAsync(host, projectId);
    }

    private async Task CreateRepositoryAsync()
    {
        Directory.CreateDirectory(Path.Combine(_repository, "src", "App"));
        Directory.CreateDirectory(Path.Combine(_repository, "src", "Lib"));
        await File.WriteAllTextAsync(Path.Combine(_repository, "src", "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Lib\\Lib.csproj\" />\n  </ItemGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(_repository, "src", "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        foreach (var args in new[] { new[] { "init", "-q" }, ["add", "."], ["commit", "-q", "-m", "initial"] })
        {
            var result = await LocalProcessExecutionRuntimeProvider.RunAsync(_repository, new ExecutionCommand("git", args));
            Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    /// <summary>Requests an analysis and runs the real pull loop until the Host reports it finished.</summary>
    private async Task AnalyzeAsync(HttpClient http, HostApiClient host, Guid projectId)
    {
        var requested = await http.PostAsJsonAsync($"/projects/{projectId}/analysis-requests", new CreateAnalysisRequestRequest(null));
        requested.EnsureSuccessStatusCode();
        var requestId = (await requested.Content.ReadFromJsonAsync<InspectionRequestResponse>(TestJsonOptions.Value))!.Id;

        var worker = new PullExecutionBackgroundService(
            host,
            new AnalysisMustNotRunTheAgent(),
            _runtime,
            DeterministicAnalyzer(_runtime),
            new NoGitCredentials(),
            new NoSelfUpdate(),
            Options.Create(new PullExecutionOptions { PollInterval = TimeSpan.FromMilliseconds(50) }),
            NullLogger<PullExecutionBackgroundService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        InspectionRequestResponse? status = null;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                status = await http.GetFromJsonAsync<InspectionRequestResponse>($"/analysis-requests/{requestId}", TestJsonOptions.Value);
                if (status!.Status is HostRequestStatus.Completed or HostRequestStatus.Failed)
                {
                    break;
                }

                await Task.Delay(100);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(status?.Status == HostRequestStatus.Completed,
            $"Analysis ended as {status?.Status}: {status?.FailureReason}");
    }

    private static async Task AssertKnowledgeCarriesCorrectionAsync(HostApiClient host, Guid projectId)
    {
        var snippets = await host.QueryKnowledgeAsync(projectId, "Xylophonic", CancellationToken.None);
        Assert.Contains(snippets, s => s.Contains($"Developer correction (authoritative): {CorrectionText}"));
    }

    private static void AssertEveryLinkResolves(IReadOnlyList<ReportDocumentDto> documents)
    {
        var paths = documents.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var links = 0;
        foreach (var document in documents)
        {
            foreach (Match link in MarkdownLink().Matches(document.Content))
            {
                links++;
                var target = Resolve(document.Path, link.Groups["target"].Value);
                Assert.True(paths.Contains(target), $"{document.Path} links to {link.Groups["target"].Value}, which is not in the report.");
            }
        }

        Assert.True(links > 0, "The report contains no links between its documents.");
    }

    private static string Resolve(string fromPath, string target)
    {
        var segments = fromPath.Split('/').SkipLast(1).ToList();
        foreach (var part in target.Split('/'))
        {
            if (part == "..")
            {
                segments.RemoveAt(segments.Count - 1);
            }
            else if (part != ".")
            {
                segments.Add(part);
            }
        }

        return string.Join('/', segments);
    }

    [GeneratedRegex(@"\]\((?<target>[^)\s]+\.md)\)")]
    private static partial Regex MarkdownLink();

    public void Dispose()
    {
        _runtime.Dispose();
        LocalProcessExecutionRuntimeProvider.DeleteDirectory(_repository);
    }

    /// <summary>An analysis never runs the agent loop; reaching it means the pull loop branched wrong.</summary>
    private sealed class AnalysisMustNotRunTheAgent : ISessionAwareAgentLoopFactory
    {
        public Task<IAgentLoop> CreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An analysis request reached the agent loop.");

        public Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An analysis request reached the agent loop.");

        public Task<(IAgentLoop Loop, FindingSink Findings, ToolCallTraceSink ToolCalls)> CreateAsync(
            AgentLoopFactoryOptions options, ExecutionSessionHandle session, Guid? projectId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An analysis request reached the agent loop.");
    }

    private sealed class NoSelfUpdate : IWorkerSelfUpdater
    {
        public Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoGitCredentials : IGitCredentialSource
    {
        public Task<GitCredential?> GetAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitCredential?>(null);
    }

    /// <summary>The analysis path with the language-model passes switched off — these tests cover the pull loop, not the passes.</summary>
    private static IProjectAnalyzer DeterministicAnalyzer(IExecutionRuntimeProvider runtime) =>
        new ProjectAnalyzer(new ProjectModelExtractor(runtime), new UnusedSynthesizer(), Options.Create(new AnalysisOptions { Synthesis = false }), NullLogger<ProjectAnalyzer>.Instance);

    private sealed class UnusedSynthesizer : IManualSynthesizer
    {
        public Task<ProjectModelPayload> SynthesizeAsync(ProjectModelPayload skeleton, ExecutionSessionHandle session, ProjectInfo project, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthesis is switched off in these tests");
    }
}
