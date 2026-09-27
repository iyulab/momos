namespace Momos.Worker.Execution;

/// <summary>
/// Mirrors <c>Momos.Host.Domain.InspectionRequestStatus</c>'s wire shape (named
/// values, via <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// on both sides — see <c>HostApiClient.JsonOptions</c>). Kept as a local copy
/// rather than a shared reference — Worker and Host are separate deployables by
/// design.
/// </summary>
public enum InspectionRequestStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Mirrors <c>Momos.Host.Domain.InspectionRequestKind</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy. Inspection and analysis
/// requests share one queue; the kind decides how a claimed request is run and completed.
/// </summary>
public enum InspectionRequestKind
{
    Inspection,
    Analysis,
}

/// <summary><see cref="Kind"/> defaults to <see cref="InspectionRequestKind.Inspection"/> so a
/// request built without it reads as the only kind older protocols knew.</summary>
public sealed record ClaimedInspectionRequest(
    Guid Id,
    Guid ProjectId,
    string? Focus,
    string? CommitRef,
    DateTimeOffset SubmittedAt,
    InspectionRequestStatus Status,
    string? FailureReason,
    InspectionRequestKind Kind = InspectionRequestKind.Inspection);

public sealed record ProjectInfo(
    Guid Id,
    string Name,
    string? RepositoryUrl,
    string? DeploymentUrl,
    string Purpose,
    string Vision,
    string Scope,
    string? AppInstallerUri = null,
    string? AppInstallPlatform = null,
    string? AppInstallArgs = null,
    string? AppInstallLaunchCommand = null);

/// <summary>
/// Mirrors <c>Momos.Host.Domain.FindingCategory</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy rather than a
/// shared reference.
/// </summary>
public enum FindingCategory
{
    FunctionalDefect,
    UxConsistency,
}

public sealed record FindingPayload(FindingCategory Category, string Description, string Evidence);

/// <summary>Mirrors Host's SubmitToolCallRequest wire shape (Momos.Host.Contracts.SubmitToolCallRequest) —
/// see <see cref="ClaimedInspectionRequest"/>'s doc comment for why this is a local copy. No
/// <c>Order</c> field — Host assigns it from submission order, same as <see cref="FindingPayload"/>.</summary>
public sealed record ToolCallPayload(string Tool, string Summary, bool Success, int DurationMs);

public sealed record SubmitReportRequest(IReadOnlyList<FindingPayload> Findings, IReadOnlyList<ToolCallPayload> ToolCalls);

/// <summary>Mirrors Host's FailInspectionRequestRequest wire shape (Momos.Host.Contracts.FailInspectionRequestRequest) —
/// see <see cref="ClaimedInspectionRequest"/>'s doc comment for why this is a local copy.</summary>
public sealed record SubmitFailureRequest(string Reason, IReadOnlyList<ToolCallPayload> ToolCalls);

public sealed record ClaimNextRequest(int ProtocolVersion, string WorkerVersion);

/// <summary>Mirrors Host's ClaimNextResponse wire shape (Momos.Host.Contracts.ClaimNextResponse) —
/// see ClaimedInspectionRequest's doc comment for why this is a local copy.</summary>
public sealed record ClaimNextResult(ClaimedInspectionRequest? Request, bool UpdateRequired, string? RecommendedWorkerVersion);

/// <summary>
/// Mirrors <c>Momos.Host.Domain.ClaimTier</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy.
/// </summary>
public enum ClaimTier
{
    Fact,
    History,
    Assessment,
}

/// <summary>
/// Mirrors <c>Momos.Host.Domain.ClaimConfidence</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy.
/// </summary>
public enum ClaimConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>
/// Mirrors <c>Momos.Host.Domain.EvidenceKind</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy.
/// </summary>
public enum EvidenceKind
{
    Code,
    Commit,
    PullRequest,
    Issue,
    Finding,
    Claim,
}

/// <summary>
/// Mirrors <c>Momos.Host.Domain.IntentSource</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy.
/// </summary>
public enum IntentSource
{
    Developer,
    Document,
    Inferred,
}

/// <summary>Mirrors Host's ClaimEvidenceDto wire shape (Momos.Host.Contracts.ClaimEvidenceDto).
/// Which fields apply depends on <see cref="Kind"/>: Code → Path (+Symbol, Lines), Commit → Sha,
/// PullRequest/Issue → Url, Finding → InspectionRequestId, Claim → ClaimKey.</summary>
public sealed record EvidencePayload(
    EvidenceKind Kind,
    string? Path = null,
    string? Symbol = null,
    string? Lines = null,
    string? Sha = null,
    string? Url = null,
    Guid? InspectionRequestId = null,
    string? ClaimKey = null);

/// <summary>Mirrors <c>Momos.Host.Domain.ClaimOrigin</c>'s wire shape — see
/// <see cref="ClaimedInspectionRequest"/>'s doc comment for why this is a local copy.</summary>
public enum ClaimOrigin
{
    Deterministic,
    Synthesized,
}

/// <summary>Mirrors Host's SubmittedClaim wire shape (Momos.Host.Contracts.SubmittedClaim).</summary>
public sealed record ClaimPayload(string Key, ClaimTier Tier, string Statement, IReadOnlyList<EvidencePayload> Evidence, ClaimConfidence Confidence, ClaimOrigin Origin);

/// <summary>Mirrors Host's ModelComponentDto wire shape (Momos.Host.Contracts.ModelComponentDto).</summary>
public sealed record ComponentPayload(string Id, string Name, string Kind, string? Responsibility, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's ModelRelationDto wire shape (Momos.Host.Contracts.ModelRelationDto).</summary>
public sealed record RelationPayload(string From, string To, string Kind, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's ModelPatternDto wire shape (Momos.Host.Contracts.ModelPatternDto).</summary>
public sealed record PatternPayload(string Id, string Name, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's ModelDecisionDto wire shape (Momos.Host.Contracts.ModelDecisionDto).</summary>
public sealed record DecisionPayload(string Id, string Summary, IReadOnlyList<string> Alternatives, string Rationale, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's ModelIntentDto wire shape (Momos.Host.Contracts.ModelIntentDto).</summary>
public sealed record IntentPayload(string Id, string Statement, IntentSource Source, IReadOnlyList<string> Claims);

/// <summary>Mirrors <c>Momos.Host.Domain.OutlineBlockKind</c>'s wire shape — see
/// <see cref="ClaimedInspectionRequest"/>'s doc comment for why this is a local copy.</summary>
public enum OutlineBlockKind
{
    Claim,
    Component,
    Pattern,
    Decision,
    Intent,
    Flow,
    Invariant,
    Map,
}

/// <summary>Mirrors Host's FlowStepDto wire shape.</summary>
public sealed record FlowStepPayload(string? ComponentId, string ClaimKey);

/// <summary>Mirrors Host's ModelFlowDto wire shape.</summary>
public sealed record FlowPayload(string Id, string Name, IReadOnlyList<FlowStepPayload> Steps, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's ModelInvariantDto wire shape.</summary>
public sealed record InvariantPayload(string Id, string Statement, string Kind, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

/// <summary>Mirrors Host's OutlineBlockDto wire shape.</summary>
public sealed record OutlineBlockPayload(OutlineBlockKind Kind, string Ref);

/// <summary>Mirrors Host's OutlineSectionDto wire shape.</summary>
public sealed record OutlineSectionPayload(string Id, string Path, string Title, string Purpose, IReadOnlyList<string> OwnerSummaryClaims, IReadOnlyList<OutlineBlockPayload> Blocks);

/// <summary>Mirrors Host's CoverageAreaDto wire shape.</summary>
public sealed record CoverageAreaPayload(string Area, string Detail);

/// <summary>Mirrors Host's CoverageGapDto wire shape.</summary>
public sealed record CoverageGapPayload(string Area, string Reason);

/// <summary>Mirrors Host's CoverageRejectionDto wire shape.</summary>
public sealed record CoverageRejectionPayload(string Reason, int Count);

/// <summary>Mirrors Host's CoverageGeneratorDto wire shape.</summary>
public sealed record CoverageGeneratorPayload(string Model, string PromptVersion);

/// <summary>Mirrors Host's ModelCoverageDto wire shape — what the analysis read, left unread, and discarded.</summary>
public sealed record CoveragePayload(
    IReadOnlyList<CoverageAreaPayload> Analyzed, IReadOnlyList<CoverageGapPayload> NotAnalyzed,
    IReadOnlyList<CoverageRejectionPayload> Rejected, CoverageGeneratorPayload? Generator);

/// <summary>Mirrors Host's SubmitProjectModelRequest wire shape
/// (Momos.Host.Contracts.SubmitProjectModelRequest) — the completion of an analysis request. Every
/// list is required; send an empty list when there is nothing to report.</summary>
public sealed record ProjectModelPayload(
    string BaseCommit,
    IReadOnlyList<ComponentPayload> Components,
    IReadOnlyList<RelationPayload> Relations,
    IReadOnlyList<PatternPayload> Patterns,
    IReadOnlyList<DecisionPayload> Decisions,
    IReadOnlyList<IntentPayload> Intents,
    IReadOnlyList<FlowPayload> Flows,
    IReadOnlyList<InvariantPayload> Invariants,
    IReadOnlyList<OutlineSectionPayload> Outline,
    CoveragePayload Coverage,
    IReadOnlyList<ClaimPayload> Claims);

/// <summary>Mirrors Host's QueryKnowledgeRequest wire shape (Momos.Host.Contracts.QueryKnowledgeRequest) —
/// see ClaimedInspectionRequest's doc comment for why this is a local copy.</summary>
public sealed record QueryKnowledgeRequest(string Query, int MaxResults = 5);

/// <summary>Mirrors Host's QueryKnowledgeResponse wire shape (Momos.Host.Contracts.QueryKnowledgeResponse).</summary>
public sealed record QueryKnowledgeResponse(IReadOnlyList<KnowledgeSnippet> Snippets);

/// <summary>Mirrors Host's KnowledgeSnippet wire shape (Momos.Host.Contracts.KnowledgeSnippet).</summary>
public sealed record KnowledgeSnippet(string Content, double Score);
