using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Analysis;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;
using Momos.Worker.Tests.Analysis.Synthesis;

namespace Momos.Worker.Tests.Analysis;

public sealed class ProjectAnalyzerTests
{
    private static readonly ProjectInfo Project = new(Guid.NewGuid(), "acme", null, null, "p", "v", "s");

    private sealed class Extractor : IProjectModelExtractor
    {
        public Task<ProjectModelPayload> ExtractAsync(ExecutionSessionHandle session, CancellationToken cancellationToken) =>
            Task.FromResult(SynthesisDraftTests.Skeleton());
    }

    private sealed class Synthesizer(Func<ProjectModelPayload, ProjectModelPayload> run) : IManualSynthesizer
    {
        public int Calls { get; private set; }

        public Task<ProjectModelPayload> SynthesizeAsync(ProjectModelPayload skeleton, ExecutionSessionHandle session, ProjectInfo project, string? language, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(run(skeleton));
        }
    }

    private static ProjectAnalyzer Analyzer(IManualSynthesizer synthesizer, bool synthesis = true) =>
        new(new Extractor(), synthesizer, Options.Create(new AnalysisOptions { Synthesis = synthesis }), NullLogger<ProjectAnalyzer>.Instance);

    [Fact]
    public async Task WithSynthesisOff_TheDeterministicModelIsSubmittedUnchanged()
    {
        var synthesizer = new Synthesizer(_ => throw new InvalidOperationException("not called"));

        var model = await Analyzer(synthesizer, synthesis: false).AnalyzeAsync(new ExecutionSessionHandle("s"), Project, null, CancellationToken.None);

        Assert.Equal(0, synthesizer.Calls);
        Assert.Equal(SynthesisDraftTests.Skeleton().Claims.Select(c => c.Key), model.Claims.Select(c => c.Key));
        Assert.DoesNotContain(model.Coverage.NotAnalyzed, g => g.Area == "manual-synthesis" && g.Reason.Contains("did not run"));
    }

    [Fact]
    public async Task WithSynthesisOff_ThePartialityIsRecordedInCoverage()
    {
        var model = await Analyzer(new Synthesizer(_ => throw new InvalidOperationException("not called")), synthesis: false)
            .AnalyzeAsync(new ExecutionSessionHandle("s"), Project, null, CancellationToken.None);

        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-synthesis");
        Assert.Contains("turned off by configuration", gap.Reason);
    }

    [Fact]
    public async Task AnUnsupportedLanguage_FailsBeforeAnyWork()
    {
        var synthesizer = new Synthesizer(_ => throw new InvalidOperationException("not called"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Analyzer(synthesizer).AnalyzeAsync(new ExecutionSessionHandle("s"), Project, "fr", CancellationToken.None));

        Assert.Equal("This Worker cannot write in 'fr'.", ex.Message);
        Assert.Equal(0, synthesizer.Calls);
    }

    [Fact]
    public async Task WhenSynthesisThrows_TheSkeletonIsSubmittedWithTheReason()
    {
        var model = await Analyzer(new Synthesizer(_ => throw new InvalidOperationException("broken")))
            .AnalyzeAsync(new ExecutionSessionHandle("s"), Project, null, CancellationToken.None);

        Assert.Equal(SynthesisDraftTests.Skeleton().Claims.Select(c => c.Key), model.Claims.Select(c => c.Key));
        var gap = Assert.Single(model.Coverage.NotAnalyzed, g => g.Area == "manual-synthesis");
        Assert.Contains("InvalidOperationException", gap.Reason);
    }

    [Fact]
    public async Task AShutdownDuringSynthesis_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Analyzer(new Synthesizer(_ => throw new OperationCanceledException(cts.Token))).AnalyzeAsync(new ExecutionSessionHandle("s"), Project, null, cts.Token));
    }
}
