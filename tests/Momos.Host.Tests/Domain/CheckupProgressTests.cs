using Momos.Host.Domain;

namespace Momos.Host.Tests.Domain;

public sealed class CheckupProgressTests
{
    private static ProjectModel Model(params CoverageGap[] notAnalyzed) => new()
    {
        ProjectId = Guid.NewGuid(),
        AnalysisRequestId = Guid.NewGuid(),
        ModelVersion = 1,
        BaseCommit = "abc",
        Coverage = new ModelCoverage([], [.. notAnalyzed], [], null),
    };

    [Fact]
    public void AModelWithEveryChapterWritten_CompletesTheDesignAnalysis() =>
        Assert.Equal(ExamRunStatus.Completed, CheckupProgress.DesignAnalysisOutcome(Model(new CoverageGap("source-files", "not read")), out _));

    [Fact]
    public void AChapterCutByItsBudget_MakesItPartial_AndSaysWhy()
    {
        var status = CheckupProgress.DesignAnalysisOutcome(Model(new CoverageGap("manual-chapter", "risks.md: partial — time budget reached")), out var reason);

        Assert.Equal(ExamRunStatus.Partial, status);
        Assert.Equal("risks.md: partial — time budget reached", reason);
    }
}
