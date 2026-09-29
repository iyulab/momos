using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class SynthesisDraftTests
{
    /// <summary>A one-component deterministic model: component <c>cmp.app</c> backed by fact <c>clm.fact</c>.</summary>
    internal static ProjectModelPayload Skeleton() => new(
        "abc123",
        [new ComponentPayload("cmp.app", "App", "web-service", null, ["clm.fact"])],
        [], [], [], [], [], [], [],
        new CoveragePayload([], [new CoverageGapPayload("source-files", "Only project files and git history are read; source code is not analyzed.")], [], null),
        [new ClaimPayload("clm.fact", ClaimTier.Fact, "App is a web service.",
            [new EvidencePayload(EvidenceKind.Code, Path: "src/App/App.csproj", Symbol: "Microsoft.NET.Sdk.Web")], ClaimConfidence.High, ClaimOrigin.Deterministic)]);

    private static ClaimPayload Claim(string key) => new(key, ClaimTier.Assessment, "Reads as a thin host.",
        [new EvidencePayload(EvidenceKind.Claim, ClaimKey: "clm.fact")], ClaimConfidence.Low, ClaimOrigin.Synthesized);

    [Fact]
    public void AStagedClaim_IsVisibleToItsStageButNotToTheDraftUntilCommitted()
    {
        var draft = new SynthesisDraft(Skeleton());
        var stage = draft.Stage(section: null);

        stage.AddClaim(Claim("clm.one"));

        Assert.NotNull(stage.FindClaim("clm.one"));
        Assert.Null(draft.FindClaim("clm.one"));
        stage.Commit();
        Assert.NotNull(draft.FindClaim("clm.one"));
    }

    [Fact]
    public void AnAbandonedStage_LeavesNoTrace()
    {
        var draft = new SynthesisDraft(Skeleton());
        var overview = draft.Stage(null);
        var section = overview.AddSection("Risks", "What could go wrong.", guide: null);
        overview.Commit();
        var stage = draft.Stage(section);

        stage.AddClaim(Claim("clm.two"));
        stage.AddBlock(new OutlineBlockPayload(OutlineBlockKind.Claim, "clm.two"));

        Assert.Null(draft.FindClaim("clm.two"));
        Assert.Empty(section.Blocks);
    }

    [Fact]
    public void AChapterCommit_PlacesWhatItProposedButDidNotPlace_InTheOrderItWasProposed()
    {
        // A chapter cut by its budget usually proposed claims but never reached its placing step;
        // unplaced, the committed claims belong to no chapter and the chapter reads as empty.
        var draft = new SynthesisDraft(Skeleton());
        var overview = draft.Stage(null);
        var section = overview.AddSection("Risks", "What could go wrong.", guide: null);
        overview.Commit();
        var stage = draft.Stage(section);

        stage.AddClaim(Claim("clm.one"));
        stage.AddInvariant(new InvariantPayload("inv.x", "Never twice.", "concurrency", [], ["clm.one"]));
        stage.AddClaim(Claim("clm.two"));
        stage.AddBlock(new OutlineBlockPayload(OutlineBlockKind.Claim, "clm.two"));
        stage.AddBlock(new OutlineBlockPayload(OutlineBlockKind.Component, "cmp.app"));
        stage.Commit();

        Assert.Equal(
            [(OutlineBlockKind.Claim, "clm.two"), (OutlineBlockKind.Component, "cmp.app"), (OutlineBlockKind.Claim, "clm.one"), (OutlineBlockKind.Invariant, "inv.x")],
            section.Blocks.Select(b => (b.Kind, b.Ref)));
    }

    [Fact]
    public void AnOverviewCommit_PlacesNothing()
    {
        var draft = new SynthesisDraft(Skeleton());
        var overview = draft.Stage(null);
        var section = overview.AddSection("Risks", "What could go wrong.", guide: null);
        overview.AddClaim(Claim("clm.goal"));

        overview.Commit();

        Assert.Empty(section.Blocks);
    }

    [Fact]
    public void ASectionAddedInTheOverview_ExistsOnlyAfterTheOverviewCommits()
    {
        var draft = new SynthesisDraft(Skeleton());
        var overview = draft.Stage(null);

        var section = overview.AddSection("System map", "The parts and how they connect.", guide: "system-map");

        Assert.Equal("system-map.md", section.Path);
        Assert.Equal(ModelIds.Section("system-map.md"), section.Id);
        Assert.Empty(draft.Sections);
        overview.Commit();
        Assert.Same(section, Assert.Single(draft.Sections));
    }

    [Fact]
    public void DescribingAComponent_AppendsClaimsAndSetsTheResponsibilityOnCommit()
    {
        var draft = new SynthesisDraft(Skeleton());
        var stage = draft.Stage(null);
        stage.AddClaim(Claim("clm.one"));

        stage.DescribeComponent("cmp.app", "Serves the public API.", ["clm.one"]);
        stage.Commit();

        var update = Assert.Single(draft.ComponentUpdates);
        Assert.Equal(("cmp.app", "Serves the public API."), (update.Key, update.Value.Responsibility));
        Assert.Equal(["clm.one"], update.Value.Claims);
    }

    [Fact]
    public void RejectionsAreCountedByReason()
    {
        var draft = new SynthesisDraft(Skeleton());

        draft.Reject(RejectionReason.EvidenceNotFound);
        draft.Reject(RejectionReason.EvidenceNotFound);
        draft.Reject(RejectionReason.TierRule);

        Assert.Equal(2, draft.Rejections[RejectionReason.EvidenceNotFound]);
        Assert.Equal(1, draft.Rejections[RejectionReason.TierRule]);
    }
}
