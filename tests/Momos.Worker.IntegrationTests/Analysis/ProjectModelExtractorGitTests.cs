using Momos.Worker.Analysis;
using Momos.Worker.Execution;
using Momos.Worker.IntegrationTests.Execution;

namespace Momos.Worker.IntegrationTests.Analysis;

/// <summary>
/// The extractor's history claims against a real git repository: the pathspec and message
/// expressions it writes have to mean to git what the extractor assumes they mean, which a scripted
/// runtime cannot show.
/// </summary>
public sealed class ProjectModelExtractorGitTests : IAsyncLifetime
{
    private const string Lib = """<Project Sdk="Microsoft.NET.Sdk" />""";

    private readonly LocalProcessExecutionRuntimeProvider _runtime = new();
    private readonly string _source = Directory.CreateTempSubdirectory("momos-history-source-").FullName;
    private ExecutionSessionHandle _session = null!;

    public async Task InitializeAsync()
    {
        await GitAsync("init", "-q", "-b", "main");
        await CommitAsync("Seed", ("src/Lib/Lib.csproj", Lib), ("src/App/App.csproj", Lib), ("Root.csproj", Lib));
        await CommitAsync("Library cleanup", ("src/Lib/Old.cs", "1"));
        await CommitAsync("Lib: add parser\n\nThe parser moves out of App.", ("src/Lib/Parse.cs", "1"), ("src/App/Program.cs", "1"));
        await CommitAsync("Rename App.Tests fixtures", ("src/App.Tests/Fixture.cs", "1"));
        await CommitAsync("Glob-looking directory", ("src/[Lib]/Glob.csproj", Lib));
        await CommitAsync("Tighten Lib validation", ("src/Lib/Validate.cs", "1"));

        _session = await _runtime.CreateSessionAsync(new ExecutionSessionRequest("native"));
        var clone = await _runtime.ExecuteAsync(_session, new ExecutionCommand("git", ["clone", "-q", "--", _source, "."]));
        Assert.True(clone.Success, clone.Error);
    }

    public Task DisposeAsync()
    {
        _runtime.Dispose();
        LocalProcessExecutionRuntimeProvider.DeleteDirectory(_source);
        return Task.CompletedTask;
    }

    private Task<ProjectModelPayload> ExtractAsync() =>
        new ProjectModelExtractor(_runtime).ExtractAsync(_session, CancellationToken.None);

    private static ClaimPayload ClaimFor(ProjectModelPayload model, string keyKind, string projectPath) =>
        Assert.Single(model.Claims, c => c.Key == ModelIds.Claim($"{keyKind}|{projectPath}"));

    [Fact]
    public async Task HistoryCountsTheCommitsUnderTheProjectDirectoryOnly()
    {
        var model = await ExtractAsync();

        Assert.StartsWith("Lib: 4 commits changed files under src/Lib/; the latest, ", ClaimFor(model, "history", "src/Lib/Lib.csproj").Statement);
        Assert.EndsWith(", is \"Tighten Lib validation\".", ClaimFor(model, "history", "src/Lib/Lib.csproj").Statement);

        // src/App.Tests/ is a sibling, not under src/App/.
        Assert.StartsWith("App: 2 commits changed files under src/App/;", ClaimFor(model, "history", "src/App/App.csproj").Statement);
        Assert.StartsWith("Root: 6 commits changed files under the repository root;", ClaimFor(model, "history", "Root.csproj").Statement);
    }

    [Fact]
    public async Task ADirectoryThatLooksLikeAGlobIsMatchedLiterally()
    {
        var model = await ExtractAsync();

        Assert.StartsWith("Glob: 1 commit changed files under src/[Lib]/;", ClaimFor(model, "history", "src/[Lib]/Glob.csproj").Statement);
    }

    [Fact]
    public async Task MentionsMatchTheWholeNameInTheFullMessage()
    {
        var model = await ExtractAsync();

        // "Library" is a longer word; "App.Tests" is another name; "out of App." ends a sentence in a body.
        Assert.StartsWith("2 commits name Lib in the message; the latest, ", ClaimFor(model, "mentions", "src/Lib/Lib.csproj").Statement);
        var app = ClaimFor(model, "mentions", "src/App/App.csproj");
        Assert.StartsWith("1 commit names App in the message; the latest, ", app.Statement);
        Assert.EndsWith(", is \"Lib: add parser\".", app.Statement);
    }

    [Fact]
    public async Task EvidenceIsTheFullShaOfCommitsThatExist()
    {
        var model = await ExtractAsync();

        foreach (var evidence in model.Claims.Where(c => c.Tier == ClaimTier.History).SelectMany(c => c.Evidence))
        {
            Assert.Equal(EvidenceKind.Commit, evidence.Kind);
            Assert.Matches("^[0-9a-f]{40}$", evidence.Sha);
            var type = await _runtime.ExecuteAsync(_session, new ExecutionCommand("git", ["cat-file", "-t", evidence.Sha!]));
            Assert.Equal("commit", type.Output?.Trim());
        }
    }

    [Fact]
    public async Task TheSameCommitGivesTheSameModel()
    {
        var first = await ExtractAsync();
        var second = await ExtractAsync();

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }

    private async Task CommitAsync(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", message);
    }

    private async Task GitAsync(params string[] args)
    {
        var result = await LocalProcessExecutionRuntimeProvider.RunAsync(_source, new ExecutionCommand("git", args));
        Assert.True(result.Success, result.Error);
    }
}
