using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Agent;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class ManualSynthesizerTests
{
    private static readonly ProjectInfo Project = new(Guid.NewGuid(), "acme", "https://example.invalid/acme.git", null, "Queue work.", "Reliable.", "Library.");

    private static ChatResponse Call(string tool, Dictionary<string, object?> args, long tokens = 10) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, args)]))
        {
            Usage = new UsageDetails { TotalTokenCount = tokens },
        };

    private static ChatResponse Done(long tokens = 10) =>
        new(new ChatMessage(ChatRole.Assistant, "done")) { Usage = new UsageDetails { TotalTokenCount = tokens } };

    private static FakeChatClient Pass(params ChatResponse[] turns) => new(turns[..^1], turns[^1]);

    private static FakeChatClient FailingPass(Exception failure, params ChatResponse[] turns) => new(turns, failure);

    private static Dictionary<string, object?> Chapter(string title, string guide) =>
        new() { ["title"] = title, ["purpose"] = $"About {title}.", ["guide"] = guide };

    private static Dictionary<string, object?> Assessment(string topic) => new()
    {
        ["topic"] = topic,
        ["tier"] = ClaimTier.Assessment,
        ["statement"] = $"{topic} reads as deliberate.",
        ["confidence"] = ClaimConfidence.Low,
        ["evidence"] = new[] { new ProposedEvidence(ProposedEvidenceKind.Claim, ClaimKey: "clm.fact") },
    };

    private static ManualSynthesizer Build(AnalysisOptions? options, params FakeChatClient[] passes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClientProvider>(new FakeChatClientProvider(passes));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var runtime = new FakeExecutionRuntimeProvider();
        services.AddSingleton<IExecutionRuntimeProvider>(runtime);
        services.AddSingleton<IHostApiClient>(new FakeHostApiClient([]));
        services.AddIronHiveAgentEngine();
        var loops = services.BuildServiceProvider().GetRequiredService<IAnalysisAgentLoopFactory>();
        return new ManualSynthesizer(loops, runtime, Options.Create(options ?? new AnalysisOptions()),
            Options.Create(new GpuStackLlmOptions { Endpoint = "http://llm.invalid", ApiKey = "k", Model = "configured-model" }),
            NullLogger<ManualSynthesizer>.Instance);
    }

    private static Task<ProjectModelPayload> Run(ManualSynthesizer synthesizer, CancellationToken cancellationToken = default) =>
        synthesizer.SynthesizeAsync(SynthesisDraftTests.Skeleton(), new ExecutionSessionHandle("s"), Project, cancellationToken);

    [Fact]
    public async Task TheOverviewPlansChapters_AndEachChapterPassFillsOne()
    {
        var synthesizer = Build(null,
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Call("ProposeChapter", Chapter("Risks", "risks-and-debt")), Done()),
            Pass(Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Map, ["reference"] = "*" }), Done()),
            Pass(Call("ProposeClaim", Assessment("retries")), Call("SetOwnerSummary", new() { ["claimKeys"] = new[] { ModelIds.SynthesizedClaim("retries") } }), Done()));

        var model = await Run(synthesizer);

        Assert.Equal(["system-map.md", "risks.md"], model.Outline.Select(s => s.Path));
        Assert.Equal(OutlineBlockKind.Map, Assert.Single(model.Outline[0].Blocks).Kind);
        Assert.Equal([ModelIds.SynthesizedClaim("retries")], model.Outline[1].OwnerSummaryClaims);
        Assert.Equal(new CoverageGeneratorPayload("configured-model", AnalysisPrompts.Version), model.Coverage.Generator);
    }

    [Fact]
    public async Task AFailedOverview_LeavesTheSkeletonAndSaysWhy()
    {
        var synthesizer = Build(null, FailingPass(new HttpRequestException("model endpoint down")));

        var model = await Run(synthesizer);

        Assert.Empty(model.Outline);
        Assert.Equal(SynthesisDraftTests.Skeleton().Claims.Select(c => c.Key), model.Claims.Select(c => c.Key));
        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-outline");
        Assert.Contains("HttpRequestException", gap.Reason);
    }

    [Fact]
    public async Task AFailedChapter_IsDroppedWhole_AndTheOthersStay()
    {
        var synthesizer = Build(null,
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Call("ProposeChapter", Chapter("Risks", "risks-and-debt")), Done()),
            FailingPass(new HttpRequestException("boom"), Call("ProposeClaim", Assessment("half written"))),
            Pass(Call("ProposeClaim", Assessment("retries")), Done()));

        var model = await Run(synthesizer);

        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("half written"));
        Assert.Contains(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("retries"));
        Assert.Equal(2, model.Outline.Count);
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Area == "manual-chapter" && g.Reason.StartsWith("system-map.md:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChapterOverItsTokenBudget_IsDropped()
    {
        var synthesizer = Build(new AnalysisOptions { MaxChapterTokens = 100 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Done()),
            Pass(Call("ProposeClaim", Assessment("expensive"), tokens: 500), Call("ProposeClaim", Assessment("never")), Done()));

        var model = await Run(synthesizer);

        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("expensive"));
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason.Contains("token budget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnceTheTotalBudgetIsSpent_LaterChaptersAreNotRun()
    {
        var chapter = Pass(Done());
        var synthesizer = Build(new AnalysisOptions { MaxTotalTokens = 300 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map"), tokens: 100), Call("ProposeChapter", Chapter("Risks", "risks-and-debt"), tokens: 100), Done(100)),
            chapter);

        var model = await Run(synthesizer);

        Assert.Null(chapter.LastOptions); // no chapter pass ever called its model
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason.StartsWith("risks.md:", StringComparison.Ordinal) && g.Reason.Contains("token budget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeadlineMidChapter_DropsThatChapterAndStillSubmits()
    {
        var synthesizer = Build(new AnalysisOptions { MaxDuration = TimeSpan.FromMilliseconds(500) },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Done()),
            new FakeChatClient([Call("ProposeClaim", Assessment("slow"))], Done()) { Delay = TimeSpan.FromSeconds(10) });

        var model = await Run(synthesizer);

        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason.Contains("time budget", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("slow"));
    }

    [Fact]
    public async Task AShutdownMidChapter_Propagates()
    {
        using var shutdown = new CancellationTokenSource();
        var synthesizer = Build(null,
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Done()),
            new FakeChatClient([], Done()) { Delay = TimeSpan.FromSeconds(10) });

        shutdown.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(synthesizer, shutdown.Token));
    }

    [Fact]
    public async Task TheConfiguredAnalysisModel_IsRecordedAsTheGenerator()
    {
        var synthesizer = Build(new AnalysisOptions { Model = "bigger-model" }, Pass(Done()));

        var model = await Run(synthesizer);

        Assert.Equal("bigger-model", model.Coverage.Generator!.Model);
    }
}
