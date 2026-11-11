using System.Text.RegularExpressions;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>
/// Whether a piece of text looks like a credential: a password or key assignment, a password inside
/// a URL, a well-known token prefix or a private key header. A repository may have such a value
/// committed; the analysis must not copy it into a claim, which is shown in reports and searched
/// from the knowledge index. The Host masks the same patterns when it renders or indexes text
/// (<c>SecretMasker</c>); the two are separate projects with no shared assembly, so a change to
/// this list needs the same change there.
/// </summary>
public static partial class SecretLikeText
{
    [GeneratedRegex(@"\b(?:password|pwd|passwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret)[""']?\s*[=:]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^;\s,""'&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyedValue();

    [GeneratedRegex(@"://[^/\s:@]+:[^@\s/]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyHeader();

    public static bool Matches(string? text) =>
        !string.IsNullOrEmpty(text)
        && (KeyedValue().IsMatch(text) || UrlPassword().IsMatch(text) || TokenPrefix().IsMatch(text) || PrivateKeyHeader().IsMatch(text));
}
