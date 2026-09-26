using System.Diagnostics;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public sealed class GitCloneCommandTests
{
    private static readonly GitCredential Credential = new("github.com", "x-access-token", "s3cret-token");

    [Fact]
    public void Create_WithoutACredential_IsAPlainCloneWithNoEnvironment()
    {
        var command = GitCloneCommand.Create("https://github.com/acme/repo.git", null);

        Assert.Equal("git", command.Name);
        Assert.Equal(["clone", "--", "https://github.com/acme/repo.git", "."], command.Args);
        Assert.Null(command.Environment);
    }

    [Fact]
    public void Create_WithACredential_PassesItOnlyThroughThisCommandsEnvironment()
    {
        var command = GitCloneCommand.Create("https://github.com/acme/repo.git", Credential);

        // The helpers the image configures are cleared before ours is added; the URL itself
        // carries no credential, so the checkout's remote is exactly what was registered.
        Assert.Equal(["-c", "credential.helper=", "-c"], command.Args.Take(3));
        Assert.StartsWith("credential.helper=!", command.Args[3]);
        Assert.Equal(["clone", "--", "https://github.com/acme/repo.git", "."], command.Args.Skip(4));
        Assert.DoesNotContain(command.Args, arg => arg.Contains("s3cret-token"));
        Assert.Equal("github.com", command.Environment![GitCloneCommand.HostVariable]);
        Assert.Equal("x-access-token", command.Environment[GitCloneCommand.UsernameVariable]);
        Assert.Equal("s3cret-token", command.Environment[GitCloneCommand.PasswordVariable]);
    }

    [Fact]
    public void ToString_NamesTheEnvironmentVariables_ButNeverTheirValues()
    {
        var command = GitCloneCommand.Create("https://github.com/acme/repo.git", Credential);

        var text = command.ToString();

        Assert.Contains(GitCloneCommand.PasswordVariable, text);
        Assert.DoesNotContain("s3cret-token", text);
        Assert.DoesNotContain("s3cret-token", Credential.ToString());
    }

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/acme/private.git/' not found")]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/private.git/'")]
    [InlineData("fatal: unable to access '...': The requested URL returned error: 403")]
    public void DescribeAccessFailure_OnAnAnonymousClone_NamesTheSettingToChange(string gitError)
    {
        var hint = GitCloneCommand.DescribeAccessFailure(gitError, null);

        Assert.NotNull(hint);
        Assert.Contains("Momos:Worker:Checkout:CredentialedRepositories", hint);
    }

    [Fact]
    public void DescribeAccessFailure_WithACredential_SaysTheCredentialWasRefused_WithoutItsValue()
    {
        var hint = GitCloneCommand.DescribeAccessFailure("remote: Repository not found.", Credential);

        Assert.NotNull(hint);
        Assert.Contains("github.com", hint);
        Assert.DoesNotContain("s3cret-token", hint);
        Assert.DoesNotContain("CredentialedRepositories", hint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fatal: unable to access '...': Could not resolve host: github.com")]
    [InlineData("fatal: destination path '.' already exists and is not an empty directory.")]
    public void DescribeAccessFailure_ForAnythingElse_AddsNothing(string? gitError)
    {
        Assert.Null(GitCloneCommand.DescribeAccessFailure(gitError, null));
    }

    // The helper is a shell function git runs itself, so its behaviour is only shown by a real
    // git asking it — which is what these do, through "git credential fill".

    [Fact]
    public async Task Helper_AnswersForTheCredentialsHostOverHttps()
    {
        var (exitCode, output) = await FillAsync("protocol=https\nhost=github.com\n\n");

        Assert.Equal(0, exitCode);
        Assert.Contains("username=x-access-token", output);
        Assert.Contains("password=s3cret-token", output);
    }

    [Theory]
    [InlineData("protocol=https\nhost=elsewhere.example\n\n")]
    [InlineData("protocol=http\nhost=github.com\n\n")]
    public async Task Helper_GivesNothingToAnotherHostOrOverPlainHttp(string request)
    {
        var (exitCode, output) = await FillAsync(request);

        // With the helper silent and prompting disabled, git has no credential to report.
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("s3cret-token", output);
    }

    [Fact]
    public async Task Helper_ReturnsAPasswordWithBackslashesUnaltered()
    {
        var credential = new GitCredential("github.com", "worker", @"pa\nss\\w0rd\c");

        var (exitCode, output) = await FillAsync("protocol=https\nhost=github.com\n\n", credential);

        Assert.Equal(0, exitCode);
        Assert.Contains(@"password=pa\nss\\w0rd\c", output);
    }

    private static async Task<(int ExitCode, string Output)> FillAsync(string request, GitCredential? credential = null)
    {
        var clone = GitCloneCommand.Create("https://github.com/acme/repo.git", credential ?? Credential);
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in clone.Args.Take(4))
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add("credential");
        start.ArgumentList.Add("fill");
        foreach (var (name, value) in clone.Environment!)
        {
            start.Environment[name] = value;
        }

        // Hermetic: no machine or user configuration, and no prompt to fall back on.
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetTempPath(), $"momos-no-config-{Guid.NewGuid():N}");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS"] = "";

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(request);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
