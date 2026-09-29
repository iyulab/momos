using System.Text.Json;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Host.Contracts;
using Momos.Worker.Agent;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using Momos.Worker.IntegrationTests.Execution;
using HostClaimValidator = Momos.Host.Domain.ClaimValidator;
using HostModelClaim = Momos.Host.Domain.ModelClaim;

namespace Momos.Worker.IntegrationTests.Analysis;

/// <summary>
/// The analysis passes against a real repository and the Host's own validator: evidence is checked
/// by git as the analyzed commit stores it (a file the agent writes into the checkout proves
/// nothing), and a model the passes assemble is one the Host accepts as it stands — the evidence
/// that the Worker's copy of the Host's rules leaves nothing out for what the passes produce.
/// </summary>
public sealed class ManualSynthesisRoundTripTests : IAsyncLifetime
{
    private const string LibProject = """<Project Sdk="Microsoft.NET.Sdk" />""";
    private const string IssueUrl = "https://example.invalid/acme/issues/3";

    private readonly LocalProcessExecutionRuntimeProvider _runtime = new();
    private readonly string _source = Directory.CreateTempSubdirectory("momos-synthesis-source-").FullName;
    private ExecutionSessionHandle _session = null!;
    private string _narrowingCommit = null!;

    private static string Lib => ModelIds.Component("src/Lib/Lib.csproj");

    private static string Key(string topic) => ModelIds.SynthesizedClaim(topic);

    public async Task InitializeAsync()
    {
        await GitAsync("init", "-q", "-b", "main");
        await CommitAsync("Seed",
            ("src/Lib/Lib.csproj", LibProject),
            ("src/Lib/Queue.cs", "public sealed class Queue { public bool TryClaim() => true; }\n"),
            ("README.md", $"Lib queues work. See {IssueUrl}.\n"));
        await CommitAsync("Claim only pending work",
            ("src/Lib/Queue.cs", "public sealed class Queue { public bool TryClaim() => Pending; public bool Pending { get; set; } }\n"));
        _narrowingCommit = (await LocalProcessExecutionRuntimeProvider.RunAsync(_source, new ExecutionCommand("git", ["rev-parse", "HEAD"]))).Output!.Trim();

        _session = await _runtime.CreateSessionAsync(new ExecutionSessionRequest("native"));
        var clone = await _runtime.ExecuteAsync(_session, new ExecutionCommand("git", ["clone", "-q", "--", _source, "."]));
        Assert.True(clone.Success, clone.Error);

        // What an agent could do with RunCommand: write a file into the checkout. It exists in the
        // working tree only, never at the analyzed commit.
        var planted = await _runtime.ExecuteAsync(_session, new ExecutionCommand("git", ["show", "--output=src/Lib/Planted.cs", "HEAD:src/Lib/Queue.cs"]));
        Assert.True(planted.Success, planted.Error);
    }

    public Task DisposeAsync()
    {
        _runtime.Dispose();
        LocalProcessExecutionRuntimeProvider.DeleteDirectory(_source);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASynthesizedModel_PassesTheHostValidatorUnchanged()
    {
        var model = await AnalyzeAsync();

        var request = JsonSerializer.Deserialize<SubmitProjectModelRequest>(JsonSerializer.Serialize(model, TestJsonOptions.Value), TestJsonOptions.Value)!;
        Assert.Empty(request.MissingFields());
        var claims = request.Claims.Select(c => new HostModelClaim
        {
            ProjectModelId = Guid.NewGuid(),
            Key = c.Key,
            Tier = c.Tier!.Value,
            Statement = c.Statement,
            Evidence = [.. c.Evidence.Select(e => e.ToDomain())],
            Confidence = c.Confidence!.Value,
            Origin = c.Origin!.Value,
        }).ToList();

        Assert.Empty(HostClaimValidator.Validate(claims, request.ToElements()));
    }

    [Fact]
    public async Task EvidenceIsCheckedAgainstTheCommit_NotTheWorktree()
    {
        var model = await AnalyzeAsync();

        Assert.DoesNotContain(model.Claims, c => c.Key == Key("planted"));
        Assert.DoesNotContain(model.Claims, c => c.Key == Key("directory"));
        Assert.Contains(model.Claims, c => c.Key == Key("queue type") && c.Origin == ClaimOrigin.Synthesized);
        Assert.Contains(model.Claims, c => c.Key == Key("issue link"));
        var history = Assert.Single(model.Claims, c => c.Key == Key("claim rule history"));
        Assert.Equal(_narrowingCommit, Assert.Single(history.Evidence).Sha);
    }

    [Fact]
    public async Task RejectionsAndWhatWasWritten_AreRecordedInCoverage()
    {
        var model = await AnalyzeAsync();

        Assert.Equal(4, model.Coverage.Rejected.Sum(r => r.Count));
        Assert.Contains(model.Coverage.Analyzed, a => a.Area == "manual-synthesis" && a.Detail.StartsWith("2 of 2 chapters", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Coverage.NotAnalyzed, g => g.Area == "source-files");
        Assert.Equal(["invariants.md", "system-map.md"], model.Outline.Select(s => s.Path));
        Assert.Equal("Holds the work queue.", Assert.Single(model.Components).Responsibility);
    }

    private static ChatResponse Call(string tool, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, args)]));

    private static ChatResponse Done() => new(new ChatMessage(ChatRole.Assistant, "done"));

    private static Dictionary<string, object?> Claim(string topic, ClaimTier tier, string statement, ClaimConfidence confidence, params ProposedEvidence[] evidence) =>
        new() { ["topic"] = topic, ["tier"] = tier, ["statement"] = statement, ["confidence"] = confidence, ["evidence"] = evidence };

    private IReadOnlyList<ChatResponse>[] Script() =>
    [
        [
            Call("ProposeChapter", new() { ["title"] = "Invariants", ["purpose"] = "What must not break.", ["guide"] = "invariants" }),
            Call("ProposeChapter", new() { ["title"] = "System map", ["purpose"] = "The parts.", ["guide"] = "system-map" }),
            Done(),
        ],
        [
            Call("ProposeClaim", Claim("queue type", ClaimTier.Fact, "Lib defines the work queue.", ClaimConfidence.High,
                new ProposedEvidence(ProposedEvidenceKind.Code, Path: "src/Lib/Queue.cs", Symbol: "sealed class Queue"))),
            Call("ProposeClaim", Claim("planted", ClaimTier.Fact, "Lib has a planted type.", ClaimConfidence.High,
                new ProposedEvidence(ProposedEvidenceKind.Code, Path: "src/Lib/Planted.cs", Symbol: "sealed class Queue"))),
            Call("ProposeClaim", Claim("directory", ClaimTier.Fact, "Lib keeps its queue in one file.", ClaimConfidence.High,
                new ProposedEvidence(ProposedEvidenceKind.Code, Path: "src/Lib", Symbol: "Queue.cs"))),
            Call("ProposeClaim", Claim("claim rule history", ClaimTier.History, "Claiming was narrowed to pending work.", ClaimConfidence.High,
                new ProposedEvidence(ProposedEvidenceKind.Commit, Sha: _narrowingCommit[..7]))),
            Call("ProposeInvariant", new()
            {
                ["topic"] = "claim pending",
                ["statement"] = "Only pending work is claimed.",
                ["kind"] = "state-machine",
                ["appliesTo"] = new[] { Lib },
                ["claimKeys"] = new[] { Key("queue type"), Key("claim rule history") },
            }),
            Call("ProposeDecision", new()
            {
                ["topic"] = "in-process queue",
                ["summary"] = "Work queues in-process.",
                ["alternatives"] = new[] { "a broker" },
                ["rationale"] = "Brokers were too costly.",
                ["claimKeys"] = new[] { Key("queue type") },
            }),
            Call("ProposeDecision", new()
            {
                ["topic"] = "in-process queue 2",
                ["summary"] = "Work queues in-process.",
                ["alternatives"] = new[] { "a broker" },
                ["rationale"] = "unrecorded",
                ["claimKeys"] = new[] { Key("queue type") },
            }),
            Call("ProposeClaim", Claim("issue link", ClaimTier.History, "The claim rule traces to issue 3.", ClaimConfidence.Medium,
                new ProposedEvidence(ProposedEvidenceKind.Issue, Url: IssueUrl))),
            Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Invariant, ["reference"] = ModelIds.Element(ModelIds.InvariantPrefix, "claim pending") }),
            Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Decision, ["reference"] = ModelIds.Element(ModelIds.DecisionPrefix, "in-process queue 2") }),
            Call("ProposeClaim", Claim("risk", ClaimTier.Assessment, "Claiming is the riskiest path.", ClaimConfidence.High,
                new ProposedEvidence(ProposedEvidenceKind.Claim, ClaimKey: Key("queue type")))),
            Call("SetOwnerSummary", new() { ["claimKeys"] = new[] { Key("queue type"), Key("claim rule history") } }),
            Done(),
        ],
        [
            Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Map, ["reference"] = "*" }),
            Call("DescribeComponent", new() { ["componentId"] = Lib, ["responsibility"] = "Holds the work queue.", ["claimKeys"] = new[] { Key("queue type") } }),
            Done(),
        ],
    ];

    private async Task<ProjectModelPayload> AnalyzeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new ScriptedChatClientProvider(Script()));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IExecutionRuntimeProvider>(_runtime);
        services.AddSingleton<IHostApiClient>(new NoKnowledgeHost());
        services.AddIronHiveAgentEngine();
        var loops = services.BuildServiceProvider().GetRequiredService<IAnalysisAgentLoopFactory>();
        var synthesizer = new ManualSynthesizer(loops, _runtime, Options.Create(new AnalysisOptions()),
            Options.Create(new GpuStackLlmOptions { Endpoint = "http://llm.invalid", ApiKey = "k", Model = "scripted-model" }),
            TimeProvider.System, NullLogger<ManualSynthesizer>.Instance);
        var analyzer = new ProjectAnalyzer(new ProjectModelExtractor(_runtime), synthesizer, Options.Create(new AnalysisOptions()), NullLogger<ProjectAnalyzer>.Instance);
        return await analyzer.AnalyzeAsync(_session, new ProjectInfo(Guid.NewGuid(), "acme", _source, null, "p", "v", "s"), CancellationToken.None);
    }

    private async Task CommitAsync(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", message);
    }

    private async Task GitAsync(params string[] args)
    {
        var result = await LocalProcessExecutionRuntimeProvider.RunAsync(_source, new ExecutionCommand("git", args));
        Assert.True(result.Success, result.Error);
    }

    /// <summary>The Host as far as the passes use it: a knowledge query that finds nothing.</summary>
    private sealed class NoKnowledgeHost : IHostApiClient
    {
        public Task<IReadOnlyList<string>> QueryKnowledgeAsync(Guid projectId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ClaimNextResult> ClaimNextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, IReadOnlyList<ToolCallPayload> toolCalls, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SubmitModelAsync(Guid analysisRequestId, ProjectModelPayload model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SubmitFailureAsync(Guid inspectionRequestId, string reason, IReadOnlyList<ToolCallPayload> toolCalls, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
