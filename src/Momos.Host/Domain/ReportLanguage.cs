namespace Momos.Host.Domain;

/// <summary>
/// The languages a checkup report can be written in — the language model writes its prose in it
/// and the report's own pages use its fixed text. ISO 639-1 codes, lowercase.
/// </summary>
public static class ReportLanguage
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal) { "en", "ko" };

    /// <summary>True with a null language when nothing was given, true with the lowercase code for a
    /// supported language, false otherwise — an unsupported language is an error, never a silent default.</summary>
    public static bool TryNormalize(string? value, out string? language)
    {
        language = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var code = value.Trim().ToLowerInvariant();
        if (!Supported.Contains(code))
        {
            return false;
        }

        language = code;
        return true;
    }
}
