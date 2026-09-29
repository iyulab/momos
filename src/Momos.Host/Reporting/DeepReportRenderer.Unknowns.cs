using System.Text;
using Momos.Host.Domain;

namespace Momos.Host.Reporting;

public static partial class DeepReportRenderer
{
    private const string UnknownsPage = "unknowns.md";

    private static IEnumerable<ModelClaim> ReadingsToCheck(Tree tree) =>
        tree.Claims.Where(c => c.Confidence == ClaimConfidence.Low && c.Status == ClaimStatus.Proposed);

    private static IEnumerable<ModelClaim> Disputed(Tree tree) =>
        tree.Claims.Where(c => c.Status == ClaimStatus.Disputed);

    private static IEnumerable<ModelDecision> Unrecorded(ProjectModel model) =>
        model.Decisions.Where(d => d.Rationale == ModelDecision.Unrecorded);

    /// <summary>Chapters with no owner summary and no blocks, and no recorded reason: the analysis ran
    /// to the end and found nothing.</summary>
    private static IEnumerable<OutlineSection> EmptySections(ProjectModel model) =>
        model.Outline.Where(s => IsEmpty(s) && NotAnalyzedReason(model, s) is null);

    /// <summary>Chapters with nothing in them because the analysis did not get to them.</summary>
    internal static IEnumerable<(OutlineSection Section, string Reason)> NotAnalyzedSections(ProjectModel model) =>
        model.Outline.Where(IsEmpty).Select(s => (Section: s, Reason: NotAnalyzedReason(model, s))).Where(x => x.Reason is not null)
            .Select(x => (x.Section, x.Reason!));

    /// <summary>Synthesized claims that no chapter shows — neither as its owner summary, nor as a
    /// block, nor as backing for a block's element. Deterministic claims are left out: the claims
    /// table already lists them all.</summary>
    private static IEnumerable<ModelClaim> UnplacedClaims(ProjectModel model, Tree tree)
    {
        if (model.Outline.Count == 0)
        {
            return [];
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in model.Outline)
        {
            placed.UnionWith(section.OwnerSummaryClaims);
            foreach (var block in section.Blocks)
            {
                placed.UnionWith(BlockClaims(block, model, tree));
            }
        }

        return tree.Claims.Where(c => c.Origin == ClaimOrigin.Synthesized && !placed.Contains(c.Key));
    }

    /// <summary>The claims a block puts on its chapter's page: itself, or the claims backing its element.</summary>
    private static IEnumerable<string> BlockClaims(OutlineBlock block, ProjectModel model, Tree tree) => block.Kind switch
    {
        OutlineBlockKind.Claim => [block.Ref],
        OutlineBlockKind.Component when tree.Component(block.Ref) is { } c => c.Claims,
        OutlineBlockKind.Pattern when tree.Patterns.TryGetValue(block.Ref, out var p) => p.Claims,
        OutlineBlockKind.Decision when tree.Decisions.TryGetValue(block.Ref, out var d) => d.Claims,
        OutlineBlockKind.Intent when tree.Intents.TryGetValue(block.Ref, out var i) => i.Claims,
        OutlineBlockKind.Flow when tree.Flows.TryGetValue(block.Ref, out var f) => f.Claims.Concat(f.Steps.Select(st => st.ClaimKey)),
        OutlineBlockKind.Invariant when tree.Invariants.TryGetValue(block.Ref, out var inv) => inv.Claims,
        OutlineBlockKind.Map when block.Ref != ClaimValidator.MapEverything && tree.Component(block.Ref) is { } centre =>
            model.Relations.Where(r => r.From == centre.Id || r.To == centre.Id).SelectMany(r => r.Claims),
        _ => [],
    };

    private static int UnknownCount(ProjectModel model, Tree tree) =>
        (model.Coverage?.NotAnalyzed.Count ?? 1)
        + Unrecorded(model).Count()
        + ReadingsToCheck(tree).Count()
        + Disputed(tree).Count()
        + EmptySections(model).Count()
        + UnplacedClaims(model, tree).Count();

    /// <summary>The summary's link to the unknowns page, e.g. "[3 open questions](unknowns.md)".</summary>
    private static string UnknownsLink(ProjectModel model, Tree tree)
    {
        var count = UnknownCount(model, tree);
        return $"[{count} open question{(count == 1 ? "" : "s")}]({UnknownsPage})";
    }

    /// <summary>The unknowns page at <paramref name="page"/>, for chapter pages under
    /// <paramref name="chapterPrefix"/> and component and claim pages under <paramref name="evidencePrefix"/>
    /// (the same prefixes as <see cref="AppendixPages"/>), linking back to <paramref name="summaryPage"/>.</summary>
    internal static ReportDocument UnknownsDocument(
        ProjectModel model, string page, string chapterPrefix, string evidencePrefix, string summaryPage = "index.md")
    {
        var dir = DirOf(page);
        return new(page, Unknowns(model, new Tree(model), Links.From(dir, evidencePrefix, summaryPage, page), s => Relative(dir, $"{chapterPrefix}{s.Path}")));
    }

    private static string Unknowns(ProjectModel model, Tree tree, Links l, Func<OutlineSection, string> chapter)
    {
        var md = new StringBuilder()
            .Line("# What this report does not know")
            .Line()
            .Line($"Back to the [summary]({l.Summary}). Everything below is either unread, unexplained, or waiting for a developer's verdict — this page is kept even when it is short, because an empty one would be a claim too.")
            .Line();

        md.Line("## Areas not analyzed").Line();
        if (model.Coverage is null)
        {
            md.Line("This model version records no analysis coverage, so what the analysis left unread is unknown.").Line();
        }
        else if (model.Coverage.NotAnalyzed.Count == 0)
        {
            md.Line("The analysis recorded no unread areas.").Line();
        }
        else
        {
            foreach (var gap in model.Coverage.NotAnalyzed)
            {
                md.Line($"- **{Inline(gap.Area)}** — {Inline(gap.Reason)}");
            }

            md.Line();
        }

        var unrecorded = Unrecorded(model).ToList();
        if (unrecorded.Count > 0)
        {
            md.Line("## Why was this decided?").Line()
              .Line("These decisions are visible in the repository, but nothing in it records why they were made. To answer, record the reason in the repository — a commit message or a document — and analyze again: a history claim can then back it.")
              .Line();
            foreach (var d in unrecorded)
            {
                // "<summary> — why?" reads the same whether the summary is a sentence or a phrase.
                md.Line($"- {Inline(d.Summary.TrimEnd('.'))} — why? ({tree.ClaimLinks(d.Claims, l.Claims)})");
            }

            md.Line();
        }

        var readings = ReadingsToCheck(tree).ToList();
        if (readings.Count > 0)
        {
            md.Line("## Is this reading right?").Line()
              .Line($"Low-confidence claims no developer has judged yet. Answer with a verdict — Confirmed, Disputed, or Corrected — through `POST /projects/{model.ProjectId}/model/claims/{{claimKey}}/corrections`.")
              .Line();
            foreach (var c in readings)
            {
                md.Line($"- {Inline(c.Statement)} — {c.Tier} ({tree.ClaimLink(c.Key, l.Claims)})");
            }

            md.Line();
        }

        var disputed = Disputed(tree).ToList();
        if (disputed.Count > 0)
        {
            md.Line("## Disputed claims").Line()
              .Line("A developer said these are wrong and gave no correction; the model still holds them until an analysis says otherwise.")
              .Line();
            foreach (var c in disputed)
            {
                md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, l.Claims)})");
            }

            md.Line();
        }

        var notAnalyzed = NotAnalyzedSections(model).ToList();
        if (notAnalyzed.Count > 0)
        {
            md.Line("## Chapters not analyzed").Line()
              .Line("The analysis ran out of budget or stopped before it wrote these chapters; the repository may well hold evidence for them.")
              .Line();
            foreach (var (s, reason) in notAnalyzed)
            {
                md.Line($"- [{LinkText(s.Title)}]({chapter(s)}) — {Inline(reason)}");
            }

            md.Line();
        }

        var empty = EmptySections(model).ToList();
        if (empty.Count > 0)
        {
            md.Line("## Chapters without evidence").Line();
            foreach (var s in empty)
            {
                md.Line($"- [{LinkText(s.Title)}]({chapter(s)}) — no evidence for this chapter was found in this repository.");
            }

            md.Line();
        }

        var unplaced = UnplacedClaims(model, tree).ToList();
        if (unplaced.Count > 0)
        {
            md.Line("## Not placed in any chapter").Line()
              .Line("The analysis proposed these statements but no chapter shows them. They are still claims with evidence, on pages of their own.")
              .Line();
            foreach (var c in unplaced)
            {
                md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, l.Claims)})");
            }

            md.Line();
        }

        if (model.Coverage is { Rejected.Count: > 0 })
        {
            md.Line("## Discarded during analysis").Line()
              .Line("Statements the analysis proposed but threw away because their evidence did not hold up, by reason:")
              .Line();
            foreach (var r in model.Coverage.Rejected)
            {
                md.Line($"- {Inline(r.Reason)}: {r.Count}");
            }

            md.Line();
        }

        return md.ToString();
    }

    private static void Understanding(StringBuilder md, ProjectModel model, Tree tree)
    {
        int Count(ClaimStatus s) => tree.Claims.Count(c => c.Status == s);
        md.Line("## Understanding").Line()
          .Line("What developers have reviewed so far. This is not a validated metric — it counts what a person has looked at, not whether the model is right.")
          .Line()
          .Line("| | Count |").Line("|---|---|")
          .Line($"| Claims | {tree.Claims.Count} |")
          .Line($"| Confirmed | {Count(ClaimStatus.Confirmed)} |")
          .Line($"| Corrected | {Count(ClaimStatus.Corrected)} |")
          .Line($"| Disputed | {Count(ClaimStatus.Disputed)} |")
          .Line($"| Not yet reviewed | {Count(ClaimStatus.Proposed)} |")
          .Line($"| Decisions without a recorded rationale | {Unrecorded(model).Count()} |")
          .Line();
    }
}
