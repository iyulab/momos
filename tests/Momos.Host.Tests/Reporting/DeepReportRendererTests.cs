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
            Origin = ClaimOrigin.Synthesized,
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

    [GeneratedRegex(@"^(index|unknowns|[a-z0-9][a-z0-9-]{0,63}|components/[a-z0-9][a-z0-9._-]*|claims/[a-z0-9][a-z0-9._-]*)\.md$")]
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

        string[] expected = ["claims/clm.app.md", "claims/clm.lib.md", "claims/clm.ref.md", "components/cmp.app.md", "components/cmp.lib.md", "index.md", "unknowns.md"];
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
    public void Index_PointsDecisionsWithoutARecordedRationaleAtTheUnknownsPage()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model()), "index.md");

        Assert.Contains("App depends on Lib, never the reverse", index);
        Assert.Contains("Decisions without a recorded rationale are asked about in [what this report does not know](unknowns.md).", index);
    }

    [Fact]
    public void Index_ListsEveryClaimWithItsCurrentStatus()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Model(m =>
            m.Claims.Single(c => c.Key == "clm.lib").Status = ClaimStatus.Disputed)), "index.md");

        Assert.Contains("## Claims", index);
        Assert.Contains("| [`clm.lib`](claims/clm.lib.md) | Fact | High | Deterministic | Disputed | Lib is a .NET project |", index);
        Assert.Contains("| [`clm.app`](claims/clm.app.md) | Fact | High | Deterministic | Proposed | App is a .NET project |", index);
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
        // Even an empty model keeps the spine: the unknowns page is never dropped.
        Assert.Equal(["index.md", "unknowns.md"], tree.Select(d => d.Path));
        Assert.Contains("[1 open question](unknowns.md)", index);
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

    /// <summary>Repository text can hold the report's own wiki-link syntax (a citation marker such as
    /// <c>[[cite:1]]</c>); left as it is, the site generator reads it as a link to a missing page and
    /// refuses to build.</summary>
    [Fact]
    public void FreeText_ShowsWikiLinkSyntaxLiterally_OutsideCodeSpans()
    {
        var claim = Doc(DeepReportRenderer.Render("acme", Model(m =>
        {
            var c = m.Claims.Single(c => c.Key == "clm.app");
            c.Status = ClaimStatus.Corrected;
            c.Correction = "Markers look like [[cite:1]], [[[3]]] or `[[2]]` in code.";
        })), "claims/clm.app.md");

        Assert.Contains("> Markers look like &#91;[cite:1]], &#91;&#91;[3]]] or `[[2]]` in code.", claim);
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

    [Fact]
    public void EveryClaim_ShowsWhetherItWasExtractedOrSynthesized()
    {
        var tree = DeepReportRenderer.Render("Acme", RichModel());

        Assert.Contains("| Origin |", Doc(tree, "index.md"));
        Assert.Contains("Synthesized", Doc(tree, "claims/clm.split.md"));
        Assert.Contains("Deterministic", Doc(tree, "claims/clm.app.md"));
    }

    [Fact]
    public void AComponentsResponsibility_IsShownWithTheClaimsBehindIt()
    {
        var model = Model(m =>
        {
            m.Components.Clear();
            m.Components.Add(new ModelComponent("cmp.app", "App", "executable", "Serves the public API", ["clm.app"]));
        });

        var page = Doc(DeepReportRenderer.Render("Acme", model), "components/cmp.app.md");

        var responsibility = page.IndexOf("Serves the public API", StringComparison.Ordinal);
        var backing = page.IndexOf("Backed by", StringComparison.Ordinal);
        Assert.True(responsibility >= 0 && backing > responsibility, page);
        Assert.Contains("(../claims/clm.app.md)", page[backing..]);
    }

    [Fact]
    public void Unknowns_AsksWhyForEveryDecisionWithoutARecordedRationale()
    {
        var unknowns = Doc(DeepReportRenderer.Render("acme", Model()), "unknowns.md");

        Assert.Contains("## Why was this decided?", unknowns);
        Assert.Contains("- App depends on Lib, never the reverse — why?", unknowns);
        Assert.Contains("record the reason in the repository", unknowns);
    }

    [Fact]
    public void Unknowns_AsksWhetherEachUnreviewedLowConfidenceReadingIsRight_AndNamesTheVerdictRoute()
    {
        var model = RichModel();
        var unknowns = Doc(DeepReportRenderer.Render("acme", model), "unknowns.md");

        Assert.Contains("## Is this reading right?", unknowns);
        Assert.Contains("The library looks deliberately UI-free", unknowns);
        Assert.Contains($"/projects/{model.ProjectId}/model/claims/{{claimKey}}/corrections", unknowns);
    }

    [Fact]
    public void Unknowns_ListsDisputedClaims_AndDropsAReadingOnceADeveloperHasJudgedIt()
    {
        var unknowns = Doc(DeepReportRenderer.Render("acme", RichModel(m =>
        {
            m.Claims.Single(c => c.Key == "clm.split").Status = ClaimStatus.Confirmed;
            m.Claims.Single(c => c.Key == "clm.ref").Status = ClaimStatus.Disputed;
        })), "unknowns.md");

        Assert.DoesNotContain("## Is this reading right?", unknowns);
        Assert.Contains("## Disputed claims", unknowns);
        Assert.Contains("App references Lib", unknowns);
    }

    private static ProjectModel WithCoverage(ModelCoverage? coverage, Action<ProjectModel>? configure = null)
    {
        var source = Model(configure);
        var model = new ProjectModel
        {
            Id = source.Id,
            ProjectId = source.ProjectId,
            ModelVersion = source.ModelVersion,
            BaseCommit = source.BaseCommit,
            AnalysisRequestId = source.AnalysisRequestId,
            CreatedAt = source.CreatedAt,
            Coverage = coverage,
        };
        model.Components.AddRange(source.Components);
        model.Relations.AddRange(source.Relations);
        model.Patterns.AddRange(source.Patterns);
        model.Decisions.AddRange(source.Decisions);
        model.Intents.AddRange(source.Intents);
        model.Flows.AddRange(source.Flows);
        model.Invariants.AddRange(source.Invariants);
        model.Outline.AddRange(source.Outline);
        foreach (var c in source.Claims)
        {
            model.Claims.Add(c);
        }

        return model;
    }

    [Fact]
    public void Unknowns_ListsTheAreasTheAnalysisLeftUnread_AndWhatItDiscarded()
    {
        var unknowns = Doc(DeepReportRenderer.Render("acme", WithCoverage(new ModelCoverage(
            [new CoverageArea("project-manifests", "2 of 2 project files")],
            [new CoverageGap("source-files", "Only project files and git history are read.")],
            [new CoverageRejection("evidence does not locate", 3)],
            null))), "unknowns.md");

        Assert.Contains("## Areas not analyzed", unknowns);
        Assert.Contains("**source-files** — Only project files and git history are read.", unknowns);
        Assert.Contains("## Discarded during analysis", unknowns);
        Assert.Contains("evidence does not locate: 3", unknowns);
    }

    [Fact]
    public void Unknowns_ForAModelWithoutCoverage_SaysSoInsteadOfClaimingNothingWasSkipped()
    {
        var unknowns = Doc(DeepReportRenderer.Render("acme", Model()), "unknowns.md");

        Assert.Contains("records no analysis coverage", unknowns);
        Assert.DoesNotContain("recorded no unread areas", unknowns);
    }

    [Fact]
    public void Index_LinksTheUnknownsPageWithItsCount_AndShowsUnderstandingAsAnUnvalidatedMeasure()
    {
        var index = Doc(DeepReportRenderer.Render("acme", RichModel(m =>
            m.Claims.Single(c => c.Key == "clm.app").Status = ClaimStatus.Confirmed)), "index.md");

        // RichModel: 1 unrecorded decision + 1 low-confidence proposed reading + no coverage (1).
        Assert.Contains("[3 open questions](unknowns.md)", index);
        Assert.Contains("## Understanding", index);
        Assert.Contains("not a validated metric", index);
        Assert.Contains("| Confirmed | 1 |", index);
        Assert.Contains("| Not yet reviewed | 3 |", index);
        Assert.Contains("| Decisions without a recorded rationale | 1 |", index);
    }

    private static ProjectModel Outlined(Action<ProjectModel>? configure = null) => RichModel(m =>
    {
        m.Flows.Add(new ModelFlow("flw.build", "Build", [new FlowStep("cmp.app", "clm.app"), new FlowStep(null, "clm.ref")], ["clm.ref"]));
        m.Invariants.Add(new ModelInvariant("inv.layering", "Lib never references App", "contract", ["cmp.lib"], ["clm.ref"]));
        m.Outline.Add(new OutlineSection("sec.map", "system-map.md", "System map", "What the parts are and how they connect", ["clm.app"],
            [new OutlineBlock(OutlineBlockKind.Component, "cmp.app"), new OutlineBlock(OutlineBlockKind.Pattern, "pat.layers")]));
        m.Outline.Add(new OutlineSection("sec.flows", "core-flows.md", "Core flows", "How a build moves", [],
            [new OutlineBlock(OutlineBlockKind.Flow, "flw.build"), new OutlineBlock(OutlineBlockKind.Invariant, "inv.layering"),
             new OutlineBlock(OutlineBlockKind.Decision, "dec.layering"), new OutlineBlock(OutlineBlockKind.Intent, "int.split"),
             new OutlineBlock(OutlineBlockKind.Claim, "clm.lib")]));
        m.Outline.Add(new OutlineSection("sec.risks", "risks.md", "Risks", "What could break", [], []));
        configure?.Invoke(m);
    });

    [Fact]
    public void AnOutlinedModel_GetsOnePagePerChapter_PlusTheSpine()
    {
        var tree = DeepReportRenderer.Render("acme", Outlined());

        var pages = tree.Select(d => d.Path).Where(p => !p.Contains('/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["core-flows.md", "index.md", "risks.md", "system-map.md", "unknowns.md"], pages);
        Assert.All(tree, d => Assert.Matches(SafePath(), d.Path));
        AssertEveryRelativeLinkResolves(tree);
    }

    [Fact]
    public void TheOutlinedIndex_ListsChaptersInOrder_WithTheirOwnerSummaryClaims()
    {
        var index = Doc(DeepReportRenderer.Render("acme", Outlined()), "index.md");

        var map = index.IndexOf("[System map](system-map.md)", StringComparison.Ordinal);
        var flows = index.IndexOf("[Core flows](core-flows.md)", StringComparison.Ordinal);
        Assert.True(map >= 0 && flows > map, "chapters must appear in outline order");
        Assert.Contains("_What the parts are and how they connect_", index);
        Assert.Contains("App is a .NET project", index); // sec.map's owner summary claim, quoted
        // The chapters lead; structure and the claims table follow so no page is left unreachable.
        Assert.True(index.IndexOf("## Structure", StringComparison.Ordinal) > flows, "structure must follow the chapters");
    }

    [Fact]
    public void ASectionPage_ShowsPurposeAsASubtitleNotAClaim()
    {
        var page = Doc(DeepReportRenderer.Render("acme", Outlined()), "system-map.md");

        Assert.StartsWith("# System map\n\n_What the parts are and how they connect_\n", page);
        Assert.Contains("## Owner summary", page);
        Assert.Contains("- App is a .NET project (", page);
    }

    [Fact]
    public void EveryBlockKind_RendersItsElementBesideTheClaimsThatBackIt()
    {
        var page = Doc(DeepReportRenderer.Render("acme", Outlined()), "core-flows.md");

        Assert.Contains("### Flow: Build", page);
        Assert.Contains("1. [App](components/cmp.app.md) — App is a .NET project ([`clm.app`](claims/clm.app.md))", page);
        Assert.Contains("2. App references Lib ([`clm.ref`](claims/clm.ref.md))", page);
        Assert.Contains("### Invariant: Lib never references App", page);
        Assert.Contains("### Decision: App depends on Lib, never the reverse", page);
        Assert.Contains("Rationale: _unrecorded_", page);
        Assert.Contains("### Intent: Keep the library free of UI concerns", page);
        Assert.Contains("- Lib is a .NET project — Fact · High", page);
        // Element text never stands alone: each element block carries its backing claims.
        Assert.Equal(4, CountOf(page, "_Backed by "));
    }

    [Fact]
    public void AChapterWithNothingInIt_SaysNoEvidenceWasFound_AndIsCountedAsUnknown()
    {
        var tree = DeepReportRenderer.Render("acme", Outlined());

        Assert.Contains("No evidence for this chapter was found in this repository.", Doc(tree, "risks.md"));
        // The summary says so too: a reader of the index alone must not take the chapter for covered.
        var index = Doc(tree, "index.md");
        var risks = index[index.IndexOf("[Risks](risks.md)", StringComparison.Ordinal)..];
        Assert.Contains("No evidence for this chapter was found in this repository.", risks);
        Assert.Contains("[Risks](risks.md)", Doc(tree, "unknowns.md"));
    }

    [Fact]
    public void AComponentBlock_LinksItsPage_WhereRelationsAreShown()
    {
        var page = Doc(DeepReportRenderer.Render("acme", Outlined()), "system-map.md");

        Assert.Contains("### Component: [App](components/cmp.app.md)", page);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void AKeyEndingInANewline_StillGetsASafeFileName()
    {
        // `$` also matches before a final newline; a stem check that uses it would let "a\n" through as-is.
        var tree = DeepReportRenderer.Render("acme", Model(m => m.Claims.Add(Fact(m, "clm.x\n", "X is a project"))));

        Assert.All(tree, d => Assert.Matches(SafePath(), d.Path));
    }

    [Fact]
    public void InAnOutlinedReport_EveryPageIsReachableFromSomeLink()
    {
        // A chapter cites only some claims and components; the rest still need a way in.
        var tree = DeepReportRenderer.Render("acme", Outlined(m => m.Claims.Add(Fact(m, "clm.orphan", "Nothing cites this"))));

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in tree)
        {
            var dir = doc.Path.Contains('/') ? doc.Path[..doc.Path.LastIndexOf('/')] : "";
            foreach (Match link in MarkdownLink().Matches(doc.Content))
            {
                var href = link.Groups[1].Value;
                targets.Add(href.StartsWith("../", StringComparison.Ordinal) ? href[3..] : (dir.Length == 0 ? "" : dir + "/") + href);
            }
        }

        Assert.All(tree.Where(d => d.Path != "index.md"), d => Assert.Contains(d.Path, targets));
        Assert.Contains("```mermaid", Doc(tree, "index.md"));
    }

    [Fact]
    public void AMapBlock_DrawsTheWholeStructure_OrAComponentWithItsNeighbours()
    {
        var tree = DeepReportRenderer.Render("acme", Outlined(m =>
        {
            m.Components.Add(new ModelComponent("cmp.tool", "Tool", "executable", null, ["clm.app"]));
            m.Outline.Add(new OutlineSection("sec.maps", "maps.md", "Maps", "", [],
                [new OutlineBlock(OutlineBlockKind.Map, "*"), new OutlineBlock(OutlineBlockKind.Map, "cmp.lib")]));
        }));
        var page = Doc(tree, "maps.md");

        var whole = page[..page.IndexOf("### Map: Lib", StringComparison.Ordinal)];
        var around = page[page.IndexOf("### Map: Lib", StringComparison.Ordinal)..];
        Assert.Contains("### Map: whole structure", whole);
        Assert.Contains("[\"Tool\"]", whole);
        Assert.Contains("[\"App\"]", around);   // App references Lib: a neighbour
        Assert.DoesNotContain("Tool", around);   // unrelated to Lib
        Assert.Contains("-->|references|", around);
    }

    [Fact]
    public void APurpose_CannotEscapeItsItalicSubtitle()
    {
        var page = Doc(DeepReportRenderer.Render("acme", Outlined(m =>
            m.Outline[0] = m.Outline[0] with { Purpose = "x_ **All inputs are validated** _y" })), "system-map.md");

        Assert.Contains(@"_x\_ \*\*All inputs are validated\*\* \_y_", page);
    }

    [Fact]
    public void AChapterTitle_CannotBecomeALink()
    {
        var tree = DeepReportRenderer.Render("acme", Outlined(m =>
            m.Outline[0] = m.Outline[0] with { Title = "[Audit passed](https://example.invalid)" }));

        Assert.StartsWith(@"# \[Audit passed\](https://example.invalid)", Doc(tree, "system-map.md"));
    }
}
