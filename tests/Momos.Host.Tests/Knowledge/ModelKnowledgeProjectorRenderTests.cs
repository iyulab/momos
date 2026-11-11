using Momos.Host.Domain;
using Momos.Host.Knowledge;

namespace Momos.Host.Tests.Knowledge;

public sealed class ModelKnowledgeProjectorRenderTests
{
    [Fact]
    public void RenderedClaim_SaysHowItWasProduced()
    {
        var text = ModelKnowledgeProjector.Render(new ModelClaim
        {
            ProjectModelId = Guid.Empty,
            Key = "clm.x",
            Tier = ClaimTier.Assessment,
            Statement = "Looks layered",
            Evidence = [new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.a")],
            Confidence = ClaimConfidence.Low,
            Origin = ClaimOrigin.Synthesized,
        });

        Assert.Contains("synthesized", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACredentialInTheEvidence_IsNotCopiedIntoTheIndexedText()
    {
        var text = ModelKnowledgeProjector.Render(new ModelClaim
        {
            ProjectModelId = Guid.Empty,
            Key = "clm.x",
            Tier = ClaimTier.Fact,
            Statement = "Services share one database",
            Evidence = [new ClaimEvidence(EvidenceKind.Code, Path: "appsettings.json", Symbol: "Host=db;Username=app;Password=hunter2")],
            Confidence = ClaimConfidence.High,
            Origin = ClaimOrigin.Synthesized,
        });

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("Password=<REDACTED>", text, StringComparison.Ordinal);
    }
}
