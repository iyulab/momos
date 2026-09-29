using Microsoft.Extensions.AI;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class SynthesisToolsTests
{
    private const string Base = "abc123";

    private static readonly ProposedEvidence QueueCode = new(ProposedEvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "sealed class Queue");

    private static (SynthesisTools Tools, DraftStage Stage) Tools(DraftSection? section = null, SynthesisDraft? draft = null, int maxChapters = 12)
    {
        draft ??= new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var runtime = new FakeExecutionRuntimeProvider
        {
            Respond = command => command.Args switch
            {
                ["cat-file", "-t", "abc123:src/App/Queue.cs"] => new(true, "blob\n", null, 1),
                ["show", "abc123:src/App/Queue.cs"] => new(true, "public sealed class Queue { public void Claim() { } }\n", null, 1),
                _ => new(false, "", "exit 1", 1),
            },
        };
        var stage = draft.Stage(section);
        return (new SynthesisTools(stage, new EvidenceVerifier(runtime, new ExecutionSessionHandle("s"), Base), maxChapters), stage);
    }

    [Fact]
    public async Task AGroundedFact_IsRecordedUnderItsTopicKey()
    {
        var (tools, stage) = Tools();

        var answer = await tools.ProposeClaim("the queue type", ClaimTier.Fact, "Requests queue in the Queue type.", ClaimConfidence.High, [QueueCode], CancellationToken.None);

        var key = ModelIds.SynthesizedClaim("the queue type");
        Assert.Equal($"Recorded as {key}.", answer);
        var claim = stage.FindClaim(key)!;
        Assert.Equal(ClaimOrigin.Synthesized, claim.Origin);
        Assert.Equal("src/App/Queue.cs", Assert.Single(claim.Evidence).Path);
    }

    [Fact]
    public async Task AFabricatedCitation_IsRejectedAndCounted()
    {
        var (tools, stage) = Tools();

        var answer = await tools.ProposeClaim("made up", ClaimTier.Fact, "There is a cache.", ClaimConfidence.High,
            [new(ProposedEvidenceKind.Code, Path: "src/App/Cache.cs", Symbol: "Cache")], CancellationToken.None);

        Assert.StartsWith("Rejected: ", answer);
        Assert.Equal(0, stage.StagedClaimCount);
        Assert.Equal(1, stage.Draft.Rejections[RejectionReason.EvidenceNotFound]);
    }

    [Fact]
    public async Task CodeEvidenceWithoutASymbol_IsAnsweredWithAnExample()
    {
        var (tools, _) = Tools();

        var answer = await tools.ProposeClaim("no symbol", ClaimTier.Fact, "There is a queue.", ClaimConfidence.High,
            [new(ProposedEvidenceKind.Code, Path: "src/App/Queue.cs")], CancellationToken.None);

        Assert.StartsWith("Rejected: Evidence 1 (Code) has no symbol.", answer, StringComparison.Ordinal);
        Assert.EndsWith(SynthesisTools.CodeEvidenceExample, answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRejectionTwiceInARow_SaysToChangeOrDrop_AndARecordedProposalResetsIt()
    {
        var (tools, _) = Tools();
        Task<string> Fabricated(string topic) => tools.ProposeClaim(topic, ClaimTier.Fact, "There is a cache.", ClaimConfidence.High,
            [new(ProposedEvidenceKind.Code, Path: "src/App/Cache.cs", Symbol: "Cache")], CancellationToken.None);

        Assert.DoesNotContain("in a row", await Fabricated("one"), StringComparison.Ordinal);
        Assert.Contains("rejection 2 in a row for the same reason", await Fabricated("two"), StringComparison.Ordinal);
        Assert.Contains("rejection 3 in a row", await Fabricated("three"), StringComparison.Ordinal);

        await tools.ProposeClaim("the queue type", ClaimTier.Fact, "Requests queue in the Queue type.", ClaimConfidence.High, [QueueCode], CancellationToken.None);
        Assert.DoesNotContain("in a row", await Fabricated("four"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectionThatWouldQuoteTheRepository_LogsItsCauseInstead()
    {
        var logger = new ListLogger();
        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var runtime = new FakeExecutionRuntimeProvider { Respond = _ => new(false, "", "exit 1", 1) };
        var tools = new SynthesisTools(draft.Stage(null), new EvidenceVerifier(runtime, new ExecutionSessionHandle("s"), Base), 12, logger);

        await tools.ProposeClaim("made up", ClaimTier.Fact, "There is a cache.", ClaimConfidence.High,
            [new(ProposedEvidenceKind.Code, Path: "src/App/Cache.cs", Symbol: "SecretCache")], CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.EndsWith("no file at the analyzed commit", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache", line, StringComparison.Ordinal);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ARepeatedTopic_IsRejected()
    {
        var (tools, _) = Tools();
        await tools.ProposeClaim("queue", ClaimTier.Fact, "One.", ClaimConfidence.High, [QueueCode], CancellationToken.None);

        var answer = await tools.ProposeClaim("Queue", ClaimTier.Fact, "Two.", ClaimConfidence.High, [QueueCode], CancellationToken.None);

        Assert.StartsWith("Rejected: ", answer);
    }

    [Fact]
    public async Task AnAssessmentAboveLow_IsRejectedWithTheReason()
    {
        var (tools, _) = Tools();

        var answer = await tools.ProposeClaim("thin host", ClaimTier.Assessment, "App is a thin host.", ClaimConfidence.High,
            [new(ProposedEvidenceKind.Claim, ClaimKey: "clm.fact")], CancellationToken.None);

        Assert.Contains("Low confidence", answer);
    }

    [Fact]
    public void AChapterBeforeTheLimit_IsRecordedPlainly()
    {
        var (tools, _) = Tools(maxChapters: 2);

        Assert.Equal("Recorded as system-map.md.", tools.ProposeChapter("System map", "The parts.", "system-map"));
    }

    [Fact]
    public void TheChapterThatFillsThePlan_SaysThePlanIsFull_AndOneBeyondIsRejectedWithTheSameAdvice()
    {
        var (tools, _) = Tools(maxChapters: 1);

        var filled = tools.ProposeChapter("System map", "The parts.", "system-map");
        Assert.StartsWith("Recorded as system-map.md.", filled, StringComparison.Ordinal);
        Assert.Contains("plan is now full (1 of 1)", filled, StringComparison.Ordinal);

        var beyond = tools.ProposeChapter("Risks", "What could go wrong.", null);
        Assert.StartsWith("Rejected: ", beyond, StringComparison.Ordinal);
        Assert.Contains("plan is full", beyond, StringComparison.Ordinal);
        Assert.Contains("finish the overview", beyond, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongChapterTitleOrPurpose_IsRejected()
    {
        var (tools, _) = Tools();

        Assert.StartsWith("Rejected: ", tools.ProposeChapter(new string('t', 81), "p", null));
        Assert.StartsWith("Rejected: ", tools.ProposeChapter("Title", new string('p', 201), null));
    }

    [Fact]
    public void ADecisionWithAnInventedReason_IsRejected_ButUnrecordedIsFine()
    {
        var (tools, _) = Tools();

        Assert.StartsWith("Rejected: ", tools.ProposeDecision("queue in db", "Queue lives in the database.", ["a broker"], "Brokers were too costly.", ["clm.fact"]));
        Assert.Equal($"Recorded as {ModelIds.Element(ModelIds.DecisionPrefix, "queue in db 2")}.",
            tools.ProposeDecision("queue in db 2", "Queue lives in the database.", ["a broker"], "unrecorded", ["clm.fact"]));
    }

    [Fact]
    public void AnIntentClaimingADeveloperSource_IsRejected()
    {
        var (tools, _) = Tools();

        Assert.StartsWith("Rejected: ", tools.ProposeIntent("goal", "Ship safely.", IntentSource.Developer, ["clm.fact"]));
    }

    [Fact]
    public void AFlowStep_MustResolve()
    {
        var (tools, _) = Tools();

        Assert.StartsWith("Rejected: ", tools.ProposeFlow("claim", "Claiming work", [new("cmp.nope", "clm.fact")], ["clm.fact"]));
        Assert.StartsWith("Rejected: ", tools.ProposeFlow("claim", "Claiming work", [], ["clm.fact"]));
        Assert.StartsWith("Recorded as flw.", tools.ProposeFlow("claim", "Claiming work", [new("cmp.app", "clm.fact")], ["clm.fact"]));
    }

    [Fact]
    public void AnInvariant_NeedsAKindAndKnownComponents()
    {
        var (tools, _) = Tools();

        Assert.StartsWith("Rejected: ", tools.ProposeInvariant("state", "Only Running completes.", " ", ["cmp.app"], ["clm.fact"]));
        Assert.StartsWith("Rejected: ", tools.ProposeInvariant("state", "Only Running completes.", "state-machine", ["cmp.nope"], ["clm.fact"]));
        Assert.StartsWith("Recorded as inv.", tools.ProposeInvariant("state", "Only Running completes.", "state-machine", ["cmp.app"], ["clm.fact"]));
    }

    [Fact]
    public void BlocksAndOwnerSummaries_BelongToAChapterPass()
    {
        var (overview, _) = Tools();
        Assert.StartsWith("Rejected: ", overview.AddBlock(OutlineBlockKind.Map, "*"));

        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var open = draft.Stage(null);
        var section = open.AddSection("System map", "The parts.", "system-map");
        open.Commit();
        var (chapter, stage) = Tools(section, draft);

        Assert.Equal("Recorded as *.", chapter.AddBlock(OutlineBlockKind.Map, "*"));
        Assert.StartsWith("Rejected: ", chapter.AddBlock(OutlineBlockKind.Flow, "flw.nope"));
        Assert.Equal("Recorded as system-map.md.", chapter.SetOwnerSummary(["clm.fact"]));
        stage.Commit();
        Assert.Single(section.Blocks);
        Assert.Equal(["clm.fact"], section.OwnerSummaryClaims);
    }

    [Fact]
    public void TheOverviewAndChapterToolSets_Differ()
    {
        var (tools, _) = Tools();

        Assert.Equal(["ProposeChapter", "ProposeClaim", "ProposeIntent"], tools.ForOverview().Select(f => f.Name).Order());
        Assert.DoesNotContain("ProposeChapter", tools.ForChapter().Select(f => f.Name));
        Assert.Contains("AddBlock", tools.ForChapter().Select(f => f.Name));
    }

    [Fact]
    public async Task Tools_AcceptEnumArgumentsAsNames()
    {
        var (tools, stage) = Tools();
        var propose = tools.ForOverview().Single(f => f.Name == "ProposeClaim");

        var answer = await propose.InvokeAsync(new AIFunctionArguments
        {
            ["topic"] = "queue by name",
            ["tier"] = "Fact",
            ["statement"] = "Requests queue in the Queue type.",
            ["confidence"] = "High",
            ["evidence"] = new[] { new Dictionary<string, object?> { ["kind"] = "Code", ["path"] = "src/App/Queue.cs", ["symbol"] = "Queue" } },
        });

        Assert.Equal($"Recorded as {ModelIds.SynthesizedClaim("queue by name")}.", answer?.ToString());
        Assert.Equal(1, stage.StagedClaimCount);
    }
}
