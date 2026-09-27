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
}
