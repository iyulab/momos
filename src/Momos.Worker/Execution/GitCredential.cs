namespace Momos.Worker.Execution;

/// <summary>
/// A credential for one git host, as the Worker machine's own git configuration answers it.
/// <see cref="ToString"/> never prints the password, so the value cannot reach a log through
/// string formatting.
/// </summary>
public sealed record GitCredential(string Host, string Username, string Password)
{
    public override string ToString() => $"GitCredential {{ Host = {Host}, Username = {Username}, Password = *** }}";
}

/// <summary>
/// Looks up the credential the Worker machine holds for a repository URL. momos stores no
/// credentials of its own: a private repository is readable exactly when the account the
/// Worker runs under could clone it by hand.
/// </summary>
public interface IGitCredentialSource
{
    /// <summary>The credential for <paramref name="repositoryUrl"/>'s host, or null when the
    /// URL is not HTTPS or the machine has none to give.</summary>
    Task<GitCredential?> GetAsync(string repositoryUrl, CancellationToken cancellationToken = default);
}
