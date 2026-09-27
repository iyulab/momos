using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis;

/// <summary>
/// Extracts the structural layer of a project model from .NET project files — no language model
/// involved, so every claim it makes is a fact whose evidence is the file (and line) it was read
/// from. Only read-only git commands run in the session; nothing is written to the repository.
/// </summary>
public sealed class ProjectModelExtractor(IExecutionRuntimeProvider runtime) : IProjectModelExtractor
{
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
        foreach (var path in paths)
        {
            var content = await RunAsync(session, ["show", $"HEAD:{path}"], cancellationToken);
            if (TryParse(content) is { } document)
            {
                projects.Add((path, document));
            }

            // Otherwise one malformed project file shouldn't cost the whole model; it is simply
            // absent, and the model makes no claim about it.
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
            components.Add(new ComponentPayload(ModelIds.Component(path), name, kind, null, [claimKey]));

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

        return new ProjectModelPayload(baseCommit, components, relations, [], [], [], claims);
    }

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
