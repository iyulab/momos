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

    /// <summary>Chapters the analysis planned but found nothing for: no owner summary and no blocks.</summary>
    private static IEnumerable<OutlineSection> EmptySections(ProjectModel model) =>
        model.Outline.Where(s => s.OwnerSummaryClaims.Count == 0 && s.Blocks.Count == 0);

    private static int UnknownCount(ProjectModel model, Tree tree) =>
        (model.Coverage?.NotAnalyzed.Count ?? 1)
        + Unrecorded(model).Count()
        + ReadingsToCheck(tree).Count()
        + Disputed(tree).Count()
        + EmptySections(model).Count();

    /// <summary>The summary's link to the unknowns page, e.g. "[3 open questions](unknowns.md)".</summary>
    private static string UnknownsLink(ProjectModel model, Tree tree)
    {
        var count = UnknownCount(model, tree);
        return $"[{count} open question{(count == 1 ? "" : "s")}]({UnknownsPage})";
    }

    private static string Unknowns(ProjectModel model, Tree tree)
    {
        var md = new StringBuilder()
            .Line("# What this report does not know")
            .Line()
            .Line("Back to the [summary](index.md). Everything below is either unread, unexplained, or waiting for a developer's verdict — this page is kept even when it is short, because an empty one would be a claim too.")
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
                md.Line($"- {Inline(d.Summary.TrimEnd('.'))} — why? ({tree.ClaimLinks(d.Claims, "claims/")})");
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
                md.Line($"- {Inline(c.Statement)} — {c.Tier} ({tree.ClaimLink(c.Key, "claims/")})");
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
                md.Line($"- {Inline(c.Statement)} ({tree.ClaimLink(c.Key, "claims/")})");
            }

            md.Line();
        }

        var empty = EmptySections(model).ToList();
        if (empty.Count > 0)
        {
            md.Line("## Chapters without evidence").Line();
            foreach (var s in empty)
            {
                md.Line($"- [{LinkText(s.Title)}]({s.Path}) — no evidence for this chapter was found in this repository.");
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
