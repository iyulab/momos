using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Execution;

namespace Momos.Worker.Tests.Analysis.Synthesis;

public sealed class EvidenceVerifierTests
{
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Old = "2222222222222222222222222222222222222222";

    private static readonly Dictionary<string, string> Files = new()
    {
        ["src/App/Queue.cs"] = "namespace App;\npublic sealed class Queue\n{\n    public void Claim() { }\n}\n",
    };

    private static (EvidenceVerifier Verifier, FakeExecutionRuntimeProvider Runtime) Verifier(string? readme = null)
    {
        var runtime = new FakeExecutionRuntimeProvider
        {
            Respond = command => command.Args switch
            {
                ["show", var spec] when spec.StartsWith(Base + ":", StringComparison.Ordinal) && Files.TryGetValue(spec[(Base.Length + 1)..], out var content)
                    => new(true, content, null, 1),
                ["rev-parse", "--verify", "--quiet", var rev] when rev.StartsWith("2222222", StringComparison.Ordinal) => new(true, Old + "\n", null, 1),
                ["merge-base", "--is-ancestor", Old, Base] => new(true, "", null, 1),
                ["grep", "-F", "-q", "-e", var url, Base] when readme?.Contains(url, StringComparison.Ordinal) == true => new(true, "", null, 1),
                ["log", "-F", "--grep=https://example.invalid/acme/pull/7", "--format=%H", "-n", "1", Base] => new(true, Old + "\n", null, 1),
                _ => new(false, "", "exit 1", 1),
            },
        };
        return (new EvidenceVerifier(runtime, new ExecutionSessionHandle("s"), Base), runtime);
    }

    public static TheoryData<string, EvidencePayload, bool> Cases => new()
    {
        { "symbol in the file", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "sealed class Queue"), true },
        { "symbol in the cited line", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "L4"), true },
        { "a range", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "2-4"), true },
        { "symbol outside the cited lines", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "1-2"), false },
        { "symbol not in the file", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Release()"), false },
        { "lines past the end", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "4-40"), false },
        { "reversed lines", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "4-2"), false },
        { "unreadable lines", new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "around 4"), false },
        { "a file not at the analyzed commit", new(EvidenceKind.Code, Path: "src/App/New.cs", Symbol: "x"), false },
        { "a path climbing out", new(EvidenceKind.Code, Path: "../secrets.txt", Symbol: "x"), false },
        { "a path that reads as a flag", new(EvidenceKind.Code, Path: "--output=x", Symbol: "x"), false },
        { "an absolute path", new(EvidenceKind.Code, Path: "/etc/passwd", Symbol: "root"), false },
        { "an ancestor commit", new(EvidenceKind.Commit, Sha: "2222222"), true },
        { "not a sha", new(EvidenceKind.Commit, Sha: "HEAD~1"), false },
        { "an unknown commit", new(EvidenceKind.Commit, Sha: "3333333"), false },
        { "a pull request named in a commit message", new(EvidenceKind.PullRequest, Url: "https://example.invalid/acme/pull/7"), true },
        { "an issue named nowhere", new(EvidenceKind.Issue, Url: "https://example.invalid/acme/issues/9"), false },
        { "a url that is not https", new(EvidenceKind.Issue, Url: "http://example.invalid/acme/issues/9"), false },
        { "a claim is left to the rules", new(EvidenceKind.Claim, ClaimKey: "clm.any"), true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Verify(string because, EvidencePayload evidence, bool accepted)
    {
        var (verifier, _) = Verifier();

        var (verdict, _) = await verifier.VerifyAsync(evidence, CancellationToken.None);

        Assert.True(accepted == verdict.Accepted, $"{because}: {verdict.Message}");
        if (!accepted)
        {
            Assert.Equal(RejectionReason.EvidenceNotFound, verdict.Reason);
        }
    }

    [Fact]
    public async Task AnIssueUrlInTheRepository_IsAccepted()
    {
        var (verifier, _) = Verifier(readme: "See https://example.invalid/acme/issues/9 for why.");

        var (verdict, _) = await verifier.VerifyAsync(new(EvidenceKind.Issue, Url: "https://example.invalid/acme/issues/9"), CancellationToken.None);

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public async Task EvidenceIsNormalized_ToAFullShaAndBareLineNumbers()
    {
        var (verifier, _) = Verifier();

        var (_, commit) = await verifier.VerifyAsync(new(EvidenceKind.Commit, Sha: "2222222"), CancellationToken.None);
        var (_, code) = await verifier.VerifyAsync(new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim()", Lines: "L2-L4"), CancellationToken.None);

        Assert.Equal(Old, commit.Sha);
        Assert.Equal("2-4", code.Lines);
    }

    [Fact]
    public async Task CodeEvidence_IsReadFromTheCommitNeverTheWorktree_AndEachFileOnce()
    {
        var (verifier, runtime) = Verifier();

        await verifier.VerifyAsync(new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Queue"), CancellationToken.None);
        await verifier.VerifyAsync(new(EvidenceKind.Code, Path: "src/App/Queue.cs", Symbol: "Claim"), CancellationToken.None);

        var read = Assert.Single(runtime.ExecutedCommands).Command;
        Assert.Equal("git", read.Name);
        Assert.Equal(["show", $"{Base}:src/App/Queue.cs"], read.Args);
    }
}
