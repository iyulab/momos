namespace Momos.Worker.Execution;

/// <summary>
/// Which repositories the Worker may clone with its machine's git credential. Empty by default:
/// a repository URL comes from whoever registered the project, so without this list anyone who
/// can register a project could have the Worker read any repository its credential reaches.
/// </summary>
public sealed class GitCredentialOptions
{
    public const string SectionName = "Momos:Worker:Checkout";

    /// <summary>
    /// HTTPS URLs: a repository (<c>https://github.com/acme/app</c>, with or without
    /// <c>.git</c>), or everything under an owner when the entry ends with a slash
    /// (<c>https://github.com/acme/</c>). Any other repository is cloned anonymously.
    /// </summary>
    public List<string> CredentialedRepositories { get; set; } = [];

    internal bool Allows(Uri repository) => CredentialedRepositories.Any(entry =>
        Uri.TryCreate(entry, UriKind.Absolute, out var allowed)
        && string.Equals(allowed.Scheme, repository.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(allowed.Authority, repository.Authority, StringComparison.OrdinalIgnoreCase)
        && (allowed.AbsolutePath.EndsWith('/')
            ? repository.AbsolutePath.StartsWith(allowed.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(WithoutGitSuffix(allowed.AbsolutePath), WithoutGitSuffix(repository.AbsolutePath), StringComparison.OrdinalIgnoreCase)));

    internal static bool IsValidEntry(string entry) =>
        Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        && uri.AbsolutePath.Trim('/').Length > 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static string WithoutGitSuffix(string path) =>
        path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
}
