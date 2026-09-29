using Momos.Worker.Analysis;

namespace Momos.Worker.Tests.Analysis;

public class AnalysisOptionsTests
{
    [Fact]
    public void TheDefaultBudgets_FitInsideTheTotal()
    {
        var o = new AnalysisOptions();
        Assert.True(o.MaxOverviewTokens + o.MaxChapters * o.MaxChapterTokens <= o.MaxTotalTokens);
    }

    [Fact]
    public void TheDefaultDuration_LeavesTheHostsDefaultReclaimLease_AQuarterHour()
    {
        // The Host default is duplicated here on purpose: the two processes cannot read each other's
        // settings, and this test is where drifting apart shows up.
        var hostReclaimDefault = TimeSpan.FromMinutes(60);
        Assert.True(new AnalysisOptions().MaxDuration + TimeSpan.FromMinutes(15) <= hostReclaimDefault);
    }

    [Fact]
    public void TheDefaults_AreValid() => Assert.True(AnalysisOptions.IsValid(new AnalysisOptions()));
}
