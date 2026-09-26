using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Momos.Worker.Execution;

/// <summary>
/// Asks the Worker machine's git (<c>git credential fill</c>) for a repository host's
/// credential — whatever helper that account has configured answers it. Never interactive:
/// with nothing stored the lookup fails fast instead of waiting on a prompt nobody will see.
/// It only reads; it never approves or rejects a credential, so the helper's store is left
/// exactly as it was. Only repositories the operator listed in
/// <see cref="GitCredentialOptions.CredentialedRepositories"/> are asked about at all.
/// </summary>
public sealed class HostGitCredentialSource(
    IOptions<GitCredentialOptions> options,
    ILogger<HostGitCredentialSource> logger) : IGitCredentialSource
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Extra variables for the git process — lets a test point git at its own
    /// configuration without touching this process's environment.</summary>
    internal IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; } = new Dictionary<string, string>();

    public async Task<GitCredential?> GetAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0)
        {
            // Only HTTPS: a credential is never sent over plain HTTP. Anything else (ssh, a
            // local path) is not a URL git would ask a credential helper about, and one that
            // already carries its own user has nothing for the host to add.
            return null;
        }

        if (!options.Value.Allows(uri))
        {
            return null;
        }

        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("credential");
        start.ArgumentList.Add("fill");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS"] = "";
        foreach (var (name, value) in EnvironmentOverrides)
        {
            start.Environment[name] = value;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("git could not be started");

            await process.StandardInput.WriteAsync(
                $"protocol={uri.Scheme}\nhost={uri.Authority}\npath={uri.AbsolutePath.TrimStart('/')}\n\n".AsMemory(), timeout.Token);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                // The usual case for a public repository on a machine with no stored
                // credential — the clone proceeds anonymously. stderr is not logged: some
                // helpers echo the request back into it.
                logger.LogDebug("No git credential for {Host} (git credential fill exited {ExitCode})", uri.Authority, process.ExitCode);
                return null;
            }

            string? username = null, password = null;
            foreach (var line in (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var value = line[(separator + 1)..];
                switch (line[..separator])
                {
                    case "username":
                        username = value;
                        break;
                    case "password":
                        password = value;
                        break;
                }
            }

            _ = await error;
            return string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
                ? null
                : new GitCredential(uri.Authority, username, password);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("git credential fill for {Host} did not answer within {Timeout}; cloning without a credential", uri.Authority, LookupTimeout);
            return null;
        }
    }
}
