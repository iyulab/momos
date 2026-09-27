using Momos.Host.Contracts;
using Momos.Host.Domain;

namespace Momos.Host.Tests;

internal static class ModelFixtures
{
    public static SubmitProjectModelRequest ValidSubmission(string baseCommit = "abc123", string componentStatement = "App is a .NET project") => new(
        baseCommit,
        Components:
        [
            new ModelComponentDto("cmp.app", "App", "executable", null, ["clm.app"]),
            new ModelComponentDto("cmp.lib", "Lib", "library", null, ["clm.lib"]),
        ],
        Relations: [new ModelRelationDto("cmp.app", "cmp.lib", "references", ["clm.ref"])],
        Patterns: [],
        Decisions: [new ModelDecisionDto("dec.layering", "App depends on Lib, never the reverse", [], ModelDecision.Unrecorded, ["clm.ref"])],
        Intents: [],
        Flows: [new ModelFlowDto("flw.build", "Build", [new FlowStepDto("cmp.app", "clm.app"), new FlowStepDto("cmp.lib", "clm.ref")], ["clm.ref"])],
        Invariants: [new ModelInvariantDto("inv.layering", "Lib never references App", "contract", ["cmp.lib"], ["clm.ref"])],
        Outline: [new OutlineSectionDto("sec.map", "system-map.md", "System map", "What the parts are", ["clm.app"],
            [new OutlineBlockDto(OutlineBlockKind.Component, "cmp.app"), new OutlineBlockDto(OutlineBlockKind.Flow, "flw.build")])],
        Coverage: new ModelCoverageDto([new CoverageAreaDto("project-manifests", "2 of 2 project files")], [new CoverageGapDto("source-files", "not read")], [], null),
        Claims:
        [
            new SubmittedClaim("clm.app", ClaimTier.Fact, componentStatement, [new ClaimEvidenceDto(EvidenceKind.Code, Path: "src/App/App.csproj")], ClaimConfidence.High, ClaimOrigin.Deterministic),
            new SubmittedClaim("clm.lib", ClaimTier.Fact, "Lib is a .NET project", [new ClaimEvidenceDto(EvidenceKind.Code, Path: "src/Lib/Lib.csproj")], ClaimConfidence.High, ClaimOrigin.Deterministic),
            new SubmittedClaim("clm.ref", ClaimTier.Fact, "App references Lib", [new ClaimEvidenceDto(EvidenceKind.Code, Path: "src/App/App.csproj", Symbol: "ProjectReference", Lines: "9")], ClaimConfidence.High, ClaimOrigin.Deterministic),
        ]);

    /// <summary>A raw JSON body with every top-level list present and empty except the claims —
    /// for tests that must send something the typed request cannot express (an omitted field).</summary>
    public static Dictionary<string, object> AnonymousBody(IReadOnlyList<object> claims, string baseCommit = "abc123") => new()
    {
        ["baseCommit"] = baseCommit,
        ["components"] = Array.Empty<object>(),
        ["relations"] = Array.Empty<object>(),
        ["patterns"] = Array.Empty<object>(),
        ["decisions"] = Array.Empty<object>(),
        ["intents"] = Array.Empty<object>(),
        ["flows"] = Array.Empty<object>(),
        ["invariants"] = Array.Empty<object>(),
        ["outline"] = Array.Empty<object>(),
        ["coverage"] = new { analyzed = Array.Empty<object>(), notAnalyzed = Array.Empty<object>(), rejected = Array.Empty<object>() },
        ["claims"] = claims,
    };
}
