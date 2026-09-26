using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>
/// Runs the real host git against a throwaway configuration, so the lookup is exercised the
/// way the Worker machine's own credential helper would answer it.
/// </summary>
public sealed class HostGitCredentialSourceTests : IDisposable
{
    private const string AnsweringHelper = "!f() { echo username=worker; echo password=from-helper; }; f";

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"momos-git-config-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_configPath);

    private HostGitCredentialSource SourceWithHelper(string? helper, params string[] credentialedRepositories)
    {
        File.WriteAllText(_configPath, helper is null ? "" : $"[credential]\n\thelper = \"{helper}\"\n");
        var options = new GitCredentialOptions
        {
            CredentialedRepositories = credentialedRepositories.Length > 0 ? [.. credentialedRepositories] : ["https://github.com/acme/"],
        };
        return new HostGitCredentialSource(Options.Create(options), NullLogger<HostGitCredentialSource>.Instance)
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["GIT_CONFIG_GLOBAL"] = _configPath,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            },
        };
    }

    [Fact]
    public async Task GetAsync_ReturnsWhatTheMachinesHelperAnswers_ForTheUrlsHost()
    {
        var source = SourceWithHelper(AnsweringHelper);

        var credential = await source.GetAsync("https://github.com/acme/private.git");

        Assert.Equal(new GitCredential("github.com", "worker", "from-helper"), credential);
    }

    [Fact]
    public async Task GetAsync_WithNoHelperConfigured_ReturnsNull_InsteadOfWaitingOnAPrompt()
    {
        var source = SourceWithHelper(null);

        var lookup = source.GetAsync("https://github.com/acme/public.git");
        var finished = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(lookup, finished);
        Assert.Null(await lookup);
    }

    [Theory]
    [InlineData("http://github.com/acme/repo.git")]
    [InlineData("git@github.com:acme/repo.git")]
    [InlineData("https://someone@github.com/acme/repo.git")]
    [InlineData("/srv/repos/acme")]
    public async Task GetAsync_ForAUrlThatShouldNotCarryTheMachinesCredential_ReturnsNull(string url)
    {
        var source = SourceWithHelper(AnsweringHelper);

        Assert.Null(await source.GetAsync(url));
    }

    [Fact]
    public async Task GetAsync_WithNothingListed_ReturnsNull_ForEveryRepository()
    {
        File.WriteAllText(_configPath, $"[credential]\n\thelper = \"{AnsweringHelper}\"\n");
        var source = new HostGitCredentialSource(Options.Create(new GitCredentialOptions()), NullLogger<HostGitCredentialSource>.Instance)
        {
            EnvironmentOverrides = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = _configPath, ["GIT_CONFIG_NOSYSTEM"] = "1" },
        };

        Assert.Null(await source.GetAsync("https://github.com/acme/private.git"));
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git", "https://github.com/acme/")]
    [InlineData("https://GitHub.com/Acme/App.git", "https://github.com/acme/")]
    [InlineData("https://github.com/acme/app.git", "https://github.com/acme/app")]
    [InlineData("https://github.com/acme/app", "https://github.com/acme/app.git")]
    public async Task GetAsync_ForAListedRepositoryOrOwner_AsksTheMachine(string url, string listed)
    {
        var source = SourceWithHelper(AnsweringHelper, listed);

        Assert.NotNull(await source.GetAsync(url));
    }

    [Theory]
    [InlineData("https://github.com/other/app.git", "https://github.com/acme/")]
    [InlineData("https://github.com/acme/app-fork.git", "https://github.com/acme/app")]
    [InlineData("https://github.com/acme/../other/app.git", "https://github.com/acme/")]
    [InlineData("https://gitlab.com/acme/app.git", "https://github.com/acme/")]
    public async Task GetAsync_ForARepositoryNotListed_ReturnsNull_WithoutAskingTheMachine(string url, string listed)
    {
        var source = SourceWithHelper(AnsweringHelper, listed);

        Assert.Null(await source.GetAsync(url));
    }

    [Theory]
    [InlineData("https://github.com/acme/", true)]
    [InlineData("https://github.com/acme/app", true)]
    [InlineData("http://github.com/acme/", false)]
    [InlineData("https://user:token@github.com/acme/", false)]
    [InlineData("https://github.com/", false)]
    [InlineData("github.com/acme/", false)]
    public void CredentialedRepositoryEntries_AreValidatedAtStartup(string entry, bool valid)
    {
        Assert.Equal(valid, GitCredentialOptions.IsValidEntry(entry));
    }
}
