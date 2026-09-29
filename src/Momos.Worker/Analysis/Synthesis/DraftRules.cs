using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>Why a proposal was turned away — the categories an analysis counts in its coverage.</summary>
public static class RejectionReason
{
    public const string MissingLocator = "evidence without the identifier that locates it";
    public const string EvidenceNotFound = "evidence not found at the analyzed commit";
    public const string TierRule = "evidence that does not fit the claim's tier";
    public const string UnresolvedReference = "a reference to something not in the model";
    public const string DuplicateTopic = "a topic already used";
    public const string InvalidElement = "an element that breaks a model rule";
    public const string ChapterLimit = "more chapters than allowed";
    public const string Unsupported = "a request the analysis does not accept";
}

/// <summary>The outcome of checking one proposal; <see cref="Message"/> is what the model reads.</summary>
/// <param name="Reason">The rejection reason, or null when accepted.</param>
/// <param name="Message">What the agent is told; may quote what it proposed.</param>
/// <param name="Cause">A fixed description of what failed that never quotes the proposal, so it can
/// be logged where <paramref name="Message"/> cannot.</param>
public sealed record Verdict(string? Reason, string Message, string? Cause = null)
{
    public static Verdict Accept { get; } = new(null, "Accepted.");

    public bool Accepted => Reason is null;

    public static Verdict Reject(string reason, string message, string? cause = null) => new(reason, message, cause);
}

/// <summary>
/// The Host validator's rules, restated for what a language model proposes. The Worker cannot
/// reference the Host, and a single proposal the Host would reject fails the whole submission —
/// so each rule the Host applies to claims and elements is applied here first. Two rules are
/// stricter than the Host's: code evidence must name a symbol, and a model may not cite a
/// finding (only a reproduced inspection can supply one).
/// </summary>
public static class DraftRules
{
    public const string Unrecorded = "unrecorded";
    public const string MapEverything = "*";
    public const int MaxTitleLength = 80;
    public const int MaxPurposeLength = 200;

    public static Verdict CheckClaim(ClaimPayload claim, Func<string, ClaimPayload?> resolve)
    {
        if (claim.Origin != ClaimOrigin.Synthesized)
        {
            return Verdict.Reject(RejectionReason.Unsupported, "A proposed claim is always Synthesized.");
        }

        if (string.IsNullOrWhiteSpace(claim.Statement))
        {
            return Verdict.Reject(RejectionReason.InvalidElement, "The statement is empty.");
        }

        if (!Enum.IsDefined(claim.Tier) || !Enum.IsDefined(claim.Confidence))
        {
            return Verdict.Reject(RejectionReason.Unsupported, "Unknown tier or confidence.");
        }

        if (claim.Evidence.Count == 0)
        {
            return Verdict.Reject(RejectionReason.TierRule, "A claim needs at least one piece of evidence.");
        }

        if (claim.Evidence.Any(e => e.Kind == EvidenceKind.Finding || !Enum.IsDefined(e.Kind)))
        {
            return Verdict.Reject(RejectionReason.Unsupported, "Findings come from inspections; cite code, commits, pull requests, issues or claims.");
        }

        // Names the piece and the empty field: told only what a kind needs, an agent resends the
        // same proposal without finding which of its pieces is short.
        if (claim.Evidence.Select((e, i) => (Evidence: e, Missing: MissingFields(e), Number: i + 1)).FirstOrDefault(x => x.Missing.Count > 0) is { Evidence: { } unlocated } found)
        {
            return Verdict.Reject(RejectionReason.MissingLocator,
                $"Evidence {found.Number} ({unlocated.Kind}) has no {string.Join(" and no ", found.Missing)}. {unlocated.Kind} evidence needs {RequiredField(unlocated.Kind)}.");
        }

        var cited = claim.Evidence.Where(e => e.Kind == EvidenceKind.Claim).Select(e => e.ClaimKey!).ToList();
        if (cited.FirstOrDefault(k => k == claim.Key || resolve(k) is null) is { } missing)
        {
            return Verdict.Reject(RejectionReason.UnresolvedReference, $"Claim '{missing}' is not in the model.");
        }

        return claim.Tier switch
        {
            ClaimTier.Fact when !claim.Evidence.Any(e => e.Kind == EvidenceKind.Code) =>
                Verdict.Reject(RejectionReason.TierRule, "A Fact needs code evidence."),
            ClaimTier.History when !claim.Evidence.Any(e => e.Kind is EvidenceKind.Commit or EvidenceKind.PullRequest or EvidenceKind.Issue) =>
                Verdict.Reject(RejectionReason.TierRule, "A History claim needs a commit, pull request or issue."),
            ClaimTier.Assessment when cited.Count == 0 =>
                Verdict.Reject(RejectionReason.TierRule, "An Assessment must cite the claim it interprets."),
            ClaimTier.Assessment when claim.Confidence != ClaimConfidence.Low =>
                Verdict.Reject(RejectionReason.TierRule, "An Assessment is Low confidence until an inspection reproduces it."),
            _ => Verdict.Accept,
        };
    }

    /// <summary>A claim built on others can be no more certain than the least certain of them.</summary>
    public static ClaimPayload CapConfidence(ClaimPayload claim, Func<string, ClaimPayload?> resolve)
    {
        // ClaimConfidence is declared High, Medium, Low: the largest value is the weakest.
        var weakest = claim.Evidence
            .Where(e => e.Kind == EvidenceKind.Claim && e.ClaimKey is not null)
            .Select(e => resolve(e.ClaimKey!)?.Confidence)
            .OfType<ClaimConfidence>()
            .DefaultIfEmpty(ClaimConfidence.High)
            .Max();
        return weakest > claim.Confidence ? claim with { Confidence = weakest } : claim;
    }

    public static Verdict CheckClaimRefs(IReadOnlyList<string> claims, Func<string, ClaimPayload?> resolve)
    {
        if (claims.Count == 0)
        {
            return Verdict.Reject(RejectionReason.InvalidElement, "Every element needs at least one claim behind it.");
        }

        return claims.FirstOrDefault(k => resolve(k) is null) is { } missing
            ? Verdict.Reject(RejectionReason.UnresolvedReference, $"Claim '{missing}' is not in the model.")
            : Verdict.Accept;
    }

    public static Verdict CheckDecision(string rationale, IReadOnlyList<string> claims, Func<string, ClaimPayload?> resolve)
    {
        if (CheckClaimRefs(claims, resolve) is { Accepted: false } refs)
        {
            return refs;
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return Verdict.Reject(RejectionReason.InvalidElement, $"A reason nobody recorded is '{Unrecorded}'.");
        }

        return rationale != Unrecorded && !claims.Any(k => resolve(k)?.Tier == ClaimTier.History)
            ? Verdict.Reject(RejectionReason.InvalidElement, $"A stated rationale needs a History claim that shows it; otherwise use '{Unrecorded}'.")
            : Verdict.Accept;
    }

    public static Verdict CheckBlock(OutlineBlockPayload block, Func<OutlineBlockKind, string, bool> resolves)
    {
        if (!Enum.IsDefined(block.Kind))
        {
            return Verdict.Reject(RejectionReason.Unsupported, $"Unknown block kind '{block.Kind}'.");
        }

        var ok = block.Kind == OutlineBlockKind.Map
            ? block.Ref == MapEverything || resolves(OutlineBlockKind.Component, block.Ref)
            : resolves(block.Kind, block.Ref);
        return ok ? Verdict.Accept : Verdict.Reject(RejectionReason.UnresolvedReference, $"{block.Kind} '{block.Ref}' is not in the model.");
    }

    // Field names only, never their values: this message is logged as it is.
    private static List<string> MissingFields(EvidencePayload e) => e.Kind switch
    {
        EvidenceKind.Code => [.. new[] { ("path", e.Path), ("symbol", e.Symbol) }.Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1)],
        EvidenceKind.Commit => string.IsNullOrWhiteSpace(e.Sha) ? ["sha"] : [],
        EvidenceKind.PullRequest or EvidenceKind.Issue => string.IsNullOrWhiteSpace(e.Url) ? ["url"] : [],
        _ => string.IsNullOrWhiteSpace(e.ClaimKey) ? ["claimKey"] : [],
    };

    private static string RequiredField(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Code => "a path and a symbol (text that appears verbatim in that file)",
        EvidenceKind.Commit => "a sha",
        EvidenceKind.PullRequest or EvidenceKind.Issue => "a url",
        _ => "a claim key",
    };
}
