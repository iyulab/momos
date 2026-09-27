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
        ["claims"] = claims,
    };
}
