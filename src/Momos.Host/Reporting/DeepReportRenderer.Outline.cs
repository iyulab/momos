using System.Text;
using Momos.Host.Domain;

namespace Momos.Host.Reporting;

public static partial class DeepReportRenderer
{
    private const string NoEvidence = "No evidence for this chapter was found in this repository.";

    private const string PartialPrefix = "partial — ";

    /// <summary>Why the analysis did not (fully) write <paramref name="section"/>, as recorded in the
    /// model's coverage — the text after "&lt;chapter path&gt;:", including a leading "partial — " —
    /// or null when coverage says nothing about the chapter.</summary>
    internal static string? NotAnalyzedReason(ProjectModel model, OutlineSection section)
    {
        var start = $"{section.Path}:";
        var gap = model.Coverage?.NotAnalyzed.FirstOrDefault(g =>
            g.Area == "manual-chapter" && g.Reason.StartsWith(start, StringComparison.Ordinal));
        return gap?.Reason[start.Length..].Trim();
    }

    /// <summary>The reason after the "partial — " marker, or null when <paramref name="reason"/> does not carry it.</summary>
    internal static string? PartialReason(string? reason) =>
        reason is not null && reason.StartsWith(PartialPrefix, StringComparison.Ordinal) ? reason[PartialPrefix.Length..].Trim() : null;

    /// <summary>True when the chapter has neither an owner summary nor any block — nothing was written in it.</summary>
    internal static bool IsEmpty(OutlineSection s) => s.OwnerSummaryClaims.Count == 0 && s.Blocks.Count == 0;

    /// <summary>What an empty chapter says: that the analysis did not get to it (and why) when coverage
    /// records that, and that the repository holds no evidence only when the analysis ran to the end.</summary>
    private static string EmptyChapterText(ProjectModel model, OutlineSection section) =>
        NotAnalyzedReason(model, section) is { } reason
            ? $"This chapter was not analyzed: {Inline((PartialReason(reason) ?? reason).TrimEnd('.'))}."
            : NoEvidence;

    private static string OutlineIndex(string projectName, ProjectModel model, Tree tree)
    {
        var md = new StringBuilder()
            .Line($"# {Inline(projectName)} — design report")
            .Line()
            .Line($"Project model version {model.ModelVersion} at commit {Code(model.BaseCommit)}, generated {Date(model.CreatedAt)}.")
            .Line("Every statement in this report is a graded claim with evidence, on a page of its own. A developer verdict never replaces the original claim — both are shown.")
            .Line("Origin says how a claim was produced: Deterministic claims are read mechanically from the repository; Synthesized claims are a language model's reading of the same evidence.")
            .Line()
            .Line($"What this report does not know: {UnknownsLink(model, tree)}.")
            .Line();
        Understanding(md, model, tree);

        md.Line("## Chapters").Line();
        foreach (var s in model.Outline)
        {
            md.Line($"### [{LinkText(s.Title)}]({s.Path})").Line();
            if (!string.IsNullOrWhiteSpace(s.Purpose))
            {
                md.Line($"_{Subtitle(s.Purpose)}_").Line();
            }

            if (s.OwnerSummaryClaims.Select(tree.Claim).OfType<ModelClaim>().Any())
            {
                OwnerSummary(md, s, tree, "claims/", emptyText: null);
            }
            else if (s.Blocks.Count == 0)
            {
                md.Line(EmptyChapterText(model, s)).Line();
            }
            else
            {
                ChapterPreview(md, s, tree);
            }
        }

        // Chapters cite only some components and claims; these keep every page reachable.
        Structure(md, model, tree);
        ClaimsTable(md, tree);
        return md.ToString();
    }

    private const int PreviewClaims = 3;

    /// <summary>For a chapter without an owner summary: its first statements, so the index shows
    /// what the chapter holds instead of a bare title.</summary>
    private static void ChapterPreview(StringBuilder md, OutlineSection section, Tree tree)
    {
        var claims = section.Blocks
            .Where(b => b.Kind == OutlineBlockKind.Claim)
            .Select(b => tree.Claim(b.Ref))
            .OfType<ModelClaim>()
            .ToList();
        if (claims.Count == 0)
        {
            return;
        }

        foreach (var c in claims.Take(PreviewClaims))
        {
            md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, "claims/")})");
        }

        if (claims.Count > PreviewClaims)
        {
            md.Line($"— and {claims.Count - PreviewClaims} more in the chapter");
        }

        md.Line();
    }

    private static string SectionPage(OutlineSection section, ProjectModel model, Tree tree, Links l)
    {
        // LinkText: a heading must not turn into a link, whatever the title says.
        var md = new StringBuilder().Line($"# {LinkText(section.Title)}").Line();
        if (!string.IsNullOrWhiteSpace(section.Purpose))
        {
            // A heading, not a statement: shown as a subtitle so it cannot read as a claim.
            md.Line($"_{Subtitle(section.Purpose)}_").Line();
        }

        if (!IsEmpty(section) && PartialReason(NotAnalyzedReason(model, section)) is { } partial)
        {
            md.Line($"This chapter is partial: {Inline(partial.TrimEnd('.'))}.").Line();
        }

        md.Line($"Back to the [summary]({l.Summary}).").Line();
        if (IsEmpty(section))
        {
            md.Line(EmptyChapterText(model, section)).Line();
            return md.ToString();
        }

        md.Line("## Owner summary").Line();
        OwnerSummary(md, section, tree, l.Claims, emptyText: "No owner summary was produced for this chapter.");
        if (section.Blocks.Count > 0)
        {
            md.Line("## Details").Line();
            foreach (var block in section.Blocks)
            {
                RenderBlock(md, block, model, tree, l);
            }
        }

        return md.ToString();
    }

    private static void OwnerSummary(StringBuilder md, OutlineSection section, Tree tree, string claimsPrefix, string? emptyText)
    {
        var claims = section.OwnerSummaryClaims.Select(tree.Claim).OfType<ModelClaim>().ToList();
        if (claims.Count == 0)
        {
            if (emptyText is not null)
            {
                md.Line(emptyText).Line();
            }

            return;
        }

        foreach (var c in claims)
        {
            md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, claimsPrefix)})");
        }

        md.Line();
    }

    private static void RenderBlock(StringBuilder md, OutlineBlock block, ProjectModel model, Tree tree, Links l)
    {
        string Backed(IReadOnlyList<string> keys) => $"_Backed by {tree.ClaimLinks(keys, l.Claims)}._";
        switch (block.Kind)
        {
            case OutlineBlockKind.Claim when tree.Claim(block.Ref) is { } c:
                md.Line($"- {Inline(c.Statement)} — {c.Tier} · {c.Confidence} ({tree.ClaimLink(c.Key, l.Claims)})").Line();
                break;
            case OutlineBlockKind.Component when tree.Component(block.Ref) is { } c:
                md.Line($"### Component: {tree.ComponentLink(c.Id, l.Components)}").Line();
                if (!string.IsNullOrWhiteSpace(c.Responsibility))
                {
                    md.Line(Block(c.Responsibility)).Line().Line(Backed(c.Claims)).Line();
                }
                else
                {
                    md.Line($"{Inline(c.Kind)} ({tree.ClaimLinks(c.Claims, l.Claims)})").Line();
                }

                break;
            case OutlineBlockKind.Pattern when tree.Patterns.TryGetValue(block.Ref, out var p):
                md.Line($"### Pattern: {Inline(p.Name)}").Line()
                  .Line($"Applies to {string.Join(", ", p.AppliesTo.Select(id => tree.ComponentLink(id, l.Components)))}.").Line()
                  .Line(Backed(p.Claims)).Line();
                break;
            case OutlineBlockKind.Decision when tree.Decisions.TryGetValue(block.Ref, out var d):
                md.Line($"### Decision: {Inline(d.Summary)}").Line()
                  .Line(d.Rationale == ModelDecision.Unrecorded
                      ? $"Rationale: _unrecorded_ — asked in [what this report does not know]({l.Unknowns})."
                      : $"Rationale: {Inline(d.Rationale)}").Line();
                if (d.Alternatives.Count > 0)
                {
                    md.Line($"Alternatives: {string.Join("; ", d.Alternatives.Select(Inline))}.").Line();
                }

                md.Line(Backed(d.Claims)).Line();
                break;
            case OutlineBlockKind.Intent when tree.Intents.TryGetValue(block.Ref, out var i):
                md.Line($"### Intent: {Inline(i.Statement)}").Line()
                  .Line($"Source: {i.Source}.").Line()
                  .Line(Backed(i.Claims)).Line();
                break;
            case OutlineBlockKind.Flow when tree.Flows.TryGetValue(block.Ref, out var f):
                md.Line($"### Flow: {Inline(f.Name)}").Line();
                for (var n = 0; n < f.Steps.Count; n++)
                {
                    var step = f.Steps[n];
                    var where = step.ComponentId is null ? "" : $"{tree.ComponentLink(step.ComponentId, l.Components)} — ";
                    var what = tree.Claim(step.ClaimKey) is { } c ? Inline(c.Statement) : "";
                    md.Line($"{n + 1}. {where}{what} ({tree.ClaimLink(step.ClaimKey, l.Claims)})");
                }

                md.Line().Line(Backed(f.Claims)).Line();
                break;
            case OutlineBlockKind.Invariant when tree.Invariants.TryGetValue(block.Ref, out var inv):
                md.Line($"### Invariant: {Inline(inv.Statement)}").Line()
                  .Line($"Kind: {Inline(inv.Kind)}." + (inv.AppliesTo.Count > 0
                      ? $" Applies to {string.Join(", ", inv.AppliesTo.Select(id => tree.ComponentLink(id, l.Components)))}."
                      : "")).Line()
                  .Line(Backed(inv.Claims)).Line();
                break;
            case OutlineBlockKind.Map when block.Ref == ClaimValidator.MapEverything:
                md.Line("### Map: whole structure").Line();
                Mermaid(md, tree.Components, model.Relations);
                md.Line();
                break;
            case OutlineBlockKind.Map when tree.Component(block.Ref) is { } centre:
                var around = model.Relations.Where(r => r.From == centre.Id || r.To == centre.Id).ToList();
                var ids = around.SelectMany(r => new[] { r.From, r.To }).Append(centre.Id).ToHashSet(StringComparer.Ordinal);
                md.Line($"### Map: {LinkText(centre.Name)}").Line();
                Mermaid(md, tree.Components.Where(c => ids.Contains(c.Id)).ToList(), around);
                md.Line().Line($"_Backed by {tree.ClaimLinks(around.SelectMany(r => r.Claims).Distinct(StringComparer.Ordinal), l.Claims)}._").Line();
                break;
            default:
                // Validation rejects a block that does not resolve; a stored model that predates a
                // rule still renders, naming what it could not find instead of inventing it.
                md.Line($"- {Code($"{block.Kind}:{block.Ref}")} — not found in this model version.").Line();
                break;
        }
    }
}
