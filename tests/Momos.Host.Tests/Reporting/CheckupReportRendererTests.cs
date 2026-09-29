using System.Globalization;
using Momos.Host.Domain;
using Momos.Host.Reporting;

namespace Momos.Host.Tests.Reporting;

public sealed class CheckupReportRendererTests
{
    private static Checkup Checkup(string language, ExamRunStatus status, string? reason = null) => new()
    {
        ProjectId = Guid.NewGuid(),
        Language = language,
        BaseCommit = "abc1234",
        ModelVersion = 3,
        Status = CheckupStatus.Completed,
        CompletedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z", CultureInfo.InvariantCulture),
        Exams = [new ExamRun { Program = ExamProgram.DesignAnalysis, RequestId = Guid.NewGuid(), Status = status, Reason = reason }],
    };

    private static string Doc(IReadOnlyList<ReportDocument> tree, string path) => Assert.Single(tree, d => d.Path == path).Content;

    [Fact]
    public void TheReport_FollowsTheCheckupLayout()
    {
        var docs = CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel());
        var paths = docs.Select(d => d.Path).ToList();

        Assert.Equal(["index.md", "results.md", "followups/index.md", "exams/design-analysis.md"], paths.Take(4));
        Assert.Contains(paths, p => p.StartsWith("manual/", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.StartsWith("evidence/claims/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("claims/", StringComparison.Ordinal));
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AKoreanCheckup_WritesItsOwnPagesInKorean() =>
        Assert.Contains("검진 결과", CheckupReportRenderer.Render("acme", Checkup("ko", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel())[0].Content);

    [Fact]
    public void ADesignAnalysisThatNeverRan_StillGivesAReport_ThatSaysWhy()
    {
        var docs = CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.NotRun, "clone failed"), model: null);

        Assert.Contains("clone failed", docs.Single(d => d.Path == "manual/index.md").Content);
        Assert.Contains("clone failed", docs.Single(d => d.Path == "results.md").Content);
    }

    [Fact]
    public void ProgramsThisCheckupDidNotInclude_AreSaidToBeNotIncluded_NotNormal()
    {
        var results = CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel()).Single(d => d.Path == "results.md").Content;

        Assert.Contains("static-quality", results);
        Assert.Contains("Not included in this checkup", results);
        Assert.DoesNotContain("Normal", results);
    }

    [Fact]
    public void EveryRelativeLink_PointsAtADocumentInTheTree()
    {
        DeepReportRendererTests.AssertEveryRelativeLinkResolves(
            CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel()));
        DeepReportRendererTests.AssertEveryRelativeLinkResolves(
            CheckupReportRenderer.Render("acme", Checkup("ko", ExamRunStatus.NotRun, "clone failed"), model: null));
    }

    [Fact]
    public void RepositoryTextInAChapterTitle_StaysText()
    {
        var docs = CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel(m =>
            m.Outline[0] = m.Outline[0] with { Title = "Links like [[cite:1]] and <b>bold</b>" }));

        foreach (var page in new[] { Doc(docs, "index.md"), Doc(docs, "manual/system-map.md") })
        {
            // A chapter title shown as link text also has its remaining brackets escaped, so the
            // wiki-link pair is broken at its first bracket either way.
            Assert.Contains("&#91;", page);
            Assert.Contains("&lt;b&gt;", page);
            Assert.DoesNotContain("[[cite:1]]", page);
            Assert.DoesNotContain("<b>", page);
        }
    }

    [Fact]
    public void TheIndex_StatesWhatThisCheckupCouldNotDo_AndHasNoOverallVerdict()
    {
        var model = DeepReportRendererTests.WithCoverage(DeepReportRendererTests.SampleModel(), new ModelCoverage(
            [], [new CoverageGap("manual-chapter", "risks.md: token budget reached")], [], null));
        var index = Doc(CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Partial, "token budget reached"), model), "index.md");

        Assert.StartsWith("# acme checkup results\n", index);
        Assert.Contains("`abc1234`", index);
        Assert.Contains("2026-09-30", index);
        Assert.Contains("## Limits of this checkup", index);
        Assert.Contains("token budget reached", index);
        Assert.Contains("static-quality", index);
        Assert.Contains("1 chapter", index);
        Assert.DoesNotContain("Opinion", index);
    }

    [Fact]
    public void FollowUps_SayThereAreNone_BecauseNoProgramRaisesThem()
    {
        var followups = Doc(CheckupReportRenderer.Render("acme", Checkup("en", ExamRunStatus.Completed), DeepReportRendererTests.SampleModel()), "followups/index.md");

        Assert.Contains("No follow-ups", followups);
    }

    [Fact]
    public void TheExamPage_CarriesTheRawRecordOfTheDesignAnalysis()
    {
        var checkup = Checkup("en", ExamRunStatus.Completed);
        var model = DeepReportRendererTests.WithCoverage(DeepReportRendererTests.SampleModel(), new ModelCoverage(
            [new CoverageArea("source-files", "12 files")], [new CoverageGap("history", "shallow clone")], [new CoverageRejection("evidence out of range", 2)], null));
        var exam = Doc(CheckupReportRenderer.Render("acme", checkup, model), "exams/design-analysis.md");

        Assert.Contains(checkup.Exams[0].RequestId.ToString(), exam);
        Assert.Contains("12 files", exam);
        Assert.Contains("shallow clone", exam);
        Assert.Contains("evidence out of range", exam);
        Assert.Contains("| 2 |", exam);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public void EveryPage_EndsLinesInLf(string language)
    {
        var docs = CheckupReportRenderer.Render("acme", Checkup(language, ExamRunStatus.Completed), DeepReportRendererTests.SampleModel());

        Assert.All(docs, d => Assert.DoesNotContain('\r', d.Content));
    }

    [Fact]
    public void ReportText_RefusesALanguageItHasNoTextFor() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportText.For("ja"));
}
