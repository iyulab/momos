using Momos.Host.Domain;

namespace Momos.Host.Contracts;

public sealed record ClaimEvidenceDto(
    EvidenceKind Kind, string? Path = null, string? Symbol = null, string? Lines = null,
    string? Sha = null, string? Url = null, Guid? InspectionRequestId = null, string? ClaimKey = null)
{
    public ClaimEvidence ToDomain() => new(Kind, Path, Symbol, Lines, Sha, Url, InspectionRequestId, ClaimKey);

    public static ClaimEvidenceDto FromDomain(ClaimEvidence e) => new(e.Kind, e.Path, e.Symbol, e.Lines, e.Sha, e.Url, e.InspectionRequestId, e.ClaimKey);
}

public sealed record SubmittedClaim(string Key, ClaimTier Tier, string Statement, IReadOnlyList<ClaimEvidenceDto> Evidence, ClaimConfidence Confidence);

public sealed record ModelComponentDto(string Id, string Name, string Kind, string? Responsibility, IReadOnlyList<string> Claims);

public sealed record ModelRelationDto(string From, string To, string Kind, IReadOnlyList<string> Claims);

public sealed record ModelPatternDto(string Id, string Name, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

public sealed record ModelDecisionDto(string Id, string Summary, IReadOnlyList<string> Alternatives, string Rationale, IReadOnlyList<string> Claims);

public sealed record ModelIntentDto(string Id, string Statement, IntentSource Source, IReadOnlyList<string> Claims);

/// <summary>A Worker's completed-analysis submission for one analysis request.</summary>
public sealed record SubmitProjectModelRequest(
    string BaseCommit,
    IReadOnlyList<ModelComponentDto> Components,
    IReadOnlyList<ModelRelationDto> Relations,
    IReadOnlyList<ModelPatternDto> Patterns,
    IReadOnlyList<ModelDecisionDto> Decisions,
    IReadOnlyList<ModelIntentDto> Intents,
    IReadOnlyList<SubmittedClaim> Claims)
{
    /// <summary>JSON binding leaves an omitted list as null despite its non-nullable type; name
    /// every such gap (and a missing base commit) so the submission is rejected as a 400 instead
    /// of failing while it is mapped.</summary>
    public IReadOnlyList<string> MissingFields()
    {
        var missing = new List<string>();
        void Require(string name, object? value)
        {
            if (value is null)
            {
                missing.Add($"'{name}' is required (send an empty list when there is nothing to report).");
            }
        }

        if (string.IsNullOrWhiteSpace(BaseCommit))
        {
            missing.Add($"'{nameof(BaseCommit)}' is required.");
        }

        Require(nameof(Components), Components);
        Require(nameof(Relations), Relations);
        Require(nameof(Patterns), Patterns);
        Require(nameof(Decisions), Decisions);
        Require(nameof(Intents), Intents);
        Require(nameof(Claims), Claims);
        if (missing.Count > 0)
        {
            return missing;
        }

        foreach (var c in Components)
        {
            Require($"Components['{c.Id}'].Claims", c.Claims);
        }

        foreach (var r in Relations)
        {
            Require($"Relations['{r.From}'->'{r.To}'].Claims", r.Claims);
        }

        foreach (var p in Patterns)
        {
            Require($"Patterns['{p.Id}'].AppliesTo", p.AppliesTo);
            Require($"Patterns['{p.Id}'].Claims", p.Claims);
        }

        foreach (var d in Decisions)
        {
            Require($"Decisions['{d.Id}'].Alternatives", d.Alternatives);
            Require($"Decisions['{d.Id}'].Claims", d.Claims);
        }

        foreach (var i in Intents)
        {
            Require($"Intents['{i.Id}'].Claims", i.Claims);
        }

        foreach (var c in Claims)
        {
            Require($"Claims['{c.Key}'].Evidence", c.Evidence);
        }

        return missing;
    }

    public ModelElements ToElements() => new(
        Components.Select(c => new ModelComponent(c.Id, c.Name, c.Kind, c.Responsibility, c.Claims)).ToList(),
        Relations.Select(r => new ModelRelation(r.From, r.To, r.Kind, r.Claims)).ToList(),
        Patterns.Select(p => new ModelPattern(p.Id, p.Name, p.AppliesTo, p.Claims)).ToList(),
        Decisions.Select(d => new ModelDecision(d.Id, d.Summary, d.Alternatives, d.Rationale, d.Claims)).ToList(),
        Intents.Select(i => new ModelIntent(i.Id, i.Statement, i.Source, i.Claims)).ToList());
}

/// <summary>A developer's verdict on one claim: Confirmed, Disputed, or Corrected (which
/// requires the corrected understanding in <see cref="Correction"/>).</summary>
public sealed record CorrectClaimRequest(ClaimStatus Status, string? Correction);

public sealed record ClaimResponse(
    string Key, ClaimTier Tier, string Statement, IReadOnlyList<ClaimEvidenceDto> Evidence,
    ClaimConfidence Confidence, ClaimStatus Status, string? Correction, DateTimeOffset? CorrectedAt)
{
    public static ClaimResponse FromEntity(ModelClaim c) => new(
        c.Key, c.Tier, c.Statement, c.Evidence.Select(ClaimEvidenceDto.FromDomain).ToList(),
        c.Confidence, c.Status, c.Correction, c.CorrectedAt);
}

public sealed record ProjectModelResponse(
    Guid ProjectId, int ModelVersion, string BaseCommit, Guid AnalysisRequestId, DateTimeOffset CreatedAt,
    IReadOnlyList<ModelComponentDto> Components, IReadOnlyList<ModelRelationDto> Relations,
    IReadOnlyList<ModelPatternDto> Patterns, IReadOnlyList<ModelDecisionDto> Decisions,
    IReadOnlyList<ModelIntentDto> Intents, IReadOnlyList<ClaimResponse> Claims)
{
    public static ProjectModelResponse FromEntity(ProjectModel m) => new(
        m.ProjectId, m.ModelVersion, m.BaseCommit, m.AnalysisRequestId, m.CreatedAt,
        m.Components.Select(c => new ModelComponentDto(c.Id, c.Name, c.Kind, c.Responsibility, c.Claims)).ToList(),
        m.Relations.Select(r => new ModelRelationDto(r.From, r.To, r.Kind, r.Claims)).ToList(),
        m.Patterns.Select(p => new ModelPatternDto(p.Id, p.Name, p.AppliesTo, p.Claims)).ToList(),
        m.Decisions.Select(d => new ModelDecisionDto(d.Id, d.Summary, d.Alternatives, d.Rationale, d.Claims)).ToList(),
        m.Intents.Select(i => new ModelIntentDto(i.Id, i.Statement, i.Source, i.Claims)).ToList(),
        m.Claims.OrderBy(c => c.Key, StringComparer.Ordinal).Select(ClaimResponse.FromEntity).ToList());
}
