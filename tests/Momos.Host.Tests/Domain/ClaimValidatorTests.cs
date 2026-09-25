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

    [Fact]
    public void AnIntentFromAWorkerClaimingDeveloperSource_IsRejected() =>
        Assert.NotEmpty(ClaimValidator.Validate(
            [Claim("clm.a", ClaimTier.Fact, ClaimConfidence.High, CodeAt)],
            new ModelElements([], [], [], [], [new ModelIntent("int.a", "x", IntentSource.Developer, ["clm.a"])])));
}
