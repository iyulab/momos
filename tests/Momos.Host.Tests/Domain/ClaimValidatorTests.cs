using Momos.Host.Domain;

namespace Momos.Host.Tests.Domain;

public sealed class ClaimValidatorTests
{
    private static readonly ModelElements NoElements = new([], [], [], [], []);

    private static ModelClaim Claim(string key, ClaimTier tier, ClaimConfidence confidence, params ClaimEvidence[] evidence) => new()
    {
        ProjectModelId = Guid.Empty,
        Key = key,
        Tier = tier,
        Statement = $"statement {key}",
        Evidence = [.. evidence],
        Confidence = confidence,
        Origin = ClaimOrigin.Deterministic,
    };

    private static readonly ClaimEvidence CodeAt = new(EvidenceKind.Code, Path: "src/App/App.csproj");

    [Fact]
    public void AFactWithCodeEvidence_IsValid() =>
        Assert.Empty(ClaimValidator.Validate([Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)], NoElements));

    [Fact]
    public void AnyClaimWithNoEvidence_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate([Claim("clm.a", ClaimTier.Assessment, ClaimConfidence.Low)], NoElements));

    [Fact]
    public void AFactWhoseOnlyEvidenceIsACommit_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, new ClaimEvidence(EvidenceKind.Commit, Sha: "abc"))], NoElements));

    [Fact]
    public void ACodeEvidenceWithoutAPath_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, new ClaimEvidence(EvidenceKind.Code))], NoElements));

    [Fact]
    public void AHistoryClaimWithACommit_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
            [Claim("clm.h", ClaimTier.History, ClaimConfidence.Medium, new ClaimEvidence(EvidenceKind.Commit, Sha: "abc"))], NoElements));

    [Fact]
    public void AnAssessmentReferencingAnExistingClaimAtLowConfidence_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
        [
            Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt),
            Claim("clm.x", ClaimTier.Assessment, ClaimConfidence.Low, new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.f")),
        ], NoElements));

    [Fact]
    public void AnAssessmentWithoutAClaimReference_IsRejectedEvenWithCodeEvidence() =>
        Assert.NotEmpty(ClaimValidator.Validate([Claim("clm.x", ClaimTier.Assessment, ClaimConfidence.Low, CodeAt)], NoElements));

    [Fact]
    public void AnAssessmentReferencingAMissingClaim_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.x", ClaimTier.Assessment, ClaimConfidence.Low, new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.nope"))], NoElements));

    [Fact]
    public void AnAssessmentAboveLowWithoutAReproducedFinding_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
        [
            Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt),
            Claim("clm.x", ClaimTier.Assessment, ClaimConfidence.Medium, new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.f")),
        ], NoElements));

    [Fact]
    public void AnAssessmentAboveLowWithAReproducedFinding_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
        [
            Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt),
            Claim("clm.x", ClaimTier.Assessment, ClaimConfidence.High,
                new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.f"),
                new ClaimEvidence(EvidenceKind.Finding, InspectionRequestId: Guid.NewGuid())),
        ], NoElements));

    [Fact]
    public void DuplicateKeys_AreRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt), Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)], NoElements));

    [Fact]
    public void AComponentWithNoClaims_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate([], new ModelElements([new ModelComponent("cmp.a", "A", "library", null, [])], [], [], [], [])));

    [Fact]
    public void AnElementReferencingAMissingClaim_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate([], new ModelElements([new ModelComponent("cmp.a", "A", "library", null, ["clm.nope"])], [], [], [], [])));

    [Fact]
    public void ARelationToAMissingComponent_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            new ModelElements([new ModelComponent("cmp.a", "A", "library", null, ["clm.a"])], [new ModelRelation("cmp.a", "cmp.b", "references", ["clm.a"])], [], [], [])));

    public static TheoryData<ClaimEvidence> EvidenceMissingItsIdentifier() => new()
    {
        new ClaimEvidence(EvidenceKind.Commit),
        new ClaimEvidence(EvidenceKind.Commit, Sha: " "),
        new ClaimEvidence(EvidenceKind.PullRequest),
        new ClaimEvidence(EvidenceKind.Issue),
        new ClaimEvidence(EvidenceKind.Finding),
    };

    [Theory]
    [MemberData(nameof(EvidenceMissingItsIdentifier))]
    public void EvidenceWithoutTheIdentifierItsKindNeeds_IsRejected(ClaimEvidence evidence) =>
        Assert.Contains(
            ClaimValidator.Validate([Claim("clm.h", ClaimTier.History, ClaimConfidence.Medium, new ClaimEvidence(EvidenceKind.Commit, Sha: "abc"), evidence)], NoElements),
            e => e.Contains("clm.h", StringComparison.Ordinal) && e.Contains(evidence.Kind.ToString(), StringComparison.Ordinal));

    [Fact]
    public void AFactOrHistoryClaimReferencingAMissingClaim_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt, new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.nope"))], NoElements));

    [Fact]
    public void AFactReferencingAnotherClaimInTheModel_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
        [
            Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt),
            Claim("clm.b", ClaimTier.Fact, ClaimConfidence.High, CodeAt, new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.a")),
        ], NoElements));

    [Fact]
    public void DuplicateElementIds_AreRejected()
    {
        var claims = new[] { Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt) };
        var errors = ClaimValidator.Validate(claims, new ModelElements(
            [new ModelComponent("cmp.a", "A", "library", null, ["clm.a"]), new ModelComponent("cmp.a", "A again", "library", null, ["clm.a"])],
            [],
            [new ModelPattern("pat.a", "P", ["cmp.a"], ["clm.a"]), new ModelPattern("pat.a", "P", ["cmp.a"], ["clm.a"])],
            [new ModelDecision("dec.a", "D", [], ModelDecision.Unrecorded, ["clm.a"]), new ModelDecision("dec.a", "D", [], ModelDecision.Unrecorded, ["clm.a"])],
            [new ModelIntent("int.a", "I", IntentSource.Inferred, ["clm.a"]), new ModelIntent("int.a", "I", IntentSource.Inferred, ["clm.a"])]));

        foreach (var id in new[] { "cmp.a", "pat.a", "dec.a", "int.a" })
        {
            Assert.Contains(errors, e => e.Contains($"'{id}'", StringComparison.Ordinal) && e.Contains("more than once", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void APatternApplyingToAMissingComponent_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            new ModelElements([new ModelComponent("cmp.a", "A", "library", null, ["clm.a"])], [], [new ModelPattern("pat.a", "P", ["cmp.a", "cmp.b"], ["clm.a"])], [], [])));

    [Fact]
    public void AnIntentFromAWorkerClaimingDeveloperSource_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            new ModelElements([], [], [], [], [new ModelIntent("int.a", "x", IntentSource.Developer, ["clm.a"])])));

    private static ClaimEvidence Cites(string key) => new(EvidenceKind.Claim, ClaimKey: key);

    [Fact]
    public void AClaimMoreCertainThanTheClaimItCites_IsRejected() =>
        Assert.Contains(ClaimValidator.Validate(
        [
            Claim("clm.f", ClaimTier.Fact, ClaimConfidence.Medium, CodeAt),
            Claim("clm.sum", ClaimTier.Fact, ClaimConfidence.High, CodeAt, Cites("clm.f")),
        ], NoElements), e => e.Contains("clm.sum", StringComparison.Ordinal) && e.Contains("cannot be more certain", StringComparison.Ordinal));

    [Fact]
    public void AClaimAsCertainAsTheLeastCertainClaimItCites_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
        [
            Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt),
            Claim("clm.g", ClaimTier.Fact, ClaimConfidence.Medium, CodeAt),
            Claim("clm.sum", ClaimTier.Fact, ClaimConfidence.Medium, CodeAt, Cites("clm.f"), Cites("clm.g")),
        ], NoElements));

    [Fact]
    public void ClaimsCitingEachOtherInACircle_AreRejected() =>
        Assert.Contains(ClaimValidator.Validate(
        [
            Claim("clm.a", ClaimTier.Fact, ClaimConfidence.Low, CodeAt, Cites("clm.b")),
            Claim("clm.b", ClaimTier.Fact, ClaimConfidence.Low, CodeAt, Cites("clm.c")),
            Claim("clm.c", ClaimTier.Fact, ClaimConfidence.Low, CodeAt, Cites("clm.a")),
        ], NoElements), e => e.Contains("cycle", StringComparison.Ordinal) && e.Contains("clm.a", StringComparison.Ordinal));

    [Fact]
    public void AVeryLongCitationChain_IsCheckedWithoutOverflowingTheStack()
    {
        const int length = 5000;
        var claims = Enumerable.Range(0, length)
            .Select(i => i == length - 1
                ? Claim($"clm.{i}", ClaimTier.Fact, ClaimConfidence.Low, CodeAt)
                : Claim($"clm.{i}", ClaimTier.Fact, ClaimConfidence.Low, CodeAt, Cites($"clm.{i + 1}")))
            .ToList();

        Assert.Empty(ClaimValidator.Validate(claims, NoElements));
    }

    [Fact]
    public void DuplicateKeysWithCitations_AreReportedWithoutThrowing()
    {
        var errors = ClaimValidator.Validate(
        [
            Claim("clm.a", ClaimTier.Fact, ClaimConfidence.Low, CodeAt),
            Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt, Cites("clm.b")),
            Claim("clm.b", ClaimTier.Fact, ClaimConfidence.Low, CodeAt, Cites("clm.a")),
        ], NoElements);

        Assert.Contains(errors, e => e.Contains("more than once", StringComparison.Ordinal));
    }

    private static ModelElements WithDecision(ModelDecision decision) => new([], [], [], [decision], []);

    [Fact]
    public void ADecisionWithARationaleButNoHistoryClaim_IsRejected() =>
        Assert.Contains(ClaimValidator.Validate(
            [Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            WithDecision(new ModelDecision("dec.a", "Split host and worker", [], "Workers sit behind firewalls", ["clm.f"]))),
            e => e.Contains("dec.a", StringComparison.Ordinal) && e.Contains("history", StringComparison.Ordinal));

    [Fact]
    public void ADecisionWithARationaleBackedByAHistoryClaim_IsValid() =>
        Assert.Empty(ClaimValidator.Validate(
            [Claim("clm.h", ClaimTier.History, ClaimConfidence.Medium, new ClaimEvidence(EvidenceKind.Commit, Sha: "abc"))],
            WithDecision(new ModelDecision("dec.a", "Split host and worker", [], "Workers sit behind firewalls", ["clm.h"]))));

    [Fact]
    public void AnUnrecordedDecision_NeedsNoHistoryClaim() =>
        Assert.Empty(ClaimValidator.Validate(
            [Claim("clm.f", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            WithDecision(new ModelDecision("dec.a", "Split host and worker", [], ModelDecision.Unrecorded, ["clm.f"]))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ADecisionWithABlankRationale_IsRejected(string rationale) =>
        Assert.Contains(ClaimValidator.Validate(
            [Claim("clm.h", ClaimTier.History, ClaimConfidence.Medium, new ClaimEvidence(EvidenceKind.Commit, Sha: "abc"))],
            WithDecision(new ModelDecision("dec.a", "Split host and worker", [], rationale, ["clm.h"]))),
            e => e.Contains("dec.a", StringComparison.Ordinal) && e.Contains("blank", StringComparison.Ordinal));
}
