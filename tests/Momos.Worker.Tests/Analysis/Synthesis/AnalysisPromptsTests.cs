namespace Momos.Worker.Tests.Analysis.Synthesis;

using Momos.Worker.Analysis.Synthesis;

public sealed class AnalysisPromptsTests
{
    [Theory]
    [InlineData(null, "Write in English")]
    [InlineData("en", "Write in English")]
    [InlineData("ko", "Write in Korean")]
    public void TheSystemPrompt_NamesTheLanguageToWriteIn(string? language, string expected) =>
        Assert.Contains(expected, AnalysisPrompts.System(language), StringComparison.Ordinal);

    [Fact]
    public void AKoreanManual_KeepsQuotedTextAsItIs() =>
        Assert.Contains("quoted text exactly as they appear", AnalysisPrompts.System("ko"), StringComparison.Ordinal);

    [Fact]
    public void AnUnknownLanguage_IsABug() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisPrompts.System("ja"));
}
