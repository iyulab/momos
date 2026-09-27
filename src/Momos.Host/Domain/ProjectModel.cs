namespace Momos.Host.Domain;

public enum ClaimTier { Fact, History, Assessment }

public enum ClaimConfidence { High, Medium, Low }

public enum ClaimStatus { Proposed, Confirmed, Disputed, Corrected }

public enum EvidenceKind { Code, Commit, PullRequest, Issue, Finding, Claim }

public enum IntentSource { Developer, Document, Inferred }

/// <summary>How a claim was produced: read mechanically from the repository by an extractor, or
/// synthesized by a language model from what it read. Both pass the same evidence checks; the
/// origin tells a reader how much interpretation stands between the evidence and the sentence.</summary>
public enum ClaimOrigin { Deterministic, Synthesized }

/// <summary>One piece of evidence behind a claim. Which fields are meaningful depends on
/// <see cref="Kind"/>: Code → Path (+Symbol, Lines), Commit → Sha, PullRequest/Issue → Url,
/// Finding → InspectionRequestId (a reproduced defect), Claim → ClaimKey (another claim in the
/// same model that this one interprets).</summary>
public sealed record ClaimEvidence(
    EvidenceKind Kind,
    string? Path = null,
    string? Symbol = null,
    string? Lines = null,
    string? Sha = null,
    string? Url = null,
    Guid? InspectionRequestId = null,
    string? ClaimKey = null);

public sealed record ModelComponent(string Id, string Name, string Kind, string? Responsibility, IReadOnlyList<string> Claims);

public sealed record ModelRelation(string From, string To, string Kind, IReadOnlyList<string> Claims);

public sealed record ModelPattern(string Id, string Name, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Claims);

public sealed record ModelDecision(string Id, string Summary, IReadOnlyList<string> Alternatives, string Rationale, IReadOnlyList<string> Claims)
{
    /// <summary>The rationale value for a decision whose reason was never written down. Momos
    /// records the gap instead of inventing a plausible reason.</summary>
    public const string Unrecorded = "unrecorded";
}

public sealed record ModelIntent(string Id, string Statement, IntentSource Source, IReadOnlyList<string> Claims);

/// <summary>
/// Momos's understanding of a project's design at one commit — the source of truth that both the
/// developer-facing deep report and the inspection agent (through the derived knowledge index) read.
/// Each analysis produces a new version; a developer correction lives on a claim, never deletes it.
/// </summary>
public sealed class ProjectModel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required int ModelVersion { get; init; }
    public required string BaseCommit { get; init; }

    /// <summary>The analysis request (an <see cref="InspectionRequest"/> of kind
    /// <see cref="InspectionRequestKind.Analysis"/>) that produced this version.</summary>
    public required Guid AnalysisRequestId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ModelComponent> Components { get; init; } = [];
    public List<ModelRelation> Relations { get; init; } = [];
    public List<ModelPattern> Patterns { get; init; } = [];
    public List<ModelDecision> Decisions { get; init; } = [];
    public List<ModelIntent> Intents { get; init; } = [];
    public ICollection<ModelClaim> Claims { get; init; } = new List<ModelClaim>();
}

/// <summary>One graded statement in a <see cref="ProjectModel"/>. <see cref="Key"/> is stable
/// across model versions for the same underlying observation, which is what lets a correction
/// carry over to the next analysis.</summary>
public sealed class ModelClaim
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectModelId { get; set; }
    public required string Key { get; init; }
    public required ClaimTier Tier { get; init; }
    public required string Statement { get; init; }
    public List<ClaimEvidence> Evidence { get; init; } = [];
    public required ClaimConfidence Confidence { get; init; }
    public required ClaimOrigin Origin { get; init; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Proposed;
    public string? Correction { get; set; }
    public DateTimeOffset? CorrectedAt { get; set; }
}
