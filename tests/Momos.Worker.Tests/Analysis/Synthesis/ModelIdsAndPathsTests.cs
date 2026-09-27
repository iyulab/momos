using System.Text.RegularExpressions;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class ModelIdsAndPathsTests
{
    // The Host validator's rule for an Outline section path.
    private static readonly Regex HostSectionPath = new(@"^[a-z0-9][a-z0-9-]{0,63}\.md\z");

    [Fact]
    public void ASynthesizedClaimKey_IgnoresCaseAndSpacingInTheTopic() =>
        Assert.Equal(ModelIds.SynthesizedClaim("Request  state machine"), ModelIds.SynthesizedClaim(" request state MACHINE "));

    [Fact]
    public void ASynthesizedClaimKey_NeverCollidesWithADeterministicOne() =>
        Assert.NotEqual(ModelIds.Claim("component|src/App/App.csproj"), ModelIds.SynthesizedClaim("component|src/App/App.csproj"));

    [Fact]
    public void ASynthesizedClaimKey_LooksLikeEveryOtherClaimKey() =>
        Assert.Matches(@"^clm\.[0-9a-f]{12}\z", ModelIds.SynthesizedClaim("anything at all"));

    [Fact]
    public void ElementIds_CarryTheirKindAndDifferByKind()
    {
        Assert.Matches(@"^flw\.[0-9a-f]{12}\z", ModelIds.Element(ModelIds.FlowPrefix, "claim next"));
        Assert.NotEqual(
            ModelIds.Element(ModelIds.FlowPrefix, "claim next")[4..],
            ModelIds.Element(ModelIds.InvariantPrefix, "claim next")[4..]);
    }

    [Theory]
    [InlineData("System map", "system-map.md")]
    [InlineData("  Core flows: from request to report! ", "core-flows-from-request-to-report.md")]
    [InlineData("Index", "index-chapter.md")]
    [InlineData("Unknowns", "unknowns-chapter.md")]
    [InlineData("…", "chapter.md")]
    public void APath_IsDerivedFromTheTitle(string title, string expected)
    {
        var path = SectionPaths.FromTitle(title, new HashSet<string>());

        Assert.Equal(expected, path);
        Assert.Matches(HostSectionPath, path);
    }

    [Fact]
    public void ARepeatedTitle_GetsANumberedPath()
    {
        var taken = new HashSet<string>();

        Assert.Equal("risks.md", SectionPaths.FromTitle("Risks", taken));
        Assert.Equal("risks-2.md", SectionPaths.FromTitle("Risks", taken));
        Assert.Equal("risks-3.md", SectionPaths.FromTitle("risks", taken));
    }

    [Fact]
    public void AVeryLongTitle_StillYieldsAValidPath() =>
        Assert.Matches(HostSectionPath, SectionPaths.FromTitle(new string('a', 300), new HashSet<string>()));

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void OptionsWithANonPositiveLimit_AreInvalid(int chapters, long chapterTokens, long totalTokens, int minutes) =>
        Assert.False(AnalysisOptions.IsValid(new AnalysisOptions
        {
            MaxChapters = chapters,
            MaxChapterTokens = chapterTokens,
            MaxTotalTokens = totalTokens,
            MaxDuration = TimeSpan.FromMinutes(minutes),
        }));

    [Fact]
    public void TheDefaults_AreValid_AndLeaveRoomUnderTheHostsDefaultReclaimLease() =>
        Assert.True(AnalysisOptions.IsValid(new AnalysisOptions()) && new AnalysisOptions().MaxDuration < TimeSpan.FromMinutes(30));
}
