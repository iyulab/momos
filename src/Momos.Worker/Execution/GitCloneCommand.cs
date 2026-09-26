namespace Momos.Worker.Execution;

/// <summary>
/// The command that checks a repository out into a session's workspace.
/// </summary>
/// <remarks>
/// With a credential, git inside the sandbox gets it from a credential helper that reads two
/// environment variables set on this one command only. Nothing is written to the checkout's
/// configuration (<c>-c</c> lasts for this invocation), the remote URL stays as given, and
/// the variables are gone when the clone exits — before any of the repository's own code
/// runs in the session. The helper answers only for the credential's host over HTTPS, so a
/// redirect to another host is never handed the credential. Any helper the sandbox image
/// configures is cleared first so only this one answers.
/// </remarks>
public static class GitCloneCommand
{
    internal const string HostVariable = "MOMOS_GIT_HOST";
    internal const string UsernameVariable = "MOMOS_GIT_USERNAME";
    internal const string PasswordVariable = "MOMOS_GIT_PASSWORD";

    private const string Helper =
        "!f() { test \"$1\" = get || return 0; p=; h=; "
        + "while IFS= read -r l; do case \"$l\" in protocol=*) p=\"${l#protocol=}\";; host=*) h=\"${l#host=}\";; esac; done; "
        + "test \"$p\" = https && test \"$h\" = \"$" + HostVariable + "\" || return 0; "
        + "echo \"username=$" + UsernameVariable + "\"; echo \"password=$" + PasswordVariable + "\"; }; f";

    public static ExecutionCommand Create(string repositoryUrl, GitCredential? credential)
    {
        // "--" pins the following token as a positional argument so a RepositoryUrl value
        // that happens to start with "-" (e.g. "--upload-pack=...") cannot be smuggled in as
        // a git flag.
        if (credential is null)
        {
            return new ExecutionCommand("git", ["clone", "--", repositoryUrl, "."]);
        }

        return new ExecutionCommand(
            "git",
            ["-c", "credential.helper=", "-c", $"credential.helper={Helper}", "clone", "--", repositoryUrl, "."],
            Environment: new Dictionary<string, string>
            {
                [HostVariable] = credential.Host,
                [UsernameVariable] = credential.Username,
                [PasswordVariable] = credential.Password,
            });
    }
}
