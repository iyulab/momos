namespace Momos.Host.Domain;

public sealed record ModelElements(
    IReadOnlyList<ModelComponent> Components,
    IReadOnlyList<ModelRelation> Relations,
    IReadOnlyList<ModelPattern> Patterns,
    IReadOnlyList<ModelDecision> Decisions,
    IReadOnlyList<ModelIntent> Intents);

/// <summary>
/// Enforces the evidence contract on a submitted model: no claim of any tier without evidence,
/// no evidence without the identifier that locates it, no reference that does not resolve inside
/// the model, and no model element without a claim behind it. An assessment is an interpretation, so it must
/// point at the claim it interprets, and it cannot rise above low confidence unless a reproduced
/// finding backs it. A claim built on other claims can be no more certain than the least certain of them, claims
/// may not cite one another in a circle, and a decision that states its rationale needs a history
/// claim — a reason nobody recorded is <see cref="ModelDecision.Unrecorded"/>.
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
            // An out-of-range value (e.g. an origin JSON never named) still binds to the enum
            // field, so it must be caught here rather than assumed to be one of the named cases
            // below — silently falling into a default would misreport how the claim was produced.
            var undefined = false;
            if (!Enum.IsDefined(claim.Origin))
            {
                errors.Add($"Claim '{claim.Key}' has an unknown origin '{(int)claim.Origin}'.");
                undefined = true;
            }

            if (!Enum.IsDefined(claim.Tier))
            {
                errors.Add($"Claim '{claim.Key}' has an unknown tier '{(int)claim.Tier}'.");
                undefined = true;
            }

            if (!Enum.IsDefined(claim.Confidence))
            {
                errors.Add($"Claim '{claim.Key}' has an unknown confidence '{(int)claim.Confidence}'.");
                undefined = true;
            }

            // The other rules read these values; against an undefined one they only restate the
            // same error in another form.
            if (undefined)
            {
                continue;
            }

            if (claim.Evidence.Count == 0)
            {
                errors.Add($"Claim '{claim.Key}' has no evidence.");
                continue;
            }

            // Evidence a developer cannot follow is not evidence: each kind must carry the
            // identifier that locates it.
            foreach (var kind in claim.Evidence.Where(e => !Locates(e)).Select(e => e.Kind).Distinct())
            {
                errors.Add($"Claim '{claim.Key}' has {kind} evidence without {RequiredField(kind)}.");
            }

            // Tiers other than Assessment may also cite another claim; the citation must resolve.
            if (claim.Tier != ClaimTier.Assessment
                && claim.Evidence.Any(e => e.Kind == EvidenceKind.Claim && e.ClaimKey is { } k && (k == claim.Key || !keys.Contains(k))))
            {
                errors.Add($"Claim '{claim.Key}' references a claim that is not in this model.");
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

        // Duplicate keys are already reported above; the checks below read the first occurrence.
        var byKey = new Dictionary<string, ModelClaim>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            byKey.TryAdd(claim.Key, claim);
        }

        foreach (var claim in claims)
        {
            foreach (var cited in CitedKeys(claim).Select(k => byKey.GetValueOrDefault(k)).OfType<ModelClaim>())
            {
                if (Certainty(claim.Confidence) > Certainty(cited.Confidence))
                {
                    errors.Add($"Claim '{claim.Key}' is {claim.Confidence} confidence but cites '{cited.Key}' at {cited.Confidence}; a claim built on another cannot be more certain than it.");
                }
            }
        }

        foreach (var cycle in CitationCycles(byKey))
        {
            errors.Add($"Claims cite one another in a cycle: {string.Join(" -> ", cycle)}.");
        }

        RequireUniqueIds("Component", elements.Components.Select(c => c.Id));
        RequireUniqueIds("Pattern", elements.Patterns.Select(p => p.Id));
        RequireUniqueIds("Decision", elements.Decisions.Select(d => d.Id));
        RequireUniqueIds("Intent", elements.Intents.Select(i => i.Id));
        void RequireUniqueIds(string element, IEnumerable<string> ids)
        {
            foreach (var id in ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                errors.Add($"{element} id '{id}' appears more than once.");
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
            if (p.AppliesTo.Any(id => !componentIds.Contains(id)))
            {
                errors.Add($"Pattern '{p.Id}' applies to a component that is not in this model.");
            }
        }

        foreach (var d in elements.Decisions)
        {
            RequireClaims($"Decision '{d.Id}'", d.Claims);
            if (string.IsNullOrWhiteSpace(d.Rationale))
            {
                errors.Add($"Decision '{d.Id}' has a blank rationale; a reason nobody recorded is '{ModelDecision.Unrecorded}'.");
            }
            else if (d.Rationale != ModelDecision.Unrecorded
                && !d.Claims.Any(k => byKey.TryGetValue(k, out var c) && c.Tier == ClaimTier.History))
            {
                errors.Add($"Decision '{d.Id}' states a rationale without a history claim behind it; a reason nobody recorded is '{ModelDecision.Unrecorded}'.");
            }
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

    private static bool Locates(ClaimEvidence e) => e.Kind switch
    {
        EvidenceKind.Code => !string.IsNullOrWhiteSpace(e.Path),
        EvidenceKind.Commit => !string.IsNullOrWhiteSpace(e.Sha),
        EvidenceKind.PullRequest or EvidenceKind.Issue => !string.IsNullOrWhiteSpace(e.Url),
        EvidenceKind.Finding => e.InspectionRequestId is not null,
        EvidenceKind.Claim => !string.IsNullOrWhiteSpace(e.ClaimKey),
        _ => false,
    };

    private static string RequiredField(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Code => "a path",
        EvidenceKind.Commit => "a sha",
        EvidenceKind.PullRequest or EvidenceKind.Issue => "a url",
        EvidenceKind.Finding => "an inspection request id",
        EvidenceKind.Claim => "a claim key",
        _ => "a known kind",
    };

    private static IEnumerable<string> CitedKeys(ModelClaim claim) => claim.Evidence
        .Where(e => e.Kind == EvidenceKind.Claim && !string.IsNullOrWhiteSpace(e.ClaimKey))
        .Select(e => e.ClaimKey!)
        .Distinct(StringComparer.Ordinal);

    private static int Certainty(ClaimConfidence confidence) => confidence switch
    {
        ClaimConfidence.High => 3,
        ClaimConfidence.Medium => 2,
        _ => 1,
    };

    /// <summary>Reports at least one cycle for every group of claims that cite one another in a
    /// circle — each report is the path that closes it. A cycle reached again through a second
    /// path that converges on a node already finished is not reported a second time. Iterative, so
    /// a long citation chain cannot overflow the stack. A claim citing itself is reported by the
    /// reference checks above and skipped here.</summary>
    private static List<List<string>> CitationCycles(Dictionary<string, ModelClaim> byKey)
    {
        const int OnPath = 1, Done = 2;
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        var frames = new Stack<(string Key, IEnumerator<string> Next)>();
        var cycles = new List<List<string>>();

        void Enter(string key)
        {
            state[key] = OnPath;
            path.Add(key);
            IEnumerable<string> next = CitedKeys(byKey[key]).Where(k => k != key && byKey.ContainsKey(k)).ToList();
            frames.Push((key, next.GetEnumerator()));
        }

        foreach (var root in byKey.Keys)
        {
            if (state.ContainsKey(root))
            {
                continue;
            }

            Enter(root);
            while (frames.Count > 0)
            {
                var (key, next) = frames.Peek();
                if (!next.MoveNext())
                {
                    frames.Pop();
                    path.RemoveAt(path.Count - 1);
                    state[key] = Done;
                    continue;
                }

                var target = next.Current;
                if (!state.TryGetValue(target, out var seen))
                {
                    Enter(target);
                }
                else if (seen == OnPath)
                {
                    cycles.Add([.. path.Skip(path.IndexOf(target)), target]);
                }
            }
        }

        return cycles;
    }
}
