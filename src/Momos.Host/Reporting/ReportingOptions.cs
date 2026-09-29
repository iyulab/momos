using Momos.Host.Domain;

namespace Momos.Host.Reporting;

public sealed class ReportingOptions
{
    public const string SectionName = "Momos:Host:Reporting";

    /// <summary>The language of a checkup whose request and project name none.</summary>
    public string DefaultLanguage { get; set; } = "en";

    public static bool IsValid(ReportingOptions o) => ReportLanguage.TryNormalize(o.DefaultLanguage, out var l) && l is not null;
}
