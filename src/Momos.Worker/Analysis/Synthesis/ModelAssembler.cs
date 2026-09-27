using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>
/// Turns the deterministic skeleton and what the passes committed into the model that is
/// submitted: components gain what was said about them, chapters become the outline, and the
/// coverage says what was written, what was not and why, and how many proposals were turned away.
/// A last pruning pass removes anything whose references no longer resolve — proposals are
/// checked as they arrive, so this is a safeguard, and whatever it removes is counted.
/// </summary>
public static class ModelAssembler
{
    private const int MaxCoverageText = 200;

    public static ProjectModelPayload Assemble(
        SynthesisDraft draft, IReadOnlyList<CoverageGapPayload> passGaps, CoverageGeneratorPayload generator, int chaptersPlanned)
    {
        var skeleton = draft.Skeleton;
        var claims = skeleton.Claims.Concat(draft.Claims).ToList();
        var components = skeleton.Components.Select(c => draft.ComponentUpdates.TryGetValue(c.Id, out var u)
            ? c with { Responsibility = u.Responsibility, Claims = [.. c.Claims, .. u.Claims.Except(c.Claims)] }
            : c).ToList();
        var flows = draft.Flows.ToList();
        var invariants = draft.Invariants.ToList();
        var patterns = draft.Patterns.ToList();
        var decisions = draft.Decisions.ToList();
        var intents = draft.Intents.ToList();
        var sections = draft.Sections
            .Select(s => new OutlineSectionPayload(s.Id, s.Path, s.Title, s.Purpose, [.. s.OwnerSummaryClaims], [.. s.Blocks]))
            .ToList();

        var pruned = 0;
        bool changed;
        do
        {
            changed = false;
            var keys = claims.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var dangling = claims
                .Where(c => c.Evidence.Any(e => e.Kind == EvidenceKind.Claim && (e.ClaimKey is null || !keys.Contains(e.ClaimKey))))
                .ToList();
            if (dangling.Count > 0)
            {
                claims.RemoveAll(dangling.Contains);
                pruned += dangling.Count;
                keys.ExceptWith(dangling.Select(c => c.Key));
                changed = true;
            }

            IReadOnlyList<string> Keep(IReadOnlyList<string> refs) => [.. refs.Where(keys.Contains)];
            var tiers = claims.ToDictionary(c => c.Key, c => c.Tier, StringComparer.Ordinal);

            pruned += Prune(flows, f => f with { Claims = Keep(f.Claims), Steps = [.. f.Steps.Where(s => keys.Contains(s.ClaimKey))] },
                f => f.Claims.Count == 0 || f.Steps.Count == 0, ref changed);
            pruned += Prune(invariants, i => i with { Claims = Keep(i.Claims) }, i => i.Claims.Count == 0, ref changed);
            pruned += Prune(patterns, p => p with { Claims = Keep(p.Claims) }, p => p.Claims.Count == 0, ref changed);
            pruned += Prune(intents, i => i with { Claims = Keep(i.Claims) }, i => i.Claims.Count == 0, ref changed);
            pruned += Prune(decisions, d => d with { Claims = Keep(d.Claims) },
                d => d.Claims.Count == 0 || (d.Rationale != DraftRules.Unrecorded && !d.Claims.Any(k => tiers[k] == ClaimTier.History)), ref changed);
            components = [.. components.Select(c => c with { Claims = Keep(c.Claims) })];

            bool Resolves(OutlineBlockPayload b) => b.Kind switch
            {
                OutlineBlockKind.Claim => keys.Contains(b.Ref),
                OutlineBlockKind.Component => components.Any(c => c.Id == b.Ref),
                OutlineBlockKind.Map => b.Ref == DraftRules.MapEverything || components.Any(c => c.Id == b.Ref),
                OutlineBlockKind.Flow => flows.Any(f => f.Id == b.Ref),
                OutlineBlockKind.Invariant => invariants.Any(i => i.Id == b.Ref),
                OutlineBlockKind.Pattern => patterns.Any(p => p.Id == b.Ref),
                OutlineBlockKind.Decision => decisions.Any(d => d.Id == b.Ref),
                OutlineBlockKind.Intent => intents.Any(i => i.Id == b.Ref),
                _ => false,
            };

            for (var n = 0; n < sections.Count; n++)
            {
                var s = sections[n];
                var fixedUp = s with { OwnerSummaryClaims = Keep(s.OwnerSummaryClaims), Blocks = [.. s.Blocks.Where(Resolves)] };
                var removed = s.OwnerSummaryClaims.Count - fixedUp.OwnerSummaryClaims.Count + s.Blocks.Count - fixedUp.Blocks.Count;
                if (removed > 0)
                {
                    sections[n] = fixedUp;
                    pruned += removed;
                    changed = true;
                }
            }
        }
        while (changed);

        var written = sections.Count(s => s.Blocks.Count > 0 || s.OwnerSummaryClaims.Count > 0);
        var coverage = skeleton.Coverage;
        IReadOnlyList<CoverageAreaPayload> analyzed = written == 0
            ? coverage.Analyzed
            : [.. coverage.Analyzed, new CoverageAreaPayload("manual-synthesis",
                Fit($"{written} of {chaptersPlanned} chapters written by a language model; Synthesized claims passed evidence checks at the analyzed commit."))];
        var notAnalyzed = coverage.NotAnalyzed
            .Where(g => written == 0 || g.Area != "source-files")
            .Concat(passGaps)
            .Select(g => g with { Reason = Fit(g.Reason) })
            .ToList();
        var rejections = new Dictionary<string, int>(draft.Rejections, StringComparer.Ordinal);
        if (pruned > 0)
        {
            rejections[RejectionReason.UnresolvedReference] = rejections.GetValueOrDefault(RejectionReason.UnresolvedReference) + pruned;
        }

        var rejected = rejections
            .Where(r => r.Value > 0)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new CoverageRejectionPayload(r.Key, r.Value))
            .ToList();

        return new ProjectModelPayload(
            skeleton.BaseCommit, components, skeleton.Relations, patterns, decisions, intents, flows, invariants, sections,
            new CoveragePayload(analyzed, notAnalyzed, rejected, generator), claims);
    }

    /// <summary>Drops each item's unresolved references and removes the items left with nothing behind them.</summary>
    private static int Prune<T>(List<T> items, Func<T, T> keepResolved, Func<T, bool> isEmpty, ref bool changed)
    {
        var removed = 0;
        for (var n = items.Count - 1; n >= 0; n--)
        {
            var fixedUp = keepResolved(items[n]);
            if (isEmpty(fixedUp))
            {
                items.RemoveAt(n);
                removed++;
                changed = true;
            }
            else
            {
                items[n] = fixedUp;
            }
        }

        return removed;
    }

    private static string Fit(string text) => text.Length <= MaxCoverageText ? text : text[..(MaxCoverageText - 3)] + "...";
}
