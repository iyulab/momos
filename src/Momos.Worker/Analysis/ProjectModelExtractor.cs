using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis;

/// <summary>
/// Extracts the structural layer of a project model from .NET project files, and each component's
/// change history from the repository's commits — no language model involved, so every claim it
/// makes is either a fact whose evidence is the file (and line) it was read from or a history claim
/// whose evidence is the commits it counted. Only read-only git commands run in the session;
/// nothing is written to the repository.
/// </summary>
public sealed class ProjectModelExtractor(IExecutionRuntimeProvider runtime) : IProjectModelExtractor
{
    /// <summary>How many commits a history claim cites as evidence — the most recent ones. The
    /// claim's count covers them all; the citations are where a reader starts.</summary>
    private const int CitedCommits = 5;

    /// <summary>One commit per NUL-terminated record: full sha, committer date (as stored in the
    /// commit, so the same commit always reads the same) and subject, split by the unit separator.</summary>
    private const string CommitFormat = "--format=%H%x1f%cs%x1f%s";

    /// <summary>Project files come from the repository under analysis, so they are untrusted input:
    /// no DTD (no entity expansion) and no resolution of anything outside the file.</summary>
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    public async Task<ProjectModelPayload> ExtractAsync(ExecutionSessionHandle session, CancellationToken cancellationToken)
    {
        var baseCommit = (await RunAsync(session, ["rev-parse", "HEAD"], cancellationToken)).Trim();
        if (baseCommit.Length == 0)
        {
            // A checkout always has a HEAD; an empty answer means the command's output was lost,
            // and a model pinned to no commit would be indistinguishable from a real one.
            throw new InvalidOperationException("git rev-parse HEAD succeeded but returned no commit.");
        }

        // -z: without it git C-quotes any path with non-ASCII characters, and the quoted form is
        // not a path "git show" can read back.
        var listing = await RunAsync(session, ["ls-files", "-z", "--", "*.csproj"], cancellationToken);
        var paths = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var projects = new List<(string Path, XDocument Document)>();
        var unread = new List<CoverageGapPayload>();
        foreach (var path in paths)
        {
            var content = await RunAsync(session, ["show", $"HEAD:{path}"], cancellationToken);
            if (TryParse(content) is { } document)
            {
                projects.Add((path, document));
            }
            else
            {
                // One malformed project file shouldn't cost the whole model; the model makes no
                // claim about it, and says so.
                unread.Add(new CoverageGapPayload("project-manifests", $"{path} is not a well-formed MSBuild project file."));
            }
        }

        var lookup = new ProjectLookup(projects.Select(p => p.Path));
        var components = new List<ComponentPayload>();
        var relations = new List<RelationPayload>();
        var claims = new List<ClaimPayload>();

        foreach (var (path, document) in projects)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var kind = Classify(document);
            var sdk = document.Root?.Attribute("Sdk")?.Value;
            var claimKey = ModelIds.Claim($"component|{path}");
            claims.Add(new ClaimPayload(claimKey, ClaimTier.Fact, $"{name} is a .NET project ({kind}) defined in {path}.",
                [new EvidencePayload(EvidenceKind.Code, Path: path, Symbol: sdk is null ? "Project" : $"Project Sdk=\"{sdk}\"")],
                ClaimConfidence.High, ClaimOrigin.Deterministic));
            var componentClaims = new List<string> { claimKey };
            foreach (var history in await HistoryAsync(session, baseCommit, path, name, cancellationToken))
            {
                claims.Add(history);
                componentClaims.Add(history.Key);
            }

            components.Add(new ComponentPayload(ModelIds.Component(path), name, kind, null, componentClaims));

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var includes = (reference.Attribute("Include")?.Value ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var include in includes)
                {
                    // Unresolvable (an MSBuild property, a path leaving the repository, a project
                    // that isn't there) → no relation: the model only states what it can point at.
                    var target = lookup.Find(Resolve(path, include));
                    if (target is null || target == path || !referenced.Add(target))
                    {
                        continue;
                    }

                    var lineInfo = (IXmlLineInfo)reference;
                    var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber.ToString(CultureInfo.InvariantCulture) : null;
                    var referenceKey = ModelIds.Claim($"reference|{path}|{target}");
                    claims.Add(new ClaimPayload(referenceKey, ClaimTier.Fact,
                        $"{name} references {Path.GetFileNameWithoutExtension(target)} (project reference).",
                        [new EvidencePayload(EvidenceKind.Code, Path: path, Symbol: "ProjectReference", Lines: line)], ClaimConfidence.High, ClaimOrigin.Deterministic));
                    relations.Add(new RelationPayload(ModelIds.Component(path), ModelIds.Component(target), "references", [referenceKey]));
                }
            }
        }

        var coverage = new CoveragePayload(
            [
                new CoverageAreaPayload("project-manifests", $"{projects.Count} of {paths.Count} project files (*.csproj) tracked at the analyzed commit."),
                new CoverageAreaPayload("git-history", "Commits under each project's directory, and commits whose message names the project."),
            ],
            [
                .. unread,
                new CoverageGapPayload("source-files", "Only project files and git history are read; source code is not analyzed."),
                new CoverageGapPayload("non-dotnet-projects", "Only .NET project files (*.csproj) are recognized; other build systems are not read."),
            ],
            [],
            Generator: null);

        return new ProjectModelPayload(baseCommit, components, relations, [], [], [], [], [], [], coverage, claims);
    }

    /// <summary>
    /// History claims for the component whose project file is at <paramref name="projectPath"/>:
    /// how often the files under its directory changed and when last, and which commit messages
    /// name it. Both are counted back from <paramref name="baseCommit"/>; neither says why a change
    /// was made — the subject quoted is the commit's own words.
    /// </summary>
    private async Task<IReadOnlyList<ClaimPayload>> HistoryAsync(
        ExecutionSessionHandle session, string baseCommit, string projectPath, string name, CancellationToken cancellationToken)
    {
        var slash = projectPath.LastIndexOf('/');
        var directory = slash < 0 ? "" : projectPath[..slash];

        // top: relative to the repository root whatever the working directory; literal: a
        // directory named with '*' or '[' is a path, not a glob.
        var pathspec = directory.Length == 0 ? ":(top)" : $":(top,literal){directory}";
        var claims = new List<ClaimPayload>();

        var (changes, recent) = await CommitsAsync(session, baseCommit, [], [pathspec], cancellationToken);
        if (recent.Count > 0)
        {
            var where = directory.Length == 0 ? "the repository root" : $"{directory}/";
            claims.Add(HistoryClaim(ModelIds.Claim($"history|{projectPath}"),
                $"{name}: {Commits(changes)} changed files under {where}; the latest, {Latest(recent[0])}.", recent));
        }

        var (mentions, named) = await CommitsAsync(session, baseCommit, ["-E", $"--grep={MentionPattern(name)}"], [], cancellationToken);
        if (named.Count > 0)
        {
            claims.Add(HistoryClaim(ModelIds.Claim($"mentions|{projectPath}"),
                $"{Commits(mentions)} {(mentions == 1 ? "names" : "name")} {name} in the message; the latest, {Latest(named[0])}.", named));
        }

        return claims;

        static string Commits(int count) => count == 1 ? "1 commit" : $"{count.ToString(CultureInfo.InvariantCulture)} commits";

        static string Latest(CommitSummary commit) => $"{commit.Sha[..Math.Min(10, commit.Sha.Length)]} on {commit.Date}, is \"{commit.Subject}\"";

        static ClaimPayload HistoryClaim(string key, string statement, IReadOnlyList<CommitSummary> cited) =>
            new(key, ClaimTier.History, statement,
                [.. cited.Select(c => new EvidencePayload(EvidenceKind.Commit, Sha: c.Sha))],
                ClaimConfidence.High, ClaimOrigin.Deterministic);
    }

    /// <summary>Counts the commits reachable from <paramref name="baseCommit"/> that the given
    /// revision options and pathspecs select, and reads the most recent of them.</summary>
    private async Task<(int Count, IReadOnlyList<CommitSummary> Recent)> CommitsAsync(
        ExecutionSessionHandle session, string baseCommit, IReadOnlyList<string> options, IReadOnlyList<string> pathspecs, CancellationToken cancellationToken)
    {
        var counted = (await RunAsync(session, ["rev-list", "--count", .. options, baseCommit, "--", .. pathspecs], cancellationToken)).Trim();
        if (!int.TryParse(counted, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            throw new InvalidOperationException($"git rev-list --count returned '{counted}', not a number.");
        }

        var log = await RunAsync(session,
            ["log", "-z", "--no-show-signature", $"--max-count={CitedCommits}", CommitFormat, .. options, baseCommit, "--", .. pathspecs],
            cancellationToken);
        var recent = log.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim('\n').Split('\u001f'))
            .Where(fields => fields.Length == 3 && fields[0].Length > 0)
            .Select(fields => new CommitSummary(fields[0], fields[1], fields[2]))
            .ToList();
        return (count, recent);
    }

    /// <summary>An extended regular expression (POSIX, as git's <c>--grep</c> with <c>-E</c> reads
    /// it) that finds <paramref name="name"/> as a whole name in a commit message: not inside a
    /// longer word, and not as the prefix of a dotted name (<c>App</c> is not named by
    /// <c>App.Tests</c>), while a sentence-ending period still counts. Case-sensitive — a message has
    /// to use the name.</summary>
    private static string MentionPattern(string name)
    {
        var escaped = new StringBuilder(name.Length * 2);
        foreach (var ch in name)
        {
            if (@".[]()*+?{}|^$\".Contains(ch, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(ch);
        }

        return $"(^|[^[:alnum:]_.]){escaped}([^[:alnum:]_.]|[.]([^[:alnum:]_]|$)|$)";
    }

    private sealed record CommitSummary(string Sha, string Date, string Subject);

    private static XDocument? TryParse(string content)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), ReaderSettings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string Classify(XDocument document)
    {
        var sdk = document.Root?.Attribute("Sdk")?.Value ?? "";
        string? Property(string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var isTest = string.Equals(Property("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || document.Descendants().Any(e => e.Name.LocalName == "PackageReference"
                && string.Equals(e.Attribute("Include")?.Value.Trim(), "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        var outputType = Property("OutputType");

        return isTest ? "test"
            : sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ? "web-service"
            : sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase) ? "worker-service"
            : string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase) ? "executable"
            : "library";
    }

    /// <summary>Resolves a ProjectReference (MSBuild accepts either slash) against the referencing
    /// project's directory, as a repository-relative forward-slash path — or null when it climbs
    /// out of the repository.</summary>
    private static string? Resolve(string fromPath, string include)
    {
        var segments = fromPath.Split('/').SkipLast(1).ToList();
        foreach (var part in include.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else if (part != ".")
            {
                segments.Add(part);
            }
        }

        return string.Join('/', segments);
    }

    private async Task<string> RunAsync(ExecutionSessionHandle session, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await runtime.ExecuteAsync(session, new ExecutionCommand("git", args), cancellationToken);
        return result.Success
            ? result.Output ?? ""
            : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
    }

    /// <summary>Maps a resolved reference to a project path in the repository. An exact match wins;
    /// otherwise a case-insensitive one, as long as it is unambiguous — MSBuild resolves references
    /// case-insensitively on the file systems many repositories are authored on.</summary>
    private sealed class ProjectLookup
    {
        private readonly HashSet<string> _exact;
        private readonly Dictionary<string, string> _folded;

        public ProjectLookup(IEnumerable<string> paths)
        {
            _exact = paths.ToHashSet(StringComparer.Ordinal);
            _folded = _exact.GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        }

        public string? Find(string? path) =>
            path is null ? null
            : _exact.Contains(path) ? path
            : _folded.GetValueOrDefault(path);
    }
}
