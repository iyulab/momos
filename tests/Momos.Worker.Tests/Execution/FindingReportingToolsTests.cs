using Microsoft.Extensions.AI;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class FindingReportingToolsTests
{
    [Fact]
    public void ReportFinding_WithEvidence_RecordsItInTheSink()
    {
        var sink = new FindingSink();
        var tools = new FindingReportingTools(sink);

        var result = tools.ReportFinding(FindingCategory.FunctionalDefect, "Crashes on empty input", "$ dotnet run\nUnhandled exception: NullReferenceException");

        Assert.Equal("Recorded.", result);
        var finding = Assert.Single(sink.Findings);
        Assert.Equal(FindingCategory.FunctionalDefect, finding.Category);
        Assert.Equal("Crashes on empty input", finding.Description);
        Assert.Contains("NullReferenceException", finding.Evidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReportFinding_WithoutEvidence_RejectsAndDoesNotRecord(string evidence)
    {
        var sink = new FindingSink();
        var tools = new FindingReportingTools(sink);

        var result = tools.ReportFinding(FindingCategory.UxConsistency, "Button label is misspelled", evidence);

        Assert.StartsWith("Rejected", result);
        Assert.Empty(sink.Findings);
    }

    [Fact]
    public async Task AsAIFunctionTool_InvokesThroughToTheSink()
    {
        var sink = new FindingSink();
        var tool = AIFunctionFactory.Create(new FindingReportingTools(sink).ReportFinding);

        await tool.InvokeAsync(new AIFunctionArguments
        {
            ["category"] = FindingCategory.UxConsistency,
            ["description"] = "Inconsistent button casing",
            ["evidence"] = "Header uses \"Log In\", nav bar uses \"log in\"",
        });

        var finding = Assert.Single(sink.Findings);
        Assert.Equal(FindingCategory.UxConsistency, finding.Category);
    }
}
