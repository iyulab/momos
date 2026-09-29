using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Momos.Host.Domain;

namespace Momos.Host.Reporting;

public sealed record ReportDocument(string Path, string Content);

/// <summary>
/// Renders a <see cref="ProjectModel"/> as the developer-facing deep report: a small tree of
/// markdown documents — a summary (<c>index.md</c>), one page per component, one page per claim —
/// linked with ordinary relative links, so it reads in any markdown viewer and a static-site
/// builder can turn it into navigable pages with backlinks. A pure view: every sentence traces back
/// to a claim, and a developer's verdict is always shown beside the claim it answers, never
/// instead of it.
/// </summary>
/// <remarks>
/// Free text (statements, corrections, names) is markdown whose raw HTML is shown literally: a
/// statement quoting <c>&lt;OutputType&gt;</c> reads as that instead of vanishing as an unknown
/// tag. Code spans in it are kept as written.
/// The output is byte-for-byte the same on every platform: lines end in <c>\n</c> and dates use
/// the invariant culture. Every relative link points at a document in the same tree; a reference
/// to something the model does not contain is rendered as plain text rather than a broken link.
/// </remarks>
public static partial class DeepReportRenderer
{
    public static IReadOnlyList<ReportDocument> Render(string projectName, ProjectModel model)
    {
        var tree = new Tree(model);

        // A model that brings an outline decides its chapters; one that doesn't (every
        // deterministic analysis, every version before outlines) gets the fixed layout.
        var documents = new List<ReportDocument>
        {
            new("index.md", model.Outline.Count > 0 ? OutlineIndex(projectName, model, tree) : Index(projectName, model, tree)),
            new(UnknownsPage, Unknowns(model, tree)),
        };
        documents.AddRange(AppendixPages(model, "", ""));
        return documents;
    }

    /// <summary>
    /// The chapter pages (at <c>{chapterPrefix}{section.Path}</c>) and the component and claim pages
    /// they link to (under <c>{evidencePrefix}components/</c> and <c>{evidencePrefix}claims/</c>) — the
    /// part of the report that does not depend on where its summary lives. Prefixes are empty or end
    /// in "/"; every link between these pages is relative to the folder of the page it is on. The
    /// pages' links back to a summary and to an unknowns page point at <paramref name="summaryPage"/>
    /// and <paramref name="unknownsPage"/>, paths in the same tree as the pages themselves.
    /// </summary>
    internal static IReadOnlyList<ReportDocument> AppendixPages(
        ProjectModel model, string chapterPrefix, string evidencePrefix, string summaryPage = "index.md", string unknownsPage = UnknownsPage)
    {
        var tree = new Tree(model);
        var claimsDir = $"{evidencePrefix}claims/";
        var componentsDir = $"{evidencePrefix}components/";
        Links LinksFrom(string dir) => new(
            Relative(dir, claimsDir), Relative(dir, componentsDir), Relative(dir, summaryPage), Relative(dir, unknownsPage));

        var documents = new List<ReportDocument>();
        var chapterLinks = LinksFrom(chapterPrefix);
        foreach (var section in model.Outline)
        {
            documents.Add(new($"{chapterPrefix}{section.Path}", SectionPage(section, model, tree, chapterLinks)));
        }

        var componentLinks = LinksFrom(componentsDir);
        foreach (var component in tree.Components)
        {
            documents.Add(new($"{componentsDir}{tree.ComponentFile(component.Id)}", ComponentPage(component, model, tree, componentLinks)));
        }

        var claimLinks = LinksFrom(claimsDir);
        foreach (var claim in tree.Claims)
        {
            documents.Add(new($"{claimsDir}{tree.ClaimFile(claim.Key)}", ClaimPage(claim, tree, claimLinks)));
        }

        return documents;
    }

    /// <summary>Where the pages a page links to live, relative to that page's folder.</summary>
    private readonly record struct Links(string Claims, string Components, string Summary, string Unknowns);

    /// <summary>The relative link from a page in <paramref name="fromDir"/> (empty or ending in "/")
    /// to <paramref name="to"/>, a file path or a folder ending in "/".</summary>
    private static string Relative(string fromDir, string to)
    {
        var from = fromDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var target = to.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = to.EndsWith('/');
        var limit = Math.Min(from.Length, folder ? target.Length : target.Length - 1);
        var common = 0;
        while (common < limit && from[common] == target[common])
        {
            common++;
        }

        var up = string.Concat(Enumerable.Repeat("../", from.Length - common));
        var down = string.Join('/', target[common..]);
        return up + down + (folder && down.Length > 0 ? "/" : "");
    }

    private static string Index(string projectName, ProjectModel model, Tree tree)
    {
        var md = new StringBuilder()
            .Line($"# {Inline(projectName)} — design report")
            .Line()
            .Line($"Project model version {model.ModelVersion} at commit {Code(model.BaseCommit)}, generated {Date(model.CreatedAt)}.")
            .Line("Every statement in this report is a graded claim with evidence, on a page of its own. A developer verdict never replaces the original claim — both are shown.")
            .Line("Origin says how a claim was produced: Deterministic claims are read mechanically from the repository; Synthesized claims are a language model's reading of the same evidence.")
            .Line()
            .Line($"What this report does not know: {UnknownsLink(model, tree)}.")
            .Line();
        Understanding(md, model, tree);
        Structure(md, model, tree);

        if (model.Patterns.Count > 0)
        {
            md.Line("## Patterns").Line();
            foreach (var p in model.Patterns)
            {
                var appliesTo = string.Join(", ", p.AppliesTo.Select(id => tree.ComponentLink(id, "components/")));
                md.Line($"- **{Inline(p.Name)}** — applies to {appliesTo} ({tree.ClaimLinks(p.Claims, "claims/")})");
            }

            md.Line();
        }

        if (model.Flows.Count > 0)
        {
            md.Line("## Flows").Line();
            foreach (var f in model.Flows)
            {
                md.Line($"- **{Inline(f.Name)}** — {f.Steps.Count} steps ({tree.ClaimLinks(f.Claims, "claims/")})");
            }

            md.Line();
        }

        if (model.Invariants.Count > 0)
        {
            md.Line("## Invariants").Line();
            foreach (var i in model.Invariants)
            {
                md.Line($"- {Inline(i.Statement)} — {Inline(i.Kind)} ({tree.ClaimLinks(i.Claims, "claims/")})");
            }

            md.Line();
        }

        if (model.Decisions.Count > 0)
        {
            md.Line("## Decisions").Line();
            foreach (var d in model.Decisions)
            {
                var rationale = d.Rationale == ModelDecision.Unrecorded ? "_unrecorded_" : Inline(d.Rationale);
                md.Line($"- **{Inline(d.Summary)}** — rationale: {rationale} ({tree.ClaimLinks(d.Claims, "claims/")})");
            }

            md.Line();
            if (model.Decisions.Any(d => d.Rationale == ModelDecision.Unrecorded))
            {
                md.Line($"Decisions without a recorded rationale are asked about in [what this report does not know]({UnknownsPage}).").Line();
            }
        }

        if (model.Intents.Count > 0)
        {
            md.Line("## Intents").Line();
            foreach (var i in model.Intents)
            {
                md.Line($"- {Inline(i.Statement)} — source: {i.Source} ({tree.ClaimLinks(i.Claims, "claims/")})");
            }

            md.Line();
        }

        ClaimsTable(md, tree);
        return md.ToString();
    }

    /// <summary>The structure diagram and component list — the way into every component page.</summary>
    private static void Structure(StringBuilder md, ProjectModel model, Tree tree)
    {
        md.Line("## Structure")
            .Line();

        if (tree.Components.Count == 0)
        {
            md.Line("No components were extracted from this commit.").Line();
        }
        else
        {
            Mermaid(md, tree.Components, model.Relations);
            md.Line()
              .Line("| Component | Kind |").Line("|---|---|");
            foreach (var c in tree.Components)
            {
                md.Line($"| {Cell(tree.ComponentLink(c.Id, "components/"))} | {Cell(Inline(c.Kind))} |");
            }

            md.Line();
        }
    }

    /// <summary>A graph of <paramref name="components"/> and the relations between them.</summary>
    private static void Mermaid(StringBuilder md, IReadOnlyList<ModelComponent> components, IEnumerable<ModelRelation> relations)
    {
        var nodes = components
            .Select((c, i) => (c.Id, Node: $"c{i}"))
            .ToDictionary(x => x.Id, x => x.Node, StringComparer.Ordinal);
        md.Line("```mermaid").Line("graph LR");
        foreach (var c in components)
        {
            md.Line($"    {nodes[c.Id]}[\"{MermaidLabel(c.Name)}\"]");
        }

        foreach (var r in relations.Where(r => nodes.ContainsKey(r.From) && nodes.ContainsKey(r.To)))
        {
            md.Line($"    {nodes[r.From]} -->|{MermaidLabel(r.Kind)}| {nodes[r.To]}");
        }

        md.Line("```");
    }

    /// <summary>Every claim with its grade and status — the way into every claim page.</summary>
    private static void ClaimsTable(StringBuilder md, Tree tree)
    {
        if (tree.Claims.Count > 0)
        {
            md.Line("## Claims").Line()
              .Line("| Claim | Tier | Confidence | Origin | Status | Statement |").Line("|---|---|---|---|---|---|");
            foreach (var c in tree.Claims)
            {
                md.Line($"| {Cell(tree.ClaimLink(c.Key, "claims/"))} | {c.Tier} | {c.Confidence} | {c.Origin} | {c.Status} | {Cell(Inline(c.Statement))} |");
            }

            md.Line();
        }
    }

    private static string ComponentPage(ModelComponent component, ProjectModel model, Tree tree, Links l)
    {
        var md = new StringBuilder()
            .Line($"# {Inline(component.Name)}")
            .Line()
            .Line($"Kind: {Inline(component.Kind)}. Back to the [summary]({l.Summary}).")
            .Line();
        if (!string.IsNullOrWhiteSpace(component.Responsibility))
        {
            // Element text is never shown on its own: the claims that back it sit right beside it.
            md.Line(Block(component.Responsibility)).Line()
              .Line($"_Backed by {tree.ClaimLinks(component.Claims, l.Claims)}._").Line();
        }

        var outgoing = model.Relations.Where(r => r.From == component.Id && tree.HasComponent(r.To)).ToList();
        var incoming = model.Relations.Where(r => r.To == component.Id && tree.HasComponent(r.From)).ToList();
        if (outgoing.Count + incoming.Count > 0)
        {
            md.Line("## Relations").Line();
            foreach (var r in outgoing)
            {
                md.Line($"- {Inline(r.Kind)} {tree.ComponentLink(r.To, "")} ({tree.ClaimLinks(r.Claims, l.Claims)})");
            }

            foreach (var r in incoming)
            {
                md.Line($"- {tree.ComponentLink(r.From, "")} {Inline(r.Kind)} this ({tree.ClaimLinks(r.Claims, l.Claims)})");
            }

            md.Line();
        }

        md.Line("## Claims behind this component").Line().Line(tree.ClaimLinks(component.Claims, l.Claims));
        return md.ToString();
    }

    private static string ClaimPage(ModelClaim claim, Tree tree, Links l)
    {
        var md = new StringBuilder()
            .Line($"# {Title(claim.Statement)}")
            .Line()
            .Line($"Claim {Code(claim.Key)}. Back to the [summary]({l.Summary}).")
            .Line()
            .Line("| Tier | Confidence | Origin | Status |").Line("|---|---|---|---|")
            .Line($"| {claim.Tier} | {claim.Confidence} | {claim.Origin} | {claim.Status} |")
            .Line()
            .Line("## Statement").Line().Line(Block(claim.Statement)).Line()
            .Line("## Evidence").Line();
        foreach (var e in claim.Evidence)
        {
            md.Line($"- {Evidence(e, tree)}");
        }

        if (claim.Correction is not null || claim.Status != ClaimStatus.Proposed)
        {
            md.Line().Line("## Developer verdict").Line()
              .Line($"{claim.Status}{(claim.CorrectedAt is { } at ? $" on {Date(at)}" : "")}.");
            if (claim.Correction is not null)
            {
                md.Line();
                foreach (var line in Block(claim.Correction).Split('\n'))
                {
                    md.Line(line.Length == 0 ? ">" : $"> {line}");
                }
            }
        }

        return md.ToString();
    }

    private static string Evidence(ClaimEvidence e, Tree tree) => e.Kind switch
    {
        EvidenceKind.Code => $"{Code(e.Path ?? "")}{(e.Lines is null ? "" : $":{Inline(e.Lines)}")}{(e.Symbol is null ? "" : $" — {Code(e.Symbol)}")}",
        EvidenceKind.Commit => $"commit {Code(e.Sha ?? "")}",
        EvidenceKind.PullRequest => $"pull request {Inline(e.Url ?? "")}",
        EvidenceKind.Issue => $"issue {Inline(e.Url ?? "")}",
        EvidenceKind.Finding => $"reproduced finding (inspection {Code(e.InspectionRequestId?.ToString() ?? "")})",
        // Claim pages live in one folder, so a claim links to another claim without a prefix.
        EvidenceKind.Claim => $"interprets {tree.ClaimLink(e.ClaimKey ?? "", "")}",
        _ => e.Kind.ToString(),
    };

    private static string Date(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const int TitleLength = 80;

    /// <summary>Free text that must stay on one line (headings, list items).</summary>
    private static string Inline(string text) => EscapeHtml(OneLine(text));

    /// <summary>A paragraph of free text, with its line endings normalized to <c>\n</c>.</summary>
    private static string Block(string text) => EscapeHtml(text.ReplaceLineEndings("\n").Trim('\n'));

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>A page title from free text: the text itself, or — past <see cref="TitleLength"/>
    /// characters — its words up to that length followed by an ellipsis.</summary>
    private static string Title(string text)
    {
        var line = OneLine(text).Trim();
        if (line.Length > TitleLength)
        {
            var cut = line.LastIndexOf(' ', TitleLength);
            line = $"{line[..(cut > 0 ? cut : TitleLength)].TrimEnd()}…";
        }

        return EscapeHtml(line);
    }

    /// <summary>Escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> outside code spans, so markdown
    /// formatting still works but raw HTML is shown as text, and the first bracket of <c>[[</c>, so
    /// repository text never reads as a wiki link to a page the report does not have. A code span is
    /// a run of backticks, its content, and a run of the same length.</summary>
    private static string EscapeHtml(string text)
    {
        var escaped = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match span in CodeSpan().Matches(text))
        {
            escaped.Append(EscapeHtmlChars(text[last..span.Index])).Append(span.Value);
            last = span.Index + span.Length;
        }

        return escaped.Append(EscapeHtmlChars(text[last..])).ToString();
    }

    private static string EscapeHtmlChars(string text) => BracketBeforeBracket().Replace(text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal), "&#91;");

    // Every bracket that another follows, so a run of three still leaves no pair.
    [GeneratedRegex(@"\[(?=\[)")]
    private static partial Regex BracketBeforeBracket();

    [GeneratedRegex(@"(?<!`)(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Singleline)]
    private static partial Regex CodeSpan();

    /// <summary>A table cell from markdown that is already rendered; only pipes are escaped.</summary>
    private static string Cell(string text) => OneLine(text).Replace("|", @"\|", StringComparison.Ordinal);

    /// <summary>Text for an emphasized subtitle: markdown emphasis markers in it are escaped so the
    /// text cannot close the emphasis and continue as ordinary (bold, asserting) prose.</summary>
    private static string Subtitle(string text) => Inline(text)
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal)
        .Replace("*", @"\*", StringComparison.Ordinal);

    private static string LinkText(string text) => Inline(text)
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("[", @"\[", StringComparison.Ordinal)
        .Replace("]", @"\]", StringComparison.Ordinal);

    /// <summary>An inline code span that survives backticks in its content.</summary>
    private static string Code(string text)
    {
        var inline = OneLine(text);
        return inline.Contains('`', StringComparison.Ordinal) ? $"`` {inline} ``" : $"`{inline}`";
    }

    // Mermaid's entity codes keep quotes, brackets and pipes from ending a label early; '#' goes
    // first so the entity codes introduced after it are not escaped again.
    private static string MermaidLabel(string text) => OneLine(text)
        .Replace("#", "#35;", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("[", "#91;", StringComparison.Ordinal)
        .Replace("]", "#93;", StringComparison.Ordinal)
        .Replace("|", "#124;", StringComparison.Ordinal)
        .Replace("<", "#lt;", StringComparison.Ordinal)
        .Replace(">", "#gt;", StringComparison.Ordinal);

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,79}\z")]
    private static partial Regex SafeFileStem();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex UnsafeRun();

    /// <summary>The file name for a component id or claim key. Ids are any string the API
    /// accepted; one that is already a safe, lower-case file stem is used as is, anything else
    /// becomes a readable slug plus a short hash of the original, so distinct ids never share a
    /// file — not even on a case-insensitive file system.</summary>
    private static string FileName(string id)
    {
        if (SafeFileStem().IsMatch(id))
        {
            return $"{id}.md";
        }

        var slug = UnsafeRun().Replace(id.ToLowerInvariant(), "-").Trim('-', '.');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-', '.');
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 4));
        return slug.Length == 0 ? $"{hash}.md" : $"{slug}-{hash}.md";
    }

    /// <summary>What the tree contains, and how to link to it. Duplicates (which a submission
    /// should never carry) keep their first occurrence so a page is never written twice.</summary>
    private sealed class Tree
    {
        private readonly Dictionary<string, ModelComponent> _components = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ModelClaim> _claims = new(StringComparer.Ordinal);

        public Tree(ProjectModel model)
        {
            foreach (var c in model.Components)
            {
                if (_components.TryAdd(c.Id, c))
                {
                    Components.Add(c);
                }
            }

            foreach (var c in model.Claims.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (_claims.TryAdd(c.Key, c))
                {
                    Claims.Add(c);
                }
            }

            foreach (var p in model.Patterns) { Patterns.TryAdd(p.Id, p); }
            foreach (var d in model.Decisions) { Decisions.TryAdd(d.Id, d); }
            foreach (var i in model.Intents) { Intents.TryAdd(i.Id, i); }
            foreach (var f in model.Flows) { Flows.TryAdd(f.Id, f); }
            foreach (var i in model.Invariants) { Invariants.TryAdd(i.Id, i); }
        }

        public Dictionary<string, ModelPattern> Patterns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelDecision> Decisions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelIntent> Intents { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelFlow> Flows { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelInvariant> Invariants { get; } = new(StringComparer.Ordinal);

        public ModelClaim? Claim(string key) => _claims.GetValueOrDefault(key);

        public ModelComponent? Component(string id) => _components.GetValueOrDefault(id);

        public List<ModelComponent> Components { get; } = [];

        public List<ModelClaim> Claims { get; } = [];

        public bool HasComponent(string id) => _components.ContainsKey(id);

        public string ComponentFile(string id) => FileName(id);

        public string ClaimFile(string key) => FileName(key);

        public string ComponentLink(string id, string prefix) => _components.TryGetValue(id, out var c)
            ? $"[{LinkText(c.Name)}]({prefix}{FileName(id)})"
            : Code(id);

        public string ClaimLink(string key, string prefix) => _claims.ContainsKey(key)
            ? $"[{Code(key)}]({prefix}{FileName(key)})"
            : Code(key);

        public string ClaimLinks(IEnumerable<string> keys, string prefix) =>
            string.Join(", ", keys.Select(k => ClaimLink(k, prefix)));
    }
}

internal static class MarkdownLines
{
    /// <summary>Appends a line ending in <c>\n</c> on every platform, unlike
    /// <see cref="StringBuilder.AppendLine()"/>.</summary>
    public static StringBuilder Line(this StringBuilder md, string text = "") => md.Append(text).Append('\n');
}
