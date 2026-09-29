using System.Globalization;
using System.Text;
using Momos.Host.Domain;
using static Momos.Host.Reporting.DeepReportRenderer;

namespace Momos.Host.Reporting;

/// <summary>
/// Renders a finished <see cref="Checkup"/> as its report: a small tree of markdown documents read
/// top-down and trusted bottom-up. The overview (<c>index.md</c>) states what was examined and what
/// this checkup could not do; <c>results.md</c> gives every exam program's status; <c>followups/</c>
/// holds what should be looked at again; the appendices hold the design analysis itself — its
/// chapters under <c>manual/</c>, every component and claim under <c>evidence/</c>, and
/// the raw exam record under <c>exams/</c>.
/// </summary>
/// <remarks>
/// There is no overall score: an exam that did not run, or ran only in part, is reported with its
/// reason and never folded into a verdict. The report's own sentences are in the checkup's language
/// (<see cref="ReportText"/>); free text is escaped exactly as in <see cref="DeepReportRenderer"/>,
/// and every relative link points at a document in the same tree.
/// </remarks>
public static class CheckupReportRenderer
{
    private const string Summary = "index.md";
    private const string Results = "results.md";
    private const string FollowUps = "followups/index.md";
    private const string ManualPrefix = "manual/";
    private const string ManualIndex = "manual/index.md";
    private const string Unknowns = "manual/unknowns.md";
    private const string EvidencePrefix = "evidence/";
    private const string EvidenceIndexPage = "evidence/index.md";

    private static string ExamRecord(string program) => $"exams/{program}.md";

    /// <param name="projectName">The project's name, shown in the title.</param>
    /// <param name="checkup">The checkup, with its exams.</param>
    /// <param name="model">The project model the design analysis produced, with its claims; null when
    /// it produced none.</param>
    public static IReadOnlyList<ReportDocument> Render(string projectName, Checkup checkup, ProjectModel? model)
    {
        var t = ReportText.For(checkup.Language);
        var design = checkup.Exams.FirstOrDefault(e => e.Program == ExamProgram.DesignAnalysis);
        var hasChapters = model is { Outline.Count: > 0 };

        var documents = new List<ReportDocument>
        {
            new(Summary, Index(projectName, checkup, model, t)),
            new(Results, ResultsPage(checkup, model, t)),
            new(FollowUps, FollowUpsPage(t)),
        };
        documents.AddRange(checkup.Exams.Select(e => new ReportDocument(ExamRecord(e.Program), ExamPage(checkup, e, model, t))));

        if (!hasChapters)
        {
            documents.Add(new(ManualIndex, ManualIndexPage(design, model, t)));
        }

        if (model is not null)
        {
            documents.Add(UnknownsDocument(model, Unknowns, ManualPrefix, EvidencePrefix, Summary));
            documents.Add(EvidenceIndex(model, EvidencePrefix, Summary));
            documents.AddRange(AppendixPages(model, ManualPrefix, EvidencePrefix, Summary, Unknowns));
        }

        return documents;
    }

    private static string Index(string projectName, Checkup checkup, ProjectModel? model, ReportText t)
    {
        var commit = checkup.BaseCommit ?? checkup.CommitRef;
        var md = new StringBuilder()
            .Line($"# {t.Title(Inline(projectName))}")
            .Line()
            .Line(t.Header(commit is null ? t.DefaultBranch : Code(commit), Date(checkup.CompletedAt ?? checkup.CreatedAt), checkup.Language))
            .Line();

        md.Line($"## {t.ExamStatusHeading}").Line();
        foreach (var program in ExamProgram.All)
        {
            var exam = checkup.Exams.FirstOrDefault(e => e.Program == program);
            md.Line($"- `{program}` — {(exam is null ? t.NotIncluded : $"**{t.Status(exam.Status)}**")}");
        }

        md.Line().Line($"[{t.ResultsTitle}]({Results})").Line();

        md.Line($"## {t.LimitsHeading}").Line().Line(t.LimitsIntro).Line();
        var limits = Limits(checkup, model, t).ToList();
        if (limits.Count == 0)
        {
            md.Line(t.NoLimits).Line();
        }
        else
        {
            foreach (var limit in limits)
            {
                md.Line($"- {limit}");
            }

            md.Line();
        }

        md.Line($"## {t.AppendicesHeading}").Line()
          .Line($"- [{t.ResultsTitle}]({Results})")
          .Line($"- [{t.FollowUpsTitle}]({FollowUps})");
        if (model is { Outline.Count: > 0 })
        {
            md.Line($"- {t.ManualTitle}");
            foreach (var section in model.Outline)
            {
                md.Line($"  - [{LinkText(section.Title)}]({ManualPrefix}{section.Path})");
            }
        }
        else
        {
            md.Line($"- [{t.ManualTitle}]({ManualIndex})");
        }

        if (model is not null)
        {
            md.Line($"- [{t.UnknownsTitle}]({Unknowns})")
              .Line($"- [{t.EvidenceTitle}]({EvidenceIndexPage})");
        }

        foreach (var exam in checkup.Exams)
        {
            md.Line($"- [{t.ExamRecordTitle(exam.Program)}]({ExamRecord(exam.Program)})");
        }

        return md.ToString();
    }

    /// <summary>What this checkup could not do, decided from its records alone: programs that did not
    /// run or did not finish, and chapters the analysis did not get to.</summary>
    private static IEnumerable<string> Limits(Checkup checkup, ProjectModel? model, ReportText t)
    {
        foreach (var exam in checkup.Exams)
        {
            var reason = exam.Reason is null ? null : Inline(exam.Reason);
            switch (exam.Status)
            {
                case ExamRunStatus.NotRun:
                    yield return t.DidNotRun(exam.Program, reason);
                    break;
                case ExamRunStatus.Partial:
                    yield return t.RanInPart(exam.Program, reason);
                    break;
                case ExamRunStatus.Pending:
                    yield return t.NotFinished(exam.Program);
                    break;
            }
        }

        if (model is not null)
        {
            var notAnalyzed = NotAnalyzedSections(model).Count();
            if (notAnalyzed > 0)
            {
                yield return t.ChaptersNotAnalyzed(notAnalyzed, $"[{t.UnknownsTitle}]({Unknowns})");
            }

            var partial = model.Outline.Count(s => !IsEmpty(s) && PartialReason(NotAnalyzedReason(model, s)) is not null);
            if (partial > 0)
            {
                yield return t.ChaptersPartial(partial);
            }
        }

        var notIncluded = ExamProgram.All.Where(p => checkup.Exams.All(e => e.Program != p)).Select(p => $"`{p}`").ToList();
        if (notIncluded.Count > 0)
        {
            yield return t.ProgramsNotIncluded(string.Join(", ", notIncluded));
        }
    }

    private static string ResultsPage(Checkup checkup, ProjectModel? model, ReportText t)
    {
        var md = new StringBuilder()
            .Line($"# {t.ResultsTitle}")
            .Line()
            .Line(t.BackToSummary(Summary))
            .Line()
            .Line(t.ResultsIntro)
            .Line()
            .Line($"| {t.ProgramColumn} | {t.StatusColumn} | {t.DetailColumn} |").Line("|---|---|---|");
        foreach (var program in ExamProgram.All)
        {
            var exam = checkup.Exams.FirstOrDefault(e => e.Program == program);
            if (exam is null)
            {
                md.Line($"| `{program}` | {t.NotIncluded} | |");
                continue;
            }

            var detail = new List<string>();
            if (exam.Reason is not null)
            {
                detail.Add(Inline(exam.Reason));
            }

            if (program == ExamProgram.DesignAnalysis && model is { Outline.Count: > 0 })
            {
                detail.Add(t.ChaptersWritten(model.Outline.Count(s => !IsEmpty(s)), model.Outline.Count));
            }

            md.Line($"| [`{program}`]({ExamRecord(program)}) | {t.Status(exam.Status)} | {Cell(string.Join(" · ", detail))} |");
        }

        return md.ToString();
    }

    private static string FollowUpsPage(ReportText t) => new StringBuilder()
        .Line($"# {t.FollowUpsTitle}")
        .Line()
        .Line(t.BackToSummary(Relative("followups/", Summary)))
        .Line()
        .Line(t.NoFollowUps)
        .ToString();

    private static string ExamPage(Checkup checkup, ExamRun exam, ProjectModel? model, ReportText t)
    {
        var page = ExamRecord(exam.Program);
        var dir = page[..(page.LastIndexOf('/') + 1)];
        var md = new StringBuilder()
            .Line($"# {t.ExamRecordTitle(exam.Program)}")
            .Line()
            .Line(t.BackToSummary(Relative(dir, Summary)))
            .Line()
            .Line("| | |").Line("|---|---|")
            .Line($"| {t.RequestLabel} | {Code(exam.RequestId.ToString())} |")
            .Line($"| {t.StatusColumn} | {t.Status(exam.Status)} |");
        if (exam.Reason is not null)
        {
            md.Line($"| {t.ReasonLabel} | {Cell(Inline(exam.Reason))} |");
        }

        // Only the design analysis produces a model; its version and commit are the checkup's.
        if (exam.Program == ExamProgram.DesignAnalysis && model is not null)
        {
            md.Line($"| {t.ModelVersionLabel} | {model.ModelVersion.ToString(CultureInfo.InvariantCulture)} |")
              .Line($"| {t.CommitLabel} | {Cell(Code(model.BaseCommit))} |");
        }

        md.Line();
        if (exam.Program != ExamProgram.DesignAnalysis)
        {
            return md.ToString();
        }

        if (model is null)
        {
            return md.Line(t.NoModel).ToString();
        }

        if (model.Coverage is not { } coverage)
        {
            return md.Line(t.NoCoverage).ToString();
        }

        md.Line($"## {t.AnalyzedHeading}").Line();
        List(md, coverage.Analyzed.Select(a => $"**{Inline(a.Area)}** — {Inline(a.Detail)}").ToList(), t);
        md.Line($"## {t.NotAnalyzedHeading}").Line();
        List(md, coverage.NotAnalyzed.Select(g => $"**{Inline(g.Area)}** — {Inline(g.Reason)}").ToList(), t);
        md.Line($"## {t.RejectedHeading}").Line();
        if (coverage.Rejected.Count == 0)
        {
            md.Line(t.NoneRecorded).Line();
        }
        else
        {
            md.Line($"| {t.ReasonLabel} | {t.CountColumn} |").Line("|---|---|");
            foreach (var r in coverage.Rejected)
            {
                md.Line($"| {Cell(Inline(r.Reason))} | {r.Count.ToString(CultureInfo.InvariantCulture)} |");
            }

            md.Line();
        }

        return md.ToString();
    }

    private static void List(StringBuilder md, IReadOnlyList<string> items, ReportText t)
    {
        if (items.Count == 0)
        {
            md.Line(t.NoneRecorded).Line();
            return;
        }

        foreach (var item in items)
        {
            md.Line($"- {item}");
        }

        md.Line();
    }

    /// <summary>The manual's page when it has no chapters: why the design analysis wrote none.</summary>
    private static string ManualIndexPage(ExamRun? design, ProjectModel? model, ReportText t)
    {
        var why = model is not null
            ? t.NoChapters
            : design is { Status: ExamRunStatus.NotRun }
                ? t.DidNotRun(ExamProgram.DesignAnalysis, design.Reason is null ? null : Inline(design.Reason))
                : t.NoModel;
        return new StringBuilder()
            .Line($"# {t.ManualTitle}")
            .Line()
            .Line(t.BackToSummary(Relative(ManualPrefix, Summary)))
            .Line()
            .Line(why)
            .ToString();
    }
}
