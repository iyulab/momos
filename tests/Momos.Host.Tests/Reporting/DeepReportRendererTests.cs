using System.Text.RegularExpressions;
using Momos.Host.Domain;
using Momos.Host.Reporting;

namespace Momos.Host.Tests.Reporting;

public sealed partial class DeepReportRendererTests
{
    private static ProjectModel Model(Action<ProjectModel>? configure = null)
    {
        var model = new ProjectModel { ProjectId = Guid.NewGuid(), ModelVersion = 2, BaseCommit = "abc123", AnalysisRequestId = Guid.NewGuid() };
        model.Components.Add(new ModelComponent("cmp.app", "App", "executable", null, ["clm.app"]));
        model.Components.Add(new ModelComponent("cmp.lib", "Lib", "library", null, ["clm.lib"]));
        model.Relations.Add(new ModelRelation("cmp.app", "cmp.lib", "references", ["clm.ref"]));
        model.Decisions.Add(new ModelDecision("dec.layering", "App depends on Lib, never the reverse", [], ModelDecision.Unrecorded, ["clm.ref"]));
        foreach (var (key, statement) in new[] { ("clm.app", "App is a .NET project"), ("clm.lib", "Lib is a .NET project"), ("clm.ref", "App references Lib") })
        {
            model.Claims.Add(Fact(model, key, statement));
        }

        configure?.Invoke(model);
        return model;
    }

    private static ModelClaim Fact(ProjectModel model, string key, string statement) => new()
    {
        ProjectModelId = model.Id,
        Key = key,
        Tier = ClaimTier.Fact,
        Statement = statement,
        Evidence = [new ClaimEvidence(EvidenceKind.Code, Path: "src/App/App.csproj", Lines: "6")],
        Confidence = ClaimConfidence.High,
        Origin = ClaimOrigin.Deterministic,
    };

    /// <summary>Every element kind and every link-producing branch: patterns, intents, and an
    /// assessment whose evidence links to the claim it interprets.</summary>
    private static ProjectModel RichModel(Action<ProjectModel>? configure = null) => Model(m =>
    {
        m.Patterns.Add(new ModelPattern("pat.layers", "Layered", ["cmp.app", "cmp.lib"], ["clm.ref"]));
        m.Intents.Add(new ModelIntent("int.split", "Keep the library free of UI concerns", IntentSource.Inferred, ["clm.split"]));
        m.Claims.Add(new ModelClaim
        {
            ProjectModelId = m.Id,
            Key = "clm.split",
            Tier = ClaimTier.Assessment,
            Statement = "The library looks deliberately UI-free",
            Evidence = [new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.ref")],
            Confidence = ClaimConfidence.Low,
            Origin = ClaimOrigin.Deterministic,
        });
        configure?.Invoke(m);
    });

    private static void ReplaceClaim(ProjectModel model, ModelClaim claim)
    {
        model.Claims.Remove(model.Claims.Single(c => c.Key == claim.Key));
        model.Claims.Add(claim);
    }

    private static string Doc(IReadOnlyList<ReportDocument> tree, string path) => Assert.Single(tree, d => d.Path == path).Content;

    [GeneratedRegex(@"\]\(([^)#\s]+\.md)(#[^)]*)?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^(index|components/[a-z0-9][a-z0-9._-]*|claims/[a-z0-9][a-z0-9._-]*)\.md$")]
    private static partial Regex SafePath();

    private static void AssertEveryRelativeLinkResolves(IReadOnlyList<ReportDocument> tree)
    {
        var paths = tree.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var links = 0;
        foreach (var doc in tree)
        {
            var dir = doc.Path.Contains('/') ? doc.Path[..doc.Path.LastIndexOf('/')] : "";
            foreach (Match link in MarkdownLink().Matches(doc.Content))
            {
                var href = link.Groups[1].Value;
                var target = href.StartsWith("../", StringComparison.Ordinal)
                    ? href[3..]
                    : (dir.Length == 0 ? "" : dir + "/") + href;
                Assert.True(paths.Contains(target), $"{doc.Path} links to missing {target}");
                links++;
            }
        }

        Assert.True(links > 0, "The tree has no links at all, so the check proved nothing.");
    }

    [Fact]
    public void Render_ProducesAnIndexOnePagePerComponentAndOnePagePerClaim()
    {
        var tree = DeepReportRenderer.Render("acme", Model());

        string[] expected = ["claims/clm.app.md", "claims/clm.lib.md", "claims/clm.ref.md", "components/cmp.app.md", "components/cmp.lib.md", "index.md"];
        Assert.Equal(expected, tree.Select(d => d.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Render_UsesLfLineEndingsOnEveryPlatform()
    {
        var tree = DeepReportRenderer.Render("acme", RichModel(m =>
        {
            var c = m.Claims.Single(c => c.Key == "clm.ref");
            c.Status = ClaimStatus.Corrected;
            c.Correction = "Lib is loaded\r\nas a plugin";
        }));

        Assert.All(tree, d => Assert.DoesNotContain('\r', d.Content));
        Assert.All(tree, d => Assert.EndsWith("\n", d.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void Index_StatesTheVersionAndBaseCommit()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model()), "index.md");

        Assert.Contains("# acme", index);
        Assert.Contains("model version 2", index);
        Assert.Contains("`abc123`", index);
    }

    [Fact]
    public void Index_DrawsRelationsAsAMermaidGraph()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model()), "index.md");

        Assert.Contains("```mermaid", index);
        Assert.Contains("graph LR", index);
        Assert.Contains("c0 -->|references| c1", index);
    }

    [Fact]
    public void Index_ListsDecisionsWhoseRationaleWasNeverRecorded()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model()), "index.md");

        Assert.Contains("## Decisions without a recorded rationale", index);
        Assert.Contains("App depends on Lib, never the reverse", index);
    }

    [Fact]
    public void Index_ListsEveryClaimWithItsCurrentStatus()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model(m =>
            m.Claims.Single(c => c.Key == "clm.lib").Status = ClaimStatus.Disputed)), "index.md");

        Assert.Contains("## Claims", index);
        Assert.Contains("| [`clm.lib`](claims/clm.lib.md) | Fact | High | Disputed | Lib is a .NET project |", index);
        Assert.Contains("| [`clm.app`](claims/clm.app.md) | Fact | High | Proposed | App is a .NET project |", index);
    }

    [Fact]
    public void AComponentPage_ShowsItsOutgoingRelationAndLinksTheClaimsBehindIt()
    {
        var app = Doc(DeepReportRenderer.Render("acme", Model()), "components/cmp.app.md");

        Assert.Contains("# App", app);
        Assert.Contains("[Lib](cmp.lib.md)", app);
        Assert.Contains("(../claims/clm.ref.md)", app);
        Assert.Contains("(../claims/clm.app.md)", app);
    }

    [Fact]
    public void AComponentPage_ShowsItsIncomingRelation()
    {
        var lib = Doc(DeepReportRenderer.Render("acme", Model()), "components/cmp.lib.md");

        Assert.Contains("[App](cmp.app.md) references this", lib);
    }

    [Fact]
    public void AClaimPage_ShowsTheCorrectionNextToTheOriginalStatement()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
        {
            var c = m.Claims.Single(c => c.Key == "clm.ref");
            c.Status = ClaimStatus.Corrected;
            c.Correction = "Lib is loaded as a plugin";
            c.CorrectedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        })), "claims/clm.ref.md");

        Assert.Contains("App references Lib", claim);
        Assert.Contains("> Lib is loaded as a plugin", claim);
        Assert.Contains("Corrected on 2026-03-04", claim);
        Assert.Contains("`src/App/App.csproj`:6", claim);
    }

    [Fact]
    public void AClaimPage_QuotesEveryLineOfAMultiLineCorrection()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
        {
            var c = m.Claims.Single(c => c.Key == "clm.ref");
            c.Status = ClaimStatus.Corrected;
            c.Correction = "Lib is loaded\nas a plugin";
        })), "claims/clm.ref.md");

        Assert.Contains("> Lib is loaded\n> as a plugin\n", claim);
    }

    [Fact]
    public void AnUnreviewedClaimPage_HasNoVerdictSection()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model()), "claims/clm.app.md");

        Assert.DoesNotContain("## Developer verdict", claim);
    }

    [Fact]
    public void EveryRelativeLinkInTheTree_PointsAtADocumentInTheTree()
    {
        var tree = DeepReportRenderer.Render("acme", RichModel());

        Assert.Contains("[`clm.ref`](clm.ref.md)", Doc(tree, "claims/clm.split.md"));
        Assert.Contains("[`clm.split`](claims/clm.split.md)", Doc(tree, "index.md"));
        AssertEveryRelativeLinkResolves(tree);
    }

    [Fact]
    public void IdsThatAreNotSafeFileNames_StillYieldSafeDistinctPathsAndWorkingLinks()
    {
        // The API accepts any string as a component id or claim key; the tree must stay a valid
        // set of relative files whatever arrives.
        var tree = DeepReportRenderer.Render("acme", Model(m =>
        {
            m.Components.Add(new ModelComponent("../Evil Id (1)", "Evil", "library", null, ["clm/Odd Key"]));
            m.Components.Add(new ModelComponent("CMP.APP", "Shouty", "library", null, ["CLM.APP"]));
            m.Relations.Add(new ModelRelation("../Evil Id (1)", "cmp.app", "references", ["clm/Odd Key"]));
            m.Claims.Add(Fact(m, "clm/Odd Key", "An oddly keyed claim"));
            m.Claims.Add(Fact(m, "CLM.APP", "A claim whose key differs from another only in case"));
        }));

        Assert.All(tree, d => Assert.Matches(SafePath(), d.Path));
        Assert.Equal(tree.Count, tree.Select(d => d.Path.ToLowerInvariant()).Distinct().Count());
        Assert.Contains(tree, d => d.Path == "components/cmp.app.md");
        AssertEveryRelativeLinkResolves(tree);
    }

    [Fact]
    public void ALinkToAClaimOrComponentNotInTheModel_IsPlainTextInsteadOfABrokenLink()
    {
        var tree = DeepReportRenderer.Render("acme", Model(m =>
            m.Claims.Single(c => c.Key == "clm.app").Evidence.Add(new ClaimEvidence(EvidenceKind.Claim, ClaimKey: "clm.gone"))));

        Assert.Contains("`clm.gone`", Doc(tree, "claims/clm.app.md"));
        AssertEveryRelativeLinkResolves(tree);
    }

    [Fact]
    public void Index_EscapesMermaidLabelsThatWouldBreakTheDiagram()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model(m =>
            m.Components[0] = m.Components[0] with { Name = "App \"v2\" [beta]" })), "index.md");

        Assert.Contains("c0[\"App #quot;v2#quot; #91;beta#93;\"]", index);
    }

    [Fact]
    public void NamesWithBracketsDoNotBreakLinkText()
    {
        var tree = DeepReportRenderer.Render("acme", Model(m =>
            m.Components[1] = m.Components[1] with { Name = "Lib [core]" }));

        Assert.Contains(@"[Lib \[core\]](components/cmp.lib.md)", Doc(tree, "index.md"));
        Assert.Contains(@"[Lib \[core\]](cmp.lib.md)", Doc(tree, "components/cmp.app.md"));
    }

    [Fact]
    public void ForAModelWithNoComponents_TheIndexSaysSoInsteadOfDrawingAnEmptyGraph()
    {
        var tree = DeepReportRenderer.Render("acme", new ProjectModel
        {
            ProjectId = Guid.NewGuid(),
            ModelVersion = 1,
            BaseCommit = "abc123",
            AnalysisRequestId = Guid.NewGuid(),
        });

        var index = Doc(tree, "index.md");
        Assert.DoesNotContain("```mermaid", index);
        Assert.Contains("No components were extracted", index);
        Assert.Single(tree);
    }

    [Fact]
    public void TableCells_EscapePipes()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model(m =>
            m.Components[1] = m.Components[1] with { Name = "Lib|Core" })), "index.md");

        Assert.Contains(@"Lib\|Core", index);
    }

    [Fact]
    public void FreeText_ShowsHtmlLiterallyInsteadOfLettingItRender()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
        {
            var c = m.Claims.Single(c => c.Key == "clm.app");
            c.Status = ClaimStatus.Corrected;
            c.Correction = "It is an executable because of <OutputType>Exe</OutputType> & nothing else";
        })), "claims/clm.app.md");

        Assert.Contains("> It is an executable because of &lt;OutputType&gt;Exe&lt;/OutputType&gt; &amp; nothing else", claim);
        Assert.DoesNotContain("<OutputType>", claim);
    }

    [Fact]
    public void ANameInATableLink_IsEscapedOnce()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model(m =>
            m.Components[1] = m.Components[1] with { Name = "Lib <core> & more" })), "index.md");

        Assert.Contains("| [Lib &lt;core&gt; &amp; more](components/cmp.lib.md) |", index);
        Assert.DoesNotContain("&amp;amp;", index);
        Assert.DoesNotContain("&amp;lt;", index);
    }

    [Fact]
    public void FreeText_KeepsItsCodeSpansAsWritten()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
            ReplaceClaim(m, Fact(m, "clm.app", "App sets `<OutputType>Exe</OutputType>` and ``a `tick` <here>``")))),
            "claims/clm.app.md");

        Assert.Contains("App sets `<OutputType>Exe</OutputType>` and ``a `tick` <here>``", claim);
    }

    [Fact]
    public void AnEvidenceSymbol_IsACodeSpan()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
            ReplaceClaim(m, new ModelClaim
            {
                ProjectModelId = m.Id,
                Key = "clm.app",
                Tier = ClaimTier.Fact,
                Statement = "App is a .NET project",
                Evidence = [new ClaimEvidence(EvidenceKind.Code, Path: "src/App/Program.cs", Lines: "3-9", Symbol: "List<Item>.Add")],
                Confidence = ClaimConfidence.High,
                Origin = ClaimOrigin.Deterministic,
            }))),
            "claims/clm.app.md");

        Assert.Contains("`src/App/Program.cs`:3-9 — `List<Item>.Add`", claim);
    }

    [Fact]
    public void AClaimPage_IsTitledByItsStatement_AndStillNamesItsKey()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model()), "claims/clm.ref.md");

        Assert.StartsWith("# App references Lib\n", claim);
        Assert.Contains("Claim `clm.ref`", claim);
    }

    [Fact]
    public void ALongStatement_IsShortenedInTheTitleButShownInFull()
    {
        var statement = string.Join(' ', Enumerable.Repeat("word", 40));
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
            ReplaceClaim(m, Fact(m, "clm.app", statement)))), "claims/clm.app.md");

        var title = claim[..claim.IndexOf('\n')];
        Assert.EndsWith("…", title);
        Assert.InRange(title.Length, 10, 2 + 80 + 1);
        Assert.DoesNotContain("wor…", title);
        Assert.Contains(statement, claim);
    }
}
