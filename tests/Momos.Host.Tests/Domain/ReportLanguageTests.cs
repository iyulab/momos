using Momos.Host.Domain;

namespace Momos.Host.Tests.Domain;

public sealed class ReportLanguageTests
{
    [Theory]
    [InlineData(null, true, null)]
    [InlineData("  ", true, null)]
    [InlineData("ko", true, "ko")]
    [InlineData(" KO ", true, "ko")]
    [InlineData("en", true, "en")]
    [InlineData("ja", false, null)]
    [InlineData("korean", false, null)]
    public void TryNormalize_AcceptsOnlySupportedLanguages(string? input, bool ok, string? expected)
    {
        Assert.Equal(ok, ReportLanguage.TryNormalize(input, out var language));
        Assert.Equal(expected, language);
    }
}
