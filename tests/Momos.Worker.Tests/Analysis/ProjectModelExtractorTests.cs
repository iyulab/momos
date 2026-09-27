using System.Text.RegularExpressions;
using Momos.Worker.Analysis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Analysis;

public sealed class ProjectModelExtractorTests
{
    private static readonly ExecutionSessionHandle Session = new("s");

    /// <summary>A commit in the fake repository's history. <paramref name="Message"/> is the full
    /// message (subject first); <paramref name="Paths"/> are the files it changed.</summary>
    private sealed record Commit(string Sha, string Date, string Message, params string[] Paths)
    {
        public string Subject => Message.Split('\n')[0];
    }

    /// <param name="commits">The history, newest first.</param>
    private static FakeExecutionRuntimeProvider Repo(Dictionary<string, string> files, string head = "abc123\n", IReadOnlyList<Commit>? commits = null)
    {
        var fake = new FakeExecutionRuntimeProvider();
        fake.Respond = command => (command.Name, command.Args) switch
        {
            ("git", ["rev-parse", "HEAD"]) => new(true, head, null, 1),
            ("git", ["ls-files", "-z", "--", "*.csproj"]) => new(true, string.Concat(files.Keys.Select(k => k + "\0")), null, 1),
            ("git", ["show", var spec]) when spec.StartsWith("HEAD:", StringComparison.Ordinal) && files.TryGetValue(spec[5..], out var content)
                => new(true, content, null, 1),
            ("git", ["rev-list", "--count", ..]) => new(true, $"{Select(command.Args, commits ?? []).Count}\n", null, 1),
            ("git", ["log", "-z", "--no-show-signature", "--max-count=5", "--format=%H%x1f%cs%x1f%s", ..]) =>
                new(true, string.Concat(Select(command.Args, commits ?? []).Take(5).Select(c => $"{c.Sha}\u001f{c.Date}\u001f{c.Subject}\0")), null, 1),
            _ => new(false, null, $"unexpected command: {command.Name} {string.Join(' ', command.Args)}", 1),
        };
        return fake;
    }

    /// <summary>What git would select from <paramref name="commits"/> for a rev-list/log argument
    /// list: a <c>:(top)</c> / <c>:(top,literal)dir</c> pathspec after <c>--</c>, and an
    /// <c>-E --grep</c> pattern read as the POSIX expression the extractor writes.</summary>
    private static List<Commit> Select(IReadOnlyList<string> args, IReadOnlyList<Commit> commits)
    {
        var separator = args.ToList().IndexOf("--");
        var pathspecs = args.Skip(separator + 1).ToList();
        var grep = args.FirstOrDefault(a => a.StartsWith("--grep=", StringComparison.Ordinal))?["--grep=".Length..];
        var pattern = grep is null ? null : new Regex(grep.Replace("[:alnum:]", "a-zA-Z0-9", StringComparison.Ordinal), RegexOptions.Multiline);
        return commits
            .Where(c => pathspecs.All(spec => spec == ":(top)"
                || c.Paths.Any(p => p.StartsWith(spec[":(top,literal)".Length..] + "/", StringComparison.Ordinal))))
            .Where(c => pattern is null || pattern.IsMatch(c.Message))
            .ToList();
    }

    private static Task<ProjectModelPayload> ExtractAsync(Dictionary<string, string> files) =>
        new ProjectModelExtractor(Repo(files)).ExtractAsync(Session, CancellationToken.None);

    private const string App = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="..\Lib\Lib.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string Lib = """<Project Sdk="Microsoft.NET.Sdk" />""";

    [Fact]
    public async Task Extract_ReadsTheHeadCommit()
    {
        var model = await ExtractAsync(new() { ["src/Lib/Lib.csproj"] = Lib });

        Assert.Equal("abc123", model.BaseCommit);
    }

    [Fact]
    public async Task Extract_TurnsEachProjectIntoAComponentBackedByAFactAtItsPath()
    {
        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = App, ["src/Lib/Lib.csproj"] = Lib });

        var app = Assert.Single(model.Components, c => c.Name == "App");
        Assert.Equal("executable", app.Kind);
        Assert.Equal("library", Assert.Single(model.Components, c => c.Name == "Lib").Kind);
        var fact = Assert.Single(model.Claims, c => c.Key == Assert.Single(app.Claims));
        Assert.Equal(ClaimTier.Fact, fact.Tier);
        Assert.Equal(ClaimConfidence.High, fact.Confidence);
        Assert.Equal(ClaimOrigin.Deterministic, fact.Origin);
        var evidence = Assert.Single(fact.Evidence);
        Assert.Equal(EvidenceKind.Code, evidence.Kind);
        Assert.Equal("src/App/App.csproj", evidence.Path);
    }

    [Fact]
    public async Task Extract_ResolvesABackslashRelativeReferenceToTheTargetComponent()
    {
        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = App, ["src/Lib/Lib.csproj"] = Lib });

        var relation = Assert.Single(model.Relations);
        Assert.Equal(ModelIds.Component("src/App/App.csproj"), relation.From);
        Assert.Equal(ModelIds.Component("src/Lib/Lib.csproj"), relation.To);
        Assert.Equal("references", relation.Kind);
        var claim = Assert.Single(model.Claims, c => c.Key == Assert.Single(relation.Claims));
        Assert.Equal(ClaimTier.Fact, claim.Tier);
        var evidence = Assert.Single(claim.Evidence);
        Assert.Equal("src/App/App.csproj", evidence.Path);
        Assert.Equal("6", evidence.Lines);
    }

    [Fact]
    public async Task Extract_IgnoresAReferenceThatLeavesTheRepository()
    {
        var outside = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\..\..\Other\Other.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = outside });

        Assert.Empty(model.Relations);
        Assert.Single(model.Claims);
    }

    [Fact]
    public async Task Extract_IgnoresAReferenceThatClimbsOutAndBackIntoALookalikePath()
    {
        // "../../../src/Lib/Lib.csproj" from src/App leaves the repository before it comes back to a
        // path that happens to exist in it — it points at another checkout, not at this Lib.
        var climbing = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\..\..\src\Lib\Lib.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = climbing, ["src/Lib/Lib.csproj"] = Lib });

        Assert.Empty(model.Relations);
    }

    [Fact]
    public async Task Extract_IgnoresAReferenceToAProjectTheRepositoryDoesNotContain()
    {
        var dangling = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="$(RepoRoot)src\Lib\Lib.csproj;..\Gone\Gone.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = dangling, ["src/Lib/Lib.csproj"] = Lib });

        Assert.Empty(model.Relations);
    }

    [Fact]
    public async Task Extract_ResolvesEachEntryOfASemicolonSeparatedInclude()
    {
        var both = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj; ../Core/Core.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = both, ["src/Lib/Lib.csproj"] = Lib, ["src/Core/Core.csproj"] = Lib });

        Assert.Equal(
            [ModelIds.Component("src/Core/Core.csproj"), ModelIds.Component("src/Lib/Lib.csproj")],
            model.Relations.Select(r => r.To).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Extract_ReferencingTheSameProjectTwice_YieldsOneRelationAndUniqueClaimKeys()
    {
        // Common with per-target-framework conditions; a duplicate claim key would make the Host
        // reject the whole model.
        var twice = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """;

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = twice, ["src/Lib/Lib.csproj"] = Lib });

        Assert.Single(model.Relations);
        Assert.Equal(model.Claims.Count, model.Claims.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Extract_MatchesAReferenceWhoseCaseDiffersFromTheRepositoryPath()
    {
        // Authored on a case-insensitive file system, where MSBuild resolves it fine.
        var differentCase = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\lib\lib.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = differentCase, ["src/Lib/Lib.csproj"] = Lib });

        Assert.Equal(ModelIds.Component("src/Lib/Lib.csproj"), Assert.Single(model.Relations).To);
    }

    [Fact]
    public async Task Extract_IgnoresAProjectReferencingItself()
    {
        var self = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="App.csproj" /></ItemGroup></Project>""";

        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = self });

        Assert.Empty(model.Relations);
    }

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", "web-service")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Worker" />""", "worker-service")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""", "test")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""", "test")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""", "executable")]
    [InlineData("""<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", "executable")]
    public async Task Extract_ClassifiesTheProjectKind(string csproj, string kind)
    {
        var model = await ExtractAsync(new() { ["src/X/X.csproj"] = csproj });

        Assert.Equal(kind, Assert.Single(model.Components).Kind);
    }

    [Fact]
    public async Task Extract_FromARepositoryWithNoProjects_ReturnsAnEmptyModelNotAFailure()
    {
        var model = await ExtractAsync(new());

        Assert.Equal("abc123", model.BaseCommit);
        Assert.Empty(model.Components);
        Assert.Empty(model.Relations);
        Assert.Empty(model.Patterns);
        Assert.Empty(model.Decisions);
        Assert.Empty(model.Intents);
        Assert.Empty(model.Claims);
    }

    [Fact]
    public async Task Extract_KeysAreStableAcrossRuns()
    {
        var files = new Dictionary<string, string> { ["src/App/App.csproj"] = App, ["src/Lib/Lib.csproj"] = Lib };

        var first = await new ProjectModelExtractor(Repo(files)).ExtractAsync(Session, CancellationToken.None);
        var second = await new ProjectModelExtractor(Repo(files, head: "def456\n")).ExtractAsync(Session, CancellationToken.None);

        Assert.Equal(first.Claims.Select(c => c.Key).Order(), second.Claims.Select(c => c.Key).Order());
        Assert.Equal(first.Components.Select(c => c.Id).Order(), second.Components.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Extract_IdsAndKeysAreSafeAsPathSegmentsWhateverTheFileNames()
    {
        var model = await ExtractAsync(new() { ["src/Ünïcode dir/A&B #1.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\..\x\y.csproj" /></ItemGroup></Project>""", ["x/y.csproj"] = Lib });

        var ids = model.Components.Select(c => c.Id).Concat(model.Claims.Select(c => c.Key)).ToList();
        Assert.Single(model.Relations);
        Assert.Equal(5, ids.Count);
        Assert.All(ids, id => Assert.Matches("^(cmp|clm)\\.[0-9a-f]{12}$", id));
        Assert.Contains(model.Components, c => c.Name == "A&B #1");
    }

    [Fact]
    public async Task Extract_WhenRevParseFails_Throws()
    {
        var fake = new FakeExecutionRuntimeProvider { Respond = _ => new(false, null, "not a git repository", 1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None));
    }

    [Fact]
    public async Task Extract_WhenRevParseSucceedsWithoutOutput_ThrowsRatherThanSubmittingAModelWithNoCommit()
    {
        var fake = Repo(new() { ["src/Lib/Lib.csproj"] = Lib }, head: "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None));
    }

    [Fact]
    public async Task Extract_RecordsWhatItReadAndWhatItLeftUnread_IncludingAnUnparseableProjectFile()
    {
        var model = await ExtractAsync(new() { ["src/App/App.csproj"] = Lib, ["src/Broken/Broken.csproj"] = "<Project" });

        Assert.Equal(["project-manifests", "git-history"], model.Coverage.Analyzed.Select(a => a.Area));
        Assert.Contains("1 of 2", model.Coverage.Analyzed[0].Detail);
        var broken = Assert.Single(model.Coverage.NotAnalyzed, g => g.Reason.Contains("src/Broken/Broken.csproj", StringComparison.Ordinal));
        Assert.Equal("project-manifests", broken.Area);
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Area == "source-files");
        Assert.Contains(model.Coverage.NotAnalyzed, g => g.Area == "non-dotnet-projects");
        Assert.Empty(model.Coverage.Rejected);
        Assert.Null(model.Coverage.Generator);
        Assert.Empty(model.Outline);
        Assert.Empty(model.Flows);
        Assert.Empty(model.Invariants);
    }

    [Fact]
    public async Task Extract_AnUnparseableProjectFile_IsSkippedRatherThanFailingTheWholeAnalysis()
    {
        var model = await ExtractAsync(new() { ["src/Bad/Bad.csproj"] = "<Project", ["src/Lib/Lib.csproj"] = Lib });

        Assert.Equal("Lib", Assert.Single(model.Components).Name);
    }

    [Fact]
    public async Task Extract_AProjectFileWithADocumentTypeDefinition_IsSkippedWithoutExpandingIt()
    {
        var withDtd = """<?xml version="1.0"?><!DOCTYPE Project [<!ENTITY a "aaaaaaaaaa">]><Project Sdk="Microsoft.NET.Sdk">&a;</Project>""";

        var model = await ExtractAsync(new() { ["src/Dtd/Dtd.csproj"] = withDtd, ["src/Lib/Lib.csproj"] = Lib });

        Assert.Equal("Lib", Assert.Single(model.Components).Name);
    }

    [Fact]
    public async Task Extract_RunsOnlyReadOnlyGitCommands()
    {
        var fake = Repo(new() { ["src/App/App.csproj"] = App, ["src/Lib/Lib.csproj"] = Lib });

        await new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None);

        Assert.All(fake.ExecutedCommands, e =>
        {
            Assert.Equal(Session, e.Session);
            Assert.Equal("git", e.Command.Name);
            Assert.Contains(e.Command.Args[0], new[] { "rev-parse", "ls-files", "show", "rev-list", "log" });
        });
    }

    private static readonly Commit[] History =
    [
        new("c7", "2026-09-20", "Tighten Lib validation", "src/Lib/Validate.cs"),
        new("c6", "2026-09-19", "Rename App.Tests fixtures", "src/App.Tests/Fixture.cs"),
        new("c5", "2026-09-18", "Move App startup into Program", "src/App/Program.cs"),
        new("c4", "2026-09-17", "Lib: add parser\n\nThe parser moves out of App.", "src/Lib/Parse.cs", "src/App/Program.cs"),
        new("c3", "2026-09-16", "Library cleanup", "src/Lib/Old.cs"),
        new("c2", "2026-09-15", "Docs", "README.md"),
        new("c1", "2026-09-14", "Seed", "src/Lib/Lib.csproj", "src/App/App.csproj"),
    ];

    private static Task<ProjectModelPayload> ExtractWithHistoryAsync(IReadOnlyList<Commit> commits, string head = "abc123\n") =>
        new ProjectModelExtractor(Repo(new() { ["src/App/App.csproj"] = App, ["src/Lib/Lib.csproj"] = Lib }, head, commits))
            .ExtractAsync(Session, CancellationToken.None);

    private static ClaimPayload ClaimFor(ProjectModelPayload model, string component, string keyKind, string projectPath)
    {
        var key = ModelIds.Claim($"{keyKind}|{projectPath}");
        Assert.Contains(key, Assert.Single(model.Components, c => c.Name == component).Claims);
        return Assert.Single(model.Claims, c => c.Key == key);
    }

    [Fact]
    public async Task Extract_GivesEachComponentAHistoryClaimCitingTheLatestCommitsUnderItsDirectory()
    {
        var model = await ExtractWithHistoryAsync(History);

        var lib = ClaimFor(model, "Lib", "history", "src/Lib/Lib.csproj");
        Assert.Equal(ClaimTier.History, lib.Tier);
        Assert.Equal(ClaimConfidence.High, lib.Confidence);
        Assert.Equal(ClaimOrigin.Deterministic, lib.Origin);
        Assert.Equal("Lib: 4 commits changed files under src/Lib/; the latest, c7 on 2026-09-20, is \"Tighten Lib validation\".", lib.Statement);
        Assert.Equal(["c7", "c4", "c3", "c1"], lib.Evidence.Select(e => e.Sha));
        Assert.All(lib.Evidence, e => Assert.Equal(EvidenceKind.Commit, e.Kind));

        // src/App.Tests/ is a sibling directory, not under src/App/.
        var app = ClaimFor(model, "App", "history", "src/App/App.csproj");
        Assert.Equal(["c5", "c4", "c1"], app.Evidence.Select(e => e.Sha));
    }

    [Fact]
    public async Task Extract_CitesAtMostFiveCommitsButCountsThemAll()
    {
        var commits = Enumerable.Range(1, 8).Reverse()
            .Select(i => new Commit($"s{i}", $"2026-09-0{i}", $"change {i}", "src/Lib/File.cs"))
            .ToList();

        var lib = ClaimFor(await ExtractWithHistoryAsync(commits), "Lib", "history", "src/Lib/Lib.csproj");

        Assert.StartsWith("Lib: 8 commits changed files under src/Lib/; the latest, s8 on 2026-09-08,", lib.Statement);
        Assert.Equal(["s8", "s7", "s6", "s5", "s4"], lib.Evidence.Select(e => e.Sha));
    }

    [Fact]
    public async Task Extract_AddsAMentionClaimForCommitMessagesThatNameTheComponent()
    {
        var model = await ExtractWithHistoryAsync(History);

        var lib = ClaimFor(model, "Lib", "mentions", "src/Lib/Lib.csproj");
        Assert.Equal(ClaimTier.History, lib.Tier);
        Assert.Equal("2 commits name Lib in the message; the latest, c7 on 2026-09-20, is \"Tighten Lib validation\".", lib.Statement);

        // "Library" is a longer word, not the name.
        Assert.Equal(["c7", "c4"], lib.Evidence.Select(e => e.Sha));
    }

    [Fact]
    public async Task Extract_AMentionIsAWholeName_NotAPrefixOfADottedName()
    {
        var model = await ExtractWithHistoryAsync(History);

        // c6 names App.Tests, not App; c4's body names App at the end of a sentence; c5 names it mid-sentence.
        var app = ClaimFor(model, "App", "mentions", "src/App/App.csproj");
        Assert.Equal(["c5", "c4"], app.Evidence.Select(e => e.Sha));
    }

    [Fact]
    public async Task Extract_NoMentionClaimWhenNoMessageNamesTheComponent()
    {
        var model = await ExtractWithHistoryAsync([new("c1", "2026-09-14", "Seed", "src/Lib/Lib.csproj", "src/App/App.csproj")]);

        Assert.DoesNotContain(model.Claims, c => c.Key == ModelIds.Claim("mentions|src/Lib/Lib.csproj"));
        Assert.Equal(2, Assert.Single(model.Components, c => c.Name == "Lib").Claims.Count);
    }

    [Fact]
    public async Task Extract_TheSameCommitGivesTheSameModel()
    {
        var first = await ExtractWithHistoryAsync(History);
        var second = await ExtractWithHistoryAsync(History);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public async Task Extract_CountsHistoryFromTheCommitItReadNotFromWhereverHeadMoves()
    {
        var fake = Repo(new() { ["src/Lib/Lib.csproj"] = Lib }, "abc123\n", History);

        await new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None);

        Assert.All(fake.ExecutedCommands.Where(e => e.Command.Args[0] is "rev-list" or "log"),
            e => Assert.Contains("abc123", e.Command.Args));
    }

    [Fact]
    public async Task Extract_ADirectoryNameIsReadLiterallyNotAsAGlob()
    {
        var fake = Repo(new() { ["src/[Lib]*/Lib.csproj"] = Lib }, commits: History);

        await new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None);

        Assert.Contains(fake.ExecutedCommands, e => e.Command.Args[0] == "log" && e.Command.Args[^1] == ":(top,literal)src/[Lib]*");
    }

    [Fact]
    public async Task Extract_AProjectAtTheRepositoryRootCountsEveryCommit()
    {
        var model = await new ProjectModelExtractor(Repo(new() { ["Lib.csproj"] = Lib }, commits: History))
            .ExtractAsync(Session, CancellationToken.None);

        var lib = ClaimFor(model, "Lib", "history", "Lib.csproj");
        Assert.StartsWith("Lib: 7 commits changed files under the repository root;", lib.Statement);
    }

    [Fact]
    public async Task Extract_WhenGitLogFails_Throws()
    {
        var fake = Repo(new() { ["src/Lib/Lib.csproj"] = Lib }, commits: History);
        var respond = fake.Respond!;
        fake.Respond = c => c.Args[0] == "log" ? new(false, null, "fatal: bad object", 1) : respond(c);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectModelExtractor(fake).ExtractAsync(Session, CancellationToken.None));
    }
}
