using Momos.Host.Endpoints;

namespace Momos.Host.Tests.Endpoints;

public class InspectionClaimOptionsTests
{
    [Fact]
    public void TheDefaultReclaimTimeout_IsAnHour()
    {
        // Duplicated in the Worker's AnalysisOptionsTests on purpose: the two processes cannot read
        // each other's settings, and these two tests are where the defaults drifting apart shows up.
        Assert.Equal(TimeSpan.FromMinutes(60), new InspectionClaimOptions().ReclaimTimeout);
    }
}
