using System.Text;
using Momos.Host.Domain;

namespace Momos.Host.Reporting;

public static partial class DeepReportRenderer
{
    private const string NoEvidence = "No evidence for this chapter was found in this repository.";

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
                md.Line($"_{Inline(s.Purpose)}_").Line();
            }

            OwnerSummary(md, s, tree, emptyText: s.Blocks.Count == 0 ? NoEvidence : null);
        }

        return md.ToString();
    }

    private static string SectionPage(OutlineSection section, ProjectModel model, Tree tree)
    {
        var md = new StringBuilder().Line($"# {Inline(section.Title)}").Line();
        if (!string.IsNullOrWhiteSpace(section.Purpose))
        {
            // A heading, not a statement: shown as a subtitle so it cannot read as a claim.
            md.Line($"_{Inline(section.Purpose)}_").Line();
        }

        md.Line($"Back to the [summary](index.md).").Line();
        if (section.OwnerSummaryClaims.Count == 0 && section.Blocks.Count == 0)
        {
            md.Line(NoEvidence).Line();
            return md.ToString();
        }

        md.Line("## Owner summary").Line();
        OwnerSummary(md, section, tree, emptyText: "No owner summary was produced for this chapter.");
        if (section.Blocks.Count > 0)
        {
            md.Line("## Details").Line();
            foreach (var block in section.Blocks)
            {
                RenderBlock(md, block, model, tree);
            }
        }

        return md.ToString();
    }

    private static void OwnerSummary(StringBuilder md, OutlineSection section, Tree tree, string? emptyText)
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
            md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, "claims/")})");
        }

        md.Line();
    }

    private static void RenderBlock(StringBuilder md, OutlineBlock block, ProjectModel model, Tree tree)
    {
        string Backed(IReadOnlyList<string> keys) => $"_Backed by {tree.ClaimLinks(keys, "claims/")}._";
        switch (block.Kind)
        {
            case OutlineBlockKind.Claim when tree.Claim(block.Ref) is { } c:
                md.Line($"- {Inline(c.Statement)} — {c.Tier} · {c.Confidence} ({tree.ClaimLink(c.Key, "claims/")})").Line();
                break;
            case OutlineBlockKind.Component when tree.Component(block.Ref) is { } c:
                md.Line($"### Component: {tree.ComponentLink(c.Id, "components/")}").Line();
                if (!string.IsNullOrWhiteSpace(c.Responsibility))
                {
                    md.Line(Block(c.Responsibility)).Line().Line(Backed(c.Claims)).Line();
                }
                else
                {
                    md.Line($"{Inline(c.Kind)} ({tree.ClaimLinks(c.Claims, "claims/")})").Line();
                }

                break;
            case OutlineBlockKind.Pattern when tree.Patterns.TryGetValue(block.Ref, out var p):
                md.Line($"### Pattern: {Inline(p.Name)}").Line()
                  .Line($"Applies to {string.Join(", ", p.AppliesTo.Select(id => tree.ComponentLink(id, "components/")))}.").Line()
                  .Line(Backed(p.Claims)).Line();
                break;
            case OutlineBlockKind.Decision when tree.Decisions.TryGetValue(block.Ref, out var d):
                md.Line($"### Decision: {Inline(d.Summary)}").Line()
                  .Line(d.Rationale == ModelDecision.Unrecorded
                      ? $"Rationale: _unrecorded_ — asked in [what this report does not know]({UnknownsPage})."
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
                    var where = step.ComponentId is null ? "" : $"{tree.ComponentLink(step.ComponentId, "components/")} — ";
                    var what = tree.Claim(step.ClaimKey) is { } c ? Inline(c.Statement) : "";
                    md.Line($"{n + 1}. {where}{what} ({tree.ClaimLink(step.ClaimKey, "claims/")})");
                }

                md.Line().Line(Backed(f.Claims)).Line();
                break;
            case OutlineBlockKind.Invariant when tree.Invariants.TryGetValue(block.Ref, out var inv):
                md.Line($"### Invariant: {Inline(inv.Statement)}").Line()
                  .Line($"Kind: {Inline(inv.Kind)}." + (inv.AppliesTo.Count > 0
                      ? $" Applies to {string.Join(", ", inv.AppliesTo.Select(id => tree.ComponentLink(id, "components/")))}."
                      : "")).Line()
                  .Line(Backed(inv.Claims)).Line();
                break;
            default:
                // Validation rejects a block that does not resolve; a stored model that predates a
                // rule still renders, naming what it could not find instead of inventing it.
                md.Line($"- {Code($"{block.Kind}:{block.Ref}")} — not found in this model version.").Line();
                break;
        }
    }
}
