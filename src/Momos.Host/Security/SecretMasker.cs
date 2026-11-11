using System.Text.RegularExpressions;

namespace Momos.Host.Security;

/// <summary>
/// Replaces text that looks like a credential before it is copied into a report page or the
/// knowledge index. An analyzed repository may have a password or a token committed; quoting it as
/// evidence is fine for the check that the cited line exists, but republishing it as searchable
/// text is not. Stored evidence keeps the original (verification compares against the file); only
/// the copies made for readers and for search are masked.
/// <para>
/// The Worker rejects a proposal that quotes such text in the first place; its pattern list
/// (<c>SecretLikeText</c>) is the same list. They are two projects with no shared assembly, so a
/// change to one list needs the same change in the other.
/// </para>
/// </summary>
public static partial class SecretMasker
{
    public const string Replacement = "<REDACTED>";

    // The value after a keyword is replaced and the keyword kept, so the page still shows what kind
    // of thing was there. The rest replace the whole token.
    [GeneratedRegex(@"(?<key>\b(?:password|pwd|passwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret)[""']?\s*[=:]\s*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^;\s,""'&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyedValue();

    [GeneratedRegex(@"(?<=://[^/\s:@]+:)[^@\s/]+(?=@)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyHeader();

    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var masked = KeyedValue().Replace(text, m => m.Groups["key"].Value + Replacement);
        masked = UrlPassword().Replace(masked, Replacement);
        masked = TokenPrefix().Replace(masked, Replacement);
        return PrivateKeyHeader().Replace(masked, "-----BEGIN PRIVATE KEY (" + Replacement + ")-----");
    }
}
