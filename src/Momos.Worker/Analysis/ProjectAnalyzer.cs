using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Momos.Worker.Analysis.Synthesis;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis;

/// <summary>
/// The deterministic extraction, then — unless switched off — the language-model passes over it.
/// A failure in the passes never costs the deterministic model: it is submitted on its own, with
/// the reason recorded in its coverage. A failed extraction still fails the request.
/// </summary>
public sealed class ProjectAnalyzer(
    IProjectModelExtractor extractor, IManualSynthesizer synthesizer, IOptions<AnalysisOptions> options, ILogger<ProjectAnalyzer> logger) : IProjectAnalyzer
{
    public async Task<ProjectModelPayload> AnalyzeAsync(ExecutionSessionHandle session, ProjectInfo project, string? language, CancellationToken cancellationToken)
    {
        if (language is not null && !AnalysisPrompts.LanguageNames.ContainsKey(language))
        {
            throw new InvalidOperationException($"This Worker cannot write in '{language}'.");
        }

        var skeleton = await extractor.ExtractAsync(session, cancellationToken);
        if (!options.Value.Synthesis)
        {
            var off = new CoverageGapPayload("manual-synthesis", "Language-model analysis is turned off by configuration; this model holds deterministic facts only.");
            return skeleton with { Coverage = skeleton.Coverage with { NotAnalyzed = [.. skeleton.Coverage.NotAnalyzed, off] } };
        }

        try
        {
            return await synthesizer.SynthesizeAsync(skeleton, session, project, language, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Language-model analysis failed; submitting the deterministic model only");
            var gap = new CoverageGapPayload("manual-synthesis", $"Language-model analysis did not run ({ex.GetType().Name}); this model holds deterministic facts only.");
            return skeleton with { Coverage = skeleton.Coverage with { NotAnalyzed = [.. skeleton.Coverage.NotAnalyzed, gap] } };
        }
    }
}
