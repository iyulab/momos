using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

/// <summary>
/// The Worker's copy of the Host validator's rules, for what a language model proposes: a
/// proposal the Host would reject must be refused here, or one bad claim would fail the whole
/// analysis.
/// </summary>
public sealed class DraftRulesTests
{
    private static readonly ClaimPayload Fact = new("clm.fact", ClaimTier.Fact, "App is a web service.",
        [new EvidencePayload(EvidenceKind.Code, Path: "src/App/App.csproj", Symbol: "Microsoft.NET.Sdk.Web")], ClaimConfidence.High, ClaimOrigin.Deterministic);

    private static readonly ClaimPayload History = new("clm.hist", ClaimTier.History, "The queue moved to the database.",
        [new EvidencePayload(EvidenceKind.Commit, Sha: "abc123")], ClaimConfidence.Medium, ClaimOrigin.Synthesized);

    private static ClaimPayload? Resolve(string key) => key switch { "clm.fact" => Fact, "clm.hist" => History, _ => null };

    private static ClaimPayload Proposed(ClaimTier tier, ClaimConfidence confidence, params EvidencePayload[] evidence) =>
        new("clm.new", tier, "A statement.", evidence, confidence, ClaimOrigin.Synthesized);

    private static EvidencePayload Code() => new(EvidenceKind.Code, Path: "a.cs", Symbol: "A");

    private static EvidencePayload Cites(string key) => new(EvidenceKind.Claim, ClaimKey: key);

    public static TheoryData<string, ClaimPayload, string?> Claims => new()
    {
        { "fact with code", Proposed(ClaimTier.Fact, ClaimConfidence.High, Code()), null },
        { "no evidence", Proposed(ClaimTier.Fact, ClaimConfidence.High), RejectionReason.TierRule },
        { "fact without code", Proposed(ClaimTier.Fact, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Commit, Sha: "abc123")), RejectionReason.TierRule },
        { "code without path", Proposed(ClaimTier.Fact, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Code, Symbol: "A")), RejectionReason.MissingLocator },
        { "code without symbol", Proposed(ClaimTier.Fact, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Code, Path: "a.cs")), RejectionReason.MissingLocator },
        { "history with commit", Proposed(ClaimTier.History, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Commit, Sha: "abc123")), null },
        { "history with issue", Proposed(ClaimTier.History, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Issue, Url: "https://example.invalid/i/1")), null },
        { "history with code only", Proposed(ClaimTier.History, ClaimConfidence.High, Code()), RejectionReason.TierRule },
        { "commit without sha", Proposed(ClaimTier.History, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Commit)), RejectionReason.MissingLocator },
        { "assessment citing a claim, low", Proposed(ClaimTier.Assessment, ClaimConfidence.Low, Cites("clm.fact")), null },
        { "assessment above low", Proposed(ClaimTier.Assessment, ClaimConfidence.Medium, Cites("clm.fact")), RejectionReason.TierRule },
        { "assessment citing nothing", Proposed(ClaimTier.Assessment, ClaimConfidence.Low, Code()), RejectionReason.TierRule },
        { "assessment citing an unknown claim", Proposed(ClaimTier.Assessment, ClaimConfidence.Low, Cites("clm.nope")), RejectionReason.UnresolvedReference },
        { "fact citing an unknown claim too", Proposed(ClaimTier.Fact, ClaimConfidence.High, Code(), Cites("clm.nope")), RejectionReason.UnresolvedReference },
        { "self citation", Proposed(ClaimTier.Assessment, ClaimConfidence.Low, Cites("clm.new")), RejectionReason.UnresolvedReference },
        { "finding evidence", Proposed(ClaimTier.Assessment, ClaimConfidence.Low, Cites("clm.fact"), new EvidencePayload(EvidenceKind.Finding, InspectionRequestId: Guid.NewGuid())), RejectionReason.Unsupported },
        { "deterministic origin from a model", Proposed(ClaimTier.Fact, ClaimConfidence.High, Code()) with { Origin = ClaimOrigin.Deterministic }, RejectionReason.Unsupported },
        { "blank statement", Proposed(ClaimTier.Fact, ClaimConfidence.High, Code()) with { Statement = "  " }, RejectionReason.InvalidElement },
    };

    [Theory]
    [MemberData(nameof(Claims))]
    public void ClaimRules(string because, ClaimPayload claim, string? expected)
    {
        var verdict = DraftRules.CheckClaim(claim, Resolve);

        Assert.True(expected == verdict.Reason, $"{because}: expected {expected ?? "accept"}, got {verdict.Reason ?? "accept"} ({verdict.Message})");
    }

    [Theory]
    [InlineData("a.cs", null, "Evidence 2 (Code) has no symbol")]
    [InlineData(null, "A", "Evidence 2 (Code) has no path")]
    [InlineData(" ", "", "Evidence 2 (Code) has no path and no symbol")]
    public void AnUnlocatedPieceOfEvidence_IsNamedWithTheFieldItLacks(string? path, string? symbol, string expected)
    {
        var claim = Proposed(ClaimTier.Fact, ClaimConfidence.High, Code(), new EvidencePayload(EvidenceKind.Code, Path: path, Symbol: symbol));

        var verdict = DraftRules.CheckClaim(claim, Resolve);

        Assert.Equal(RejectionReason.MissingLocator, verdict.Reason);
        Assert.StartsWith(expected, verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlocatedCommit_SaysItNeedsASha() =>
        Assert.StartsWith("Evidence 1 (Commit) has no sha",
            DraftRules.CheckClaim(Proposed(ClaimTier.History, ClaimConfidence.High, new EvidencePayload(EvidenceKind.Commit)), Resolve).Message,
            StringComparison.Ordinal);

    [Fact]
    public void AClaimBuiltOnAWeakerOne_IsCappedAtItsConfidence()
    {
        var claim = Proposed(ClaimTier.Fact, ClaimConfidence.High, Code(), Cites("clm.hist"));

        Assert.Equal(ClaimConfidence.Medium, DraftRules.CapConfidence(claim, Resolve).Confidence);
    }

    [Theory]
    [InlineData("unrecorded", "clm.fact", null)]
    [InlineData("Because the queue had to survive restarts.", "clm.hist", null)]
    [InlineData("Because the queue had to survive restarts.", "clm.fact", RejectionReason.InvalidElement)]
    [InlineData(" ", "clm.fact", RejectionReason.InvalidElement)]
    [InlineData("unrecorded", "", RejectionReason.InvalidElement)]
    [InlineData("unrecorded", "clm.nope", RejectionReason.UnresolvedReference)]
    public void DecisionRules(string rationale, string claims, string? expected) =>
        Assert.Equal(expected, DraftRules.CheckDecision(rationale, claims.Split(',', StringSplitOptions.RemoveEmptyEntries), Resolve).Reason);

    [Theory]
    [InlineData(OutlineBlockKind.Map, "*", null)]
    [InlineData(OutlineBlockKind.Map, "cmp.app", null)]
    [InlineData(OutlineBlockKind.Map, "cmp.nope", RejectionReason.UnresolvedReference)]
    [InlineData(OutlineBlockKind.Claim, "clm.fact", null)]
    [InlineData(OutlineBlockKind.Flow, "flw.nope", RejectionReason.UnresolvedReference)]
    [InlineData((OutlineBlockKind)99, "clm.fact", RejectionReason.Unsupported)]
    public void BlockRules(OutlineBlockKind kind, string reference, string? expected) =>
        Assert.Equal(expected, DraftRules.CheckBlock(new OutlineBlockPayload(kind, reference),
            (k, r) => (k, r) is (OutlineBlockKind.Claim, "clm.fact") or (OutlineBlockKind.Component, "cmp.app")).Reason);
}
