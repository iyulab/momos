using Momos.Worker.Analysis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Analysis;

public sealed class ProjectModelExtractorTests
{
    private static readonly ExecutionSessionHandle Session = new("s");

    private static FakeExecutionRuntimeProvider Repo(Dictionary<string, string> files, string head = "abc123\n")
    {
        var fake = new FakeExecutionRuntimeProvider();
        fake.Respond = command => (command.Name, command.Args) switch
        {
            ("git", ["rev-parse", "HEAD"]) => new(true, head, null, 1),
            ("git", ["ls-files", "-z", "--", "*.csproj"]) => new(true, string.Concat(files.Keys.Select(k => k + "\0")), null, 1),
            ("git", ["show", var spec]) when spec.StartsWith("HEAD:", StringComparison.Ordinal) && files.TryGetValue(spec[5..], out var content)
                => new(true, content, null, 1),
            _ => new(false, null, $"unexpected command: {command.Name} {string.Join(' ', command.Args)}", 1),
        };
        return fake;
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
            Assert.Contains(e.Command.Args[0], new[] { "rev-parse", "ls-files", "show" });
        });
    }
}
