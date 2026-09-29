using Momos.Host.Domain;

namespace Momos.Host.Contracts;

public sealed record ClaimEvidenceDto(
    EvidenceKind Kind, string? Path = null, string? Symbol = null, string? Lines = null,
    string? Sha = null, string? Url = null, Guid? InspectionRequestId = null, string? ClaimKey = null)
{
    public ClaimEvidence ToDomain() => new(Kind, Path, Symbol, Lines, Sha, Url, InspectionRequestId, ClaimKey);

    public static ClaimEvidenceDto FromDomain(ClaimEvidence e) => new(e.Kind, e.Path, e.Symbol, e.Lines, e.Sha, e.Url, e.InspectionRequestId, e.ClaimKey);
}

/// <param name="Tier">Required. Nullable, like <paramref name="Confidence"/> and <paramref name="Origin"/>,
/// only so an omitted value is caught and named (see <see cref="SubmitProjectModelRequest.MissingFields"/>)
/// instead of binding to the enum's first value — for these two, the most authoritative one.</param>
public sealed record SubmittedClaim(string Key, ClaimTier? Tier, string Statement, IReadOnlyList<ClaimEvidenceDto> Evidence, ClaimConfidence? Confidence, ClaimOrigin? Origin);

public sealed record ModelComponentDto(string Id, string Name, string Kind, string? Responsibility, IReadOnlyList<string> Claims);

public sealed record ModelRelationDto(string From, string To, string Kind, IReadOnlyList<string> Claims);

public sealed record ModelPatternDto(string Id, string Name, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

public sealed record ModelDecisionDto(string Id, string Summary, IReadOnlyList<string> Alternatives, string Rationale, IReadOnlyList<string> Claims);

public sealed record ModelIntentDto(string Id, string Statement, IntentSource Source, IReadOnlyList<string> Claims);

public sealed record FlowStepDto(string? ComponentId, string ClaimKey);

public sealed record ModelFlowDto(string Id, string Name, IReadOnlyList<FlowStepDto> Steps, IReadOnlyList<string> Claims);

public sealed record ModelInvariantDto(string Id, string Statement, string Kind, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

/// <param name="Kind">Required; nullable only so an omission is named instead of binding to the first kind.</param>
public sealed record OutlineBlockDto(OutlineBlockKind? Kind, string Ref);

public sealed record OutlineSectionDto(string Id, string Path, string Title, string Purpose, IReadOnlyList<string> OwnerSummaryClaims, IReadOnlyList<OutlineBlockDto> Blocks);

public sealed record CoverageAreaDto(string Area, string Detail);

public sealed record CoverageGapDto(string Area, string Reason);

public sealed record CoverageRejectionDto(string Reason, int Count);

public sealed record CoverageGeneratorDto(string Model, string PromptVersion);

public sealed record ModelCoverageDto(
    IReadOnlyList<CoverageAreaDto> Analyzed, IReadOnlyList<CoverageGapDto> NotAnalyzed,
    IReadOnlyList<CoverageRejectionDto> Rejected, CoverageGeneratorDto? Generator)
{
    public ModelCoverage ToDomain() => new(
        Analyzed.Select(a => new CoverageArea(a.Area, a.Detail)).ToList(),
        NotAnalyzed.Select(g => new CoverageGap(g.Area, g.Reason)).ToList(),
        Rejected.Select(r => new CoverageRejection(r.Reason, r.Count)).ToList(),
        Generator is null ? null : new CoverageGenerator(Generator.Model, Generator.PromptVersion));

    public static ModelCoverageDto FromDomain(ModelCoverage c) => new(
        c.Analyzed.Select(a => new CoverageAreaDto(a.Area, a.Detail)).ToList(),
        c.NotAnalyzed.Select(g => new CoverageGapDto(g.Area, g.Reason)).ToList(),
        c.Rejected.Select(r => new CoverageRejectionDto(r.Reason, r.Count)).ToList(),
        c.Generator is null ? null : new CoverageGeneratorDto(c.Generator.Model, c.Generator.PromptVersion));
}

/// <summary>A Worker's completed-analysis submission for one analysis request.</summary>
public sealed record SubmitProjectModelRequest(
    string BaseCommit,
    IReadOnlyList<ModelComponentDto> Components,
    IReadOnlyList<ModelRelationDto> Relations,
    IReadOnlyList<ModelPatternDto> Patterns,
    IReadOnlyList<ModelDecisionDto> Decisions,
    IReadOnlyList<ModelIntentDto> Intents,
    IReadOnlyList<ModelFlowDto> Flows,
    IReadOnlyList<ModelInvariantDto> Invariants,
    IReadOnlyList<OutlineSectionDto> Outline,
    ModelCoverageDto Coverage,
    IReadOnlyList<SubmittedClaim> Claims)
{
    /// <summary>JSON binding leaves an omitted list as null despite its non-nullable type; name
    /// every such gap (and a missing base commit) so the submission is rejected as a 400 instead
    /// of failing while it is mapped.</summary>
    public IReadOnlyList<string> MissingFields()
    {
        var missing = new List<string>();
        void Require(string name, object? value, bool isList = true)
        {
            if (value is null)
            {
                missing.Add(isList
                    ? $"'{name}' is required (send an empty list when there is nothing to report)."
                    : $"'{name}' is required.");
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
        Require(nameof(Flows), Flows);
        Require(nameof(Invariants), Invariants);
        Require(nameof(Outline), Outline);
        Require(nameof(Coverage), Coverage, isList: false);
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

        foreach (var f in Flows)
        {
            Require($"Flows['{f.Id}'].Steps", f.Steps);
            Require($"Flows['{f.Id}'].Claims", f.Claims);
        }

        foreach (var i in Invariants)
        {
            Require($"Invariants['{i.Id}'].AppliesTo", i.AppliesTo);
            Require($"Invariants['{i.Id}'].Claims", i.Claims);
        }

        foreach (var s in Outline)
        {
            Require($"Outline['{s.Id}'].OwnerSummaryClaims", s.OwnerSummaryClaims);
            Require($"Outline['{s.Id}'].Blocks", s.Blocks);
            for (var n = 0; n < (s.Blocks?.Count ?? 0); n++)
            {
                Require($"Outline['{s.Id}'].Blocks[{n + 1}].Kind", s.Blocks![n].Kind, isList: false);
            }
        }

        Require("Coverage.Analyzed", Coverage.Analyzed);
        Require("Coverage.NotAnalyzed", Coverage.NotAnalyzed);
        Require("Coverage.Rejected", Coverage.Rejected);

        foreach (var c in Claims)
        {
            Require($"Claims['{c.Key}'].Tier", c.Tier, isList: false);
            Require($"Claims['{c.Key}'].Confidence", c.Confidence, isList: false);
            Require($"Claims['{c.Key}'].Evidence", c.Evidence);
            Require($"Claims['{c.Key}'].Origin", c.Origin, isList: false);
        }

        return missing;
    }

    public ModelElements ToElements() => new(
        Components.Select(c => new ModelComponent(c.Id, c.Name, c.Kind, c.Responsibility, c.Claims)).ToList(),
        Relations.Select(r => new ModelRelation(r.From, r.To, r.Kind, r.Claims)).ToList(),
        Patterns.Select(p => new ModelPattern(p.Id, p.Name, p.AppliesTo, p.Claims)).ToList(),
        Decisions.Select(d => new ModelDecision(d.Id, d.Summary, d.Alternatives, d.Rationale, d.Claims)).ToList(),
        Intents.Select(i => new ModelIntent(i.Id, i.Statement, i.Source, i.Claims)).ToList(),
        Flows.Select(f => new ModelFlow(f.Id, f.Name, f.Steps.Select(s => new FlowStep(s.ComponentId, s.ClaimKey)).ToList(), f.Claims)).ToList(),
        Invariants.Select(i => new ModelInvariant(i.Id, i.Statement, i.Kind, i.AppliesTo, i.Claims)).ToList(),
        // MissingFields() has already rejected a block without a kind.
        Outline.Select(s => new OutlineSection(s.Id, s.Path, s.Title, s.Purpose, s.OwnerSummaryClaims,
            s.Blocks.Select(b => new OutlineBlock(b.Kind!.Value, b.Ref)).ToList())).ToList(),
        Coverage.ToDomain());
}

/// <summary>A developer's verdict on one claim: Confirmed, Disputed, or Corrected (which
/// requires the corrected understanding in <see cref="Correction"/>).</summary>
public sealed record CorrectClaimRequest(ClaimStatus Status, string? Correction);

public sealed record ClaimResponse(
    string Key, ClaimTier Tier, string Statement, IReadOnlyList<ClaimEvidenceDto> Evidence,
    ClaimConfidence Confidence, ClaimStatus Status, string? Correction, DateTimeOffset? CorrectedAt, ClaimOrigin Origin)
{
    public static ClaimResponse FromEntity(ModelClaim c) => new(
        c.Key, c.Tier, c.Statement, c.Evidence.Select(ClaimEvidenceDto.FromDomain).ToList(),
        c.Confidence, c.Status, c.Correction, c.CorrectedAt, c.Origin);
}

public sealed record ProjectModelResponse(
    Guid ProjectId, int ModelVersion, string BaseCommit, Guid AnalysisRequestId, DateTimeOffset CreatedAt,
    IReadOnlyList<ModelComponentDto> Components, IReadOnlyList<ModelRelationDto> Relations,
    IReadOnlyList<ModelPatternDto> Patterns, IReadOnlyList<ModelDecisionDto> Decisions,
    IReadOnlyList<ModelIntentDto> Intents, IReadOnlyList<ModelFlowDto> Flows,
    IReadOnlyList<ModelInvariantDto> Invariants, IReadOnlyList<OutlineSectionDto> Outline,
    ModelCoverageDto? Coverage, IReadOnlyList<ClaimResponse> Claims, DateTimeOffset? KnowledgeIndexedAt)
{
    public static ProjectModelResponse FromEntity(ProjectModel m) => new(
        m.ProjectId, m.ModelVersion, m.BaseCommit, m.AnalysisRequestId, m.CreatedAt,
        m.Components.Select(c => new ModelComponentDto(c.Id, c.Name, c.Kind, c.Responsibility, c.Claims)).ToList(),
        m.Relations.Select(r => new ModelRelationDto(r.From, r.To, r.Kind, r.Claims)).ToList(),
        m.Patterns.Select(p => new ModelPatternDto(p.Id, p.Name, p.AppliesTo, p.Claims)).ToList(),
        m.Decisions.Select(d => new ModelDecisionDto(d.Id, d.Summary, d.Alternatives, d.Rationale, d.Claims)).ToList(),
        m.Intents.Select(i => new ModelIntentDto(i.Id, i.Statement, i.Source, i.Claims)).ToList(),
        m.Flows.Select(f => new ModelFlowDto(f.Id, f.Name, f.Steps.Select(s => new FlowStepDto(s.ComponentId, s.ClaimKey)).ToList(), f.Claims)).ToList(),
        m.Invariants.Select(i => new ModelInvariantDto(i.Id, i.Statement, i.Kind, i.AppliesTo, i.Claims)).ToList(),
        m.Outline.Select(s => new OutlineSectionDto(s.Id, s.Path, s.Title, s.Purpose, s.OwnerSummaryClaims,
            s.Blocks.Select(b => new OutlineBlockDto(b.Kind, b.Ref)).ToList())).ToList(),
        m.Coverage is null ? null : ModelCoverageDto.FromDomain(m.Coverage),
        m.Claims.OrderBy(c => c.Key, StringComparer.Ordinal).Select(ClaimResponse.FromEntity).ToList(),
        m.KnowledgeIndexedAt);
}

public sealed record ReportDocumentDto(string Path, string Content);

/// <summary>The deep report of the latest model as a tree of markdown documents — <c>index.md</c>
/// is the summary, <c>components/*.md</c> and <c>claims/*.md</c> hold one component or claim
/// each, and every link between them is relative. A generic <c>{path, content}</c> shape, so any
/// static-site builder that takes a markdown tree can publish it; a reader who wants one document
/// reads <c>index.md</c>.</summary>
public sealed record ProjectModelReportResponse(int ModelVersion, string BaseCommit, IReadOnlyList<ReportDocumentDto> Documents);
