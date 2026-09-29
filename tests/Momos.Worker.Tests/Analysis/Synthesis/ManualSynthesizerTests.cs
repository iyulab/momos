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

    /// <summary>Captures each formatted log message so a test can assert on the pass telemetry.</summary>
    private sealed class RecordingLogger : ILogger<ManualSynthesizer>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static ManualSynthesizer Build(AnalysisOptions? options, params FakeChatClient[] passes) => Build(options, null, passes);

    private static ManualSynthesizer Build(AnalysisOptions? options, ILogger<ManualSynthesizer>? logger, params FakeChatClient[] passes)
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
            TimeProvider.System, logger ?? NullLogger<ManualSynthesizer>.Instance);
    }

    private static Task<ProjectModelPayload> Run(ManualSynthesizer synthesizer, CancellationToken cancellationToken = default, string? language = null) =>
        synthesizer.SynthesizeAsync(SynthesisDraftTests.Skeleton(), new ExecutionSessionHandle("s"), Project, language, cancellationToken);

    [Fact]
    public async Task TheLanguage_ReachesEveryPassesSystemPrompt()
    {
        var overview = Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Done());
        var chapter = Pass(Done());
        var synthesizer = Build(null, overview, chapter);

        await synthesizer.SynthesizeAsync(SynthesisDraftTests.Skeleton(), new ExecutionSessionHandle("s"), Project, "ko", CancellationToken.None);

        Assert.Contains(overview.LastMessages!, m => m.Role == ChatRole.System && m.Text.Contains("Write in Korean", StringComparison.Ordinal));
        Assert.Contains(chapter.LastMessages!, m => m.Role == ChatRole.System && m.Text.Contains("Write in Korean", StringComparison.Ordinal));
    }

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
    public async Task AnOverviewCutByItsBudget_KeepsTheChaptersItPlanned_AndTheyAreWritten()
    {
        // The second chapter's answer spends the overview's budget; both chapters were already
        // verified and staged, so the next call is what the budget stops.
        var synthesizer = Build(new AnalysisOptions { MaxOverviewTokens = 100 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Call("ProposeChapter", Chapter("Risks", "risks-and-debt"), tokens: 500),
                Call("ProposeChapter", Chapter("Never", "")), Done()),
            Pass(Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Map, ["reference"] = "*" }), Done()),
            Pass(Call("ProposeClaim", Assessment("retries")), Done()));

        var model = await Run(synthesizer);

        Assert.Equal(["system-map.md", "risks.md"], model.Outline.Select(s => s.Path));
        Assert.Equal(OutlineBlockKind.Map, Assert.Single(model.Outline[0].Blocks).Kind);
        Assert.Contains(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("retries"));
        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-outline");
        Assert.Equal("partial — token budget reached", gap.Reason);
        Assert.Contains(model.Coverage.Analyzed, a => a.Area == "manual-synthesis" && a.Detail.StartsWith("2 of 2 chapters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOverviewCutByItsBudget_BeforeAnyChapter_LeavesTheSkeleton()
    {
        // A verified claim alone is no plan: without a chapter there is nothing to write.
        var synthesizer = Build(new AnalysisOptions { MaxOverviewTokens = 100 },
            Pass(Call("ProposeClaim", Assessment("early"), tokens: 500), Call("ProposeChapter", Chapter("Never", "")), Done()));

        var model = await Run(synthesizer);

        Assert.Empty(model.Outline);
        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("early"));
        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-outline");
        Assert.Equal("The overview pass did not finish (token budget reached); no chapters were written.", gap.Reason);
    }

    [Fact]
    public async Task AFailedOverviewCall_DropsTheChaptersItHadPlanned()
    {
        var synthesizer = Build(null,
            FailingPass(new HttpRequestException("model endpoint down"), Call("ProposeChapter", Chapter("System map", "system-map"))));

        var model = await Run(synthesizer);

        Assert.Empty(model.Outline);
        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-outline");
        Assert.Contains("HttpRequestException", gap.Reason);
    }

    [Fact]
    public async Task TheOverviewUsesItsOwnBudget_NotTheChapterBudget()
    {
        var synthesizer = Build(new AnalysisOptions { MaxChapterTokens = 100 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map"), tokens: 200), Call("ProposeChapter", Chapter("Risks", "risks-and-debt"), tokens: 200), Done(100)),
            Pass(Done()),
            Pass(Done()));

        var model = await Run(synthesizer);

        Assert.Equal(2, model.Outline.Count);
        Assert.DoesNotContain(model.Coverage.NotAnalyzed, g => g.Area == "manual-outline");
    }

    [Fact]
    public async Task AFailedOverview_StillLogsThePassTelemetry()
    {
        var logger = new RecordingLogger();
        var synthesizer = Build(null, logger,
            FailingPass(new HttpRequestException("model endpoint down"), Call("ProposeChapter", Chapter("", "")), Call("ProposeChapter", Chapter("System map", "system-map"), tokens: 30)));

        await Run(synthesizer);

        Assert.Contains(logger.Messages, m => m == "Analysis passes planned 0 chapter(s) and added 0 claim(s); 1 proposal(s) rejected, 40 token(s) used");
    }

    [Fact]
    public async Task EveryPass_LogsItsStartAndHowItEnded_WithToolCallsByTool()
    {
        var logger = new RecordingLogger();
        var synthesizer = Build(new AnalysisOptions { MaxChapterTokens = 1_000 }, logger,
            Pass(Call("ProposeChapter", Chapter("System map", "system-map"), tokens: 20), Done(5)),
            Pass(Call("ProposeClaim", Assessment("retries"), tokens: 30), Call("AddBlock", new() { ["kind"] = OutlineBlockKind.Map, ["reference"] = "cmp.nope" }), Done(5)));

        await Run(synthesizer);

        Assert.Contains(logger.Messages, m => m.StartsWith("Analysis pass overview started", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.StartsWith("Analysis pass overview finished after", StringComparison.Ordinal)
            && m.EndsWith("25 token(s); tool calls: ProposeChapter 1; 0 proposal(s) rejected; 0 reminder(s) to propose", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.StartsWith("Analysis pass system-map.md started", StringComparison.Ordinal) && m.EndsWith("1000 token(s) at most", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.StartsWith("Analysis pass system-map.md finished after", StringComparison.Ordinal)
            && m.EndsWith("tool calls: AddBlock 1, ProposeClaim 1; 1 proposal(s) rejected; 0 reminder(s) to propose", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectionsSentTogetherInOneRound_AreNotCalledARepeat_ButTheNextRoundIs()
    {
        static FunctionCallContent Unlocated(string topic) => new(Guid.NewGuid().ToString("N"), "ProposeClaim", new Dictionary<string, object?>
        {
            ["topic"] = topic,
            ["tier"] = ClaimTier.Fact,
            ["statement"] = "A statement.",
            ["confidence"] = ClaimConfidence.High,
            ["evidence"] = new[] { new ProposedEvidence(ProposedEvidenceKind.Code, Path: "a.cs") },
        });
        var overview = new FakeChatClient(
            [
                new ChatResponse(new ChatMessage(ChatRole.Assistant, [Unlocated("one"), Unlocated("two")])),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, [Unlocated("three")])),
            ],
            Done());
        var synthesizer = Build(null, overview);

        await Run(synthesizer);

        var answers = overview.LastMessages!.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.Result?.ToString() ?? "").ToList();
        Assert.Equal(3, answers.Count);
        Assert.DoesNotContain("in a row", answers[0], StringComparison.Ordinal);
        Assert.DoesNotContain("in a row", answers[1], StringComparison.Ordinal);
        Assert.Contains("rejection 2 in a row", answers[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChapterPass_IsToldItsShareOfTimeAndTokens_AndToProposeAsItReads()
    {
        var chapter = Pass(Done());
        var synthesizer = Build(new AnalysisOptions { MaxDuration = TimeSpan.FromMinutes(10), MaxChapterTokens = 250_000 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Call("ProposeChapter", Chapter("Risks", "risks-and-debt")), Done()),
            chapter,
            Pass(Done()));

        await Run(synthesizer);

        var prompt = Assert.Single(chapter.LastMessages!, m => m.Role == ChatRole.User).Text;
        Assert.Contains("This chapter has about 5 minute(s) and 250,000 tokens.", prompt, StringComparison.Ordinal);
        Assert.Contains("propose the claims they support before you read further", prompt, StringComparison.Ordinal);
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
    public async Task AChapterOverItsTokenBudget_KeepsWhatWasAlreadyVerified_AsAPartialChapter()
    {
        var synthesizer = Build(new AnalysisOptions { MaxChapterTokens = 100 },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Done()),
            Pass(Call("ProposeClaim", Assessment("expensive"), tokens: 500), Call("ProposeClaim", Assessment("never")), Done()));

        var model = await Run(synthesizer);

        Assert.Contains(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("expensive"));
        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("never"));
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason == "system-map.md: partial — token budget reached");
        Assert.Equal([(OutlineBlockKind.Claim, ModelIds.SynthesizedClaim("expensive"))], model.Outline[0].Blocks.Select(b => (b.Kind, b.Ref)));
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

        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason == "system-map.md: time budget reached");
        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("slow"));
    }

    [Fact]
    public async Task EachChapter_GetsItsShareOfTheRemainingTime()
    {
        // Two chapters, 2s in total: the first may not take the whole deadline — it is cut at
        // its share, and the second still runs.
        var second = Pass(Call("ProposeClaim", Assessment("second")), Done());
        var synthesizer = Build(new AnalysisOptions { MaxDuration = TimeSpan.FromSeconds(2) },
            Pass(Call("ProposeChapter", Chapter("System map", "system-map")), Call("ProposeChapter", Chapter("Risks", "risks-and-debt")), Done()),
            new FakeChatClient([], Done()) { Delay = TimeSpan.FromSeconds(10) },
            second);

        var model = await Run(synthesizer);

        Assert.Contains(model.Claims, c => c.Key == ModelIds.SynthesizedClaim("second"));
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Reason == "system-map.md: time budget reached");
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
