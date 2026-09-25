namespace Momos.Host.Domain;

public sealed record ModelElements(
    IReadOnlyList<ModelComponent> Components,
    IReadOnlyList<ModelRelation> Relations,
    IReadOnlyList<ModelPattern> Patterns,
    IReadOnlyList<ModelDecision> Decisions,
    IReadOnlyList<ModelIntent> Intents);

/// <summary>
/// Enforces the evidence contract on a submitted model: no claim of any tier without evidence,
/// and no model element without a claim behind it. An assessment is an interpretation, so it must
/// point at the claim it interprets, and it cannot rise above low confidence unless a reproduced
/// finding backs it.
/// </summary>
public static class ClaimValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ModelClaim> claims, ModelElements elements)
    {
        var errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (!keys.Add(claim.Key))
            {
                errors.Add($"Claim key '{claim.Key}' appears more than once.");
            }
        }

        foreach (var claim in claims)
        {
            if (claim.Evidence.Count == 0)
            {
                errors.Add($"Claim '{claim.Key}' has no evidence.");
                continue;
            }

            if (claim.Evidence.Any(e => e.Kind == EvidenceKind.Code && string.IsNullOrWhiteSpace(e.Path)))
            {
                errors.Add($"Claim '{claim.Key}' has code evidence without a path.");
            }

            switch (claim.Tier)
            {
                case ClaimTier.Fact when !claim.Evidence.Any(e => e.Kind == EvidenceKind.Code):
                    errors.Add($"Fact '{claim.Key}' needs code evidence.");
                    break;
                case ClaimTier.History when !claim.Evidence.Any(e => e.Kind is EvidenceKind.Commit or EvidenceKind.PullRequest or EvidenceKind.Issue):
                    errors.Add($"History claim '{claim.Key}' needs a commit, pull request or issue.");
                    break;
                case ClaimTier.Assessment:
                    var referenced = claim.Evidence.Where(e => e.Kind == EvidenceKind.Claim).Select(e => e.ClaimKey).ToList();
                    if (referenced.Count == 0)
                    {
                        errors.Add($"Assessment '{claim.Key}' must reference the claim it interprets.");
                    }
                    else if (referenced.Any(k => k is null || k == claim.Key || !keys.Contains(k)))
                    {
                        errors.Add($"Assessment '{claim.Key}' references a claim that is not in this model.");
                    }

                    if (claim.Confidence != ClaimConfidence.Low && !claim.Evidence.Any(e => e.Kind == EvidenceKind.Finding))
                    {
                        errors.Add($"Assessment '{claim.Key}' cannot exceed low confidence without a reproduced finding.");
                    }

                    break;
            }
        }

        var componentIds = elements.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        void RequireClaims(string element, IReadOnlyList<string> claimKeys)
        {
            if (claimKeys.Count == 0)
            {
                errors.Add($"{element} has no claim behind it.");
            }
            else if (claimKeys.Any(k => !keys.Contains(k)))
            {
                errors.Add($"{element} references a claim that is not in this model.");
            }
        }

        foreach (var c in elements.Components)
        {
            RequireClaims($"Component '{c.Id}'", c.Claims);
        }

        foreach (var r in elements.Relations)
        {
            RequireClaims($"Relation '{r.From}'->'{r.To}'", r.Claims);
            if (!componentIds.Contains(r.From) || !componentIds.Contains(r.To))
            {
                errors.Add($"Relation '{r.From}'->'{r.To}' names a component that is not in this model.");
            }
        }

        foreach (var p in elements.Patterns)
        {
            RequireClaims($"Pattern '{p.Id}'", p.Claims);
        }

        foreach (var d in elements.Decisions)
        {
            RequireClaims($"Decision '{d.Id}'", d.Claims);
        }

        foreach (var i in elements.Intents)
        {
            // Developer-sourced intents come from a developer through the API, never from an analysis.
            if (i.Source == IntentSource.Developer)
            {
                errors.Add($"Intent '{i.Id}' cannot claim a developer source from an analysis.");
            }

            RequireClaims($"Intent '{i.Id}'", i.Claims);
        }

        return errors;
    }
}
