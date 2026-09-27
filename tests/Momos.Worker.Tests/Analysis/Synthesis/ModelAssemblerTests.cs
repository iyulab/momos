using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class ModelAssemblerTests
{
    private static readonly CoverageGeneratorPayload Generator = new("test-model", "p3.1");

    private static ClaimPayload Assessment(string key, string cites) => new(key, ClaimTier.Assessment, "An interpretation.",
        [new EvidencePayload(EvidenceKind.Claim, ClaimKey: cites)], ClaimConfidence.Low, ClaimOrigin.Synthesized);

    [Fact]
    public void WithNothingSynthesized_TheSkeletonComesBackWithOnlyTheGapsAdded()
    {
        var skeleton = SynthesisDraftTests.Skeleton();
        var gap = new CoverageGapPayload("manual-outline", "The overview pass did not finish (time budget reached); no chapters were written.");

        var model = ModelAssembler.Assemble(new SynthesisDraft(skeleton), [gap], Generator, chaptersPlanned: 0);

        Assert.Equal(skeleton.Claims, model.Claims);
        Assert.Equal(skeleton.Components.Select(c => (c.Id, c.Responsibility)), model.Components.Select(c => (c.Id, c.Responsibility)));
        Assert.Empty(model.Outline);
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Area == "source-files");
        Assert.Equal(gap, model.Coverage.NotAnalyzed[^1]);
        Assert.Equal(Generator, model.Coverage.Generator);
    }

    [Fact]
    public void AWrittenChapter_ReplacesTheSourceFilesGapWithWhatWasRead()
    {
        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var overview = draft.Stage(null);
        var section = overview.AddSection("System map", "The parts.", "system-map");
        overview.Commit();
        var chapter = draft.Stage(section);
        chapter.AddClaim(Assessment("clm.one", "clm.fact"));
        chapter.AddBlock(new OutlineBlockPayload(OutlineBlockKind.Map, "*"));
        chapter.SetOwnerSummary(["clm.one"]);
        chapter.Commit();

        var model = ModelAssembler.Assemble(draft, [], Generator, chaptersPlanned: 1);

        Assert.DoesNotContain(model.Coverage.NotAnalyzed, g => g.Area == "source-files");
        var synthesis = Assert.Single(model.Coverage.Analyzed, a => a.Area == "manual-synthesis");
        Assert.StartsWith("1 of 1 chapters", synthesis.Detail);
        var outline = Assert.Single(model.Outline);
        Assert.Equal("system-map.md", outline.Path);
        Assert.Equal(["clm.one"], outline.OwnerSummaryClaims);
        Assert.Contains(model.Claims, c => c.Key == "clm.one");
    }

    [Fact]
    public void AComponentDescription_MergesIntoTheDeterministicComponent()
    {
        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var stage = draft.Stage(null);
        stage.AddClaim(Assessment("clm.one", "clm.fact"));
        stage.DescribeComponent("cmp.app", "Serves the public API.", ["clm.one"]);
        stage.Commit();

        var component = Assert.Single(ModelAssembler.Assemble(draft, [], Generator, 0).Components);

        Assert.Equal("Serves the public API.", component.Responsibility);
        Assert.Equal(["clm.fact", "clm.one"], component.Claims);
    }

    [Fact]
    public void AnythingLeftUnresolved_IsPrunedInCascadeAndCounted()
    {
        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        var stage = draft.Stage(null);
        // A defensive case the tools never produce: a claim citing one that is not there.
        stage.AddClaim(Assessment("clm.orphan", "clm.gone"));
        stage.AddClaim(Assessment("clm.child", "clm.orphan"));
        stage.AddInvariant(new InvariantPayload("inv.x", "Holds.", "contract", [], ["clm.child"]));
        stage.Commit();

        var model = ModelAssembler.Assemble(draft, [], Generator, 0);

        Assert.DoesNotContain(model.Claims, c => c.Key is "clm.orphan" or "clm.child");
        Assert.Empty(model.Invariants);
        Assert.Equal(3, Assert.Single(model.Coverage.Rejected, r => r.Reason == RejectionReason.UnresolvedReference).Count);
    }

    [Fact]
    public void RejectionsBecomeCoverage_SortedByReason()
    {
        var draft = new SynthesisDraft(SynthesisDraftTests.Skeleton());
        draft.Reject(RejectionReason.TierRule);
        draft.Reject(RejectionReason.EvidenceNotFound);
        draft.Reject(RejectionReason.EvidenceNotFound);

        var rejected = ModelAssembler.Assemble(draft, [], Generator, 0).Coverage.Rejected;

        Assert.Equal([(RejectionReason.EvidenceNotFound, 2), (RejectionReason.TierRule, 1)], rejected.Select(r => (r.Reason, r.Count)));
    }

    [Fact]
    public void CoverageText_IsKeptWithinTheHostsLimit()
    {
        var gap = new CoverageGapPayload("manual-chapter", new string('x', 500));

        var model = ModelAssembler.Assemble(new SynthesisDraft(SynthesisDraftTests.Skeleton()), [gap], Generator, 0);

        Assert.All(model.Coverage.NotAnalyzed, g => Assert.True(g.Reason.Length <= 200));
    }
}
