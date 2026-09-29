using System.Globalization;
using System.Text.RegularExpressions;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>
/// Checks that evidence a language model cites exists at the analyzed commit. Everything is read
/// from git's object database — the agent can run commands that change the checkout, so a file in
/// the worktree proves nothing. No network is involved: a pull request or issue counts only when
/// its URL appears in the repository or in its history.
/// </summary>
public sealed partial class EvidenceVerifier(IExecutionRuntimeProvider runtime, ExecutionSessionHandle session, string baseCommit)
{
    private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);

    public async Task<(Verdict Verdict, EvidencePayload Normalized)> VerifyAsync(EvidencePayload evidence, CancellationToken cancellationToken) =>
        evidence.Kind switch
        {
            EvidenceKind.Code => await CodeAsync(evidence, cancellationToken),
            EvidenceKind.Commit => await CommitAsync(evidence, cancellationToken),
            EvidenceKind.PullRequest or EvidenceKind.Issue => (await LinkAsync(evidence.Url!, cancellationToken), evidence),
            _ => (Verdict.Accept, evidence),
        };

    private async Task<(Verdict, EvidencePayload)> CodeAsync(EvidencePayload e, CancellationToken cancellationToken)
    {
        var path = e.Path!;
        if (path.StartsWith('-') || path.StartsWith('/') || path.Contains('\0', StringComparison.Ordinal)
            || path.Replace('\\', '/').Split('/').Contains(".."))
        {
            return (NotFound($"'{path}' is not a path inside the repository.", "path outside the repository"), e);
        }

        var content = await FileAsync(path, cancellationToken);
        if (content is null)
        {
            return (NotFound($"'{path}' does not exist at the analyzed commit.", "no file at the analyzed commit"), e);
        }

        var searched = content;
        string? normalizedLines = null;
        if (!string.IsNullOrWhiteSpace(e.Lines))
        {
            var match = LineRange().Match(e.Lines.Trim());
            if (!match.Success)
            {
                return (NotFound($"Lines '{e.Lines}' is not a line number or a range like 12-20.", "unreadable line range"), e);
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            {
                return (NotFound($"Lines '{e.Lines}' is not a line number or a range like 12-20.", "unreadable line range"), e);
            }

            var end = start;
            if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end))
            {
                return (NotFound($"Lines '{e.Lines}' is not a line number or a range like 12-20.", "unreadable line range"), e);
            }

            var lines = content.Split('\n');
            var count = content.EndsWith('\n') ? lines.Length - 1 : lines.Length;
            if (start < 1 || end < start || end > count)
            {
                return (NotFound($"Lines {start}-{end} are outside '{path}' ({count} lines).", "line range outside the file"), e);
            }

            searched = string.Join('\n', lines[(start - 1)..end]);
            normalizedLines = start == end ? $"{start}" : $"{start}-{end}";
        }

        if (searched.Contains(e.Symbol!, StringComparison.Ordinal))
        {
            return (Verdict.Accept, e with { Lines = normalizedLines });
        }

        // Wrong lines around a right quote is the one miss the agent can fix without reading again.
        return normalizedLines is not null && content.Contains(e.Symbol!, StringComparison.Ordinal)
            ? (NotFound($"'{e.Symbol}' does not appear in lines {normalizedLines} of '{path}', but it does appear elsewhere in the file: fix Lines or leave them out.",
                "symbol outside the cited lines"), e)
            : (NotFound($"'{e.Symbol}' does not appear in '{path}'. Symbol must be text copied exactly from the file.", "symbol not in the file"), e);
    }

    private async Task<(Verdict, EvidencePayload)> CommitAsync(EvidencePayload e, CancellationToken cancellationToken)
    {
        if (!ShortSha().IsMatch(e.Sha!))
        {
            return (NotFound($"'{e.Sha}' is not a commit sha.", "not a commit sha"), e);
        }

        var full = (await GitAsync(["rev-parse", "--verify", "--quiet", $"{e.Sha}^{{commit}}"], cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(full))
        {
            return (NotFound($"Commit {e.Sha} is not in the repository.", "commit not in the repository"), e);
        }

        return await GitAsync(["merge-base", "--is-ancestor", full, baseCommit], cancellationToken) is null
            ? (NotFound($"Commit {e.Sha} is not in the analyzed commit's history.", "commit not in the analyzed history"), e)
            : (Verdict.Accept, e with { Sha = full });
    }

    private async Task<Verdict> LinkAsync(string url, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal))
        {
            return NotFound("A pull request or issue is cited by its https URL.", "url not https");
        }

        if (await GitAsync(["grep", "-F", "-q", "-e", url, baseCommit], cancellationToken) is not null)
        {
            return Verdict.Accept;
        }

        var log = await GitAsync(["log", "-F", $"--grep={url}", "--format=%H", "-n", "1", baseCommit], cancellationToken);
        return string.IsNullOrWhiteSpace(log)
            ? NotFound($"{url} appears neither in the repository nor in a commit message.", "url in neither the repository nor its history")
            : Verdict.Accept;
    }

    private async Task<string?> FileAsync(string path, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(path, out var content))
        {
            // Only a file counts: "git show" on a directory succeeds too, printing the entry names,
            // and a symbol matched against those would be evidence that no file contains.
            var type = await GitAsync(["cat-file", "-t", $"{baseCommit}:{path}"], cancellationToken);
            content = type?.Trim() == "blob" ? await GitAsync(["show", $"{baseCommit}:{path}"], cancellationToken) : null;
            _files[path] = content;
        }

        return content;
    }

    /// <summary>The command's output, or null when git said no — a failed check is an answer, not an error.</summary>
    private async Task<string?> GitAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await runtime.ExecuteAsync(session, new ExecutionCommand("git", args), cancellationToken);
        return result.Success ? result.Output ?? "" : null;
    }

    private static Verdict NotFound(string message, string cause) => Verdict.Reject(RejectionReason.EvidenceNotFound, message, cause);

    [GeneratedRegex(@"^L?(\d+)(?:-L?(\d+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LineRange();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSha();
}
