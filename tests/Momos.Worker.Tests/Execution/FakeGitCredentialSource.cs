using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>Answers every lookup with <see cref="Credential"/> (none by default — the anonymous clone).</summary>
internal sealed class FakeGitCredentialSource(GitCredential? credential = null) : IGitCredentialSource
{
    public GitCredential? Credential { get; } = credential;
    public List<string> RequestedUrls { get; } = [];

    public Task<GitCredential?> GetAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        RequestedUrls.Add(repositoryUrl);
        return Task.FromResult(Credential);
    }
}
