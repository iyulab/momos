using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

public interface IManualSynthesizer
{
    Task<ProjectModelPayload> SynthesizeAsync(ProjectModelPayload skeleton, ExecutionSessionHandle session, ProjectInfo project, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the language-model passes of an analysis over the deterministic skeleton: one overview
/// pass that plans the chapters, then one pass per chapter. A pass that runs out of tokens or
/// time, or whose model call fails, loses only its own additions — the analysis still completes
/// with what the other passes wrote, and the coverage says what was lost and why. Only a shutdown
/// of the Worker itself ends the analysis early.
/// </summary>
public sealed class ManualSynthesizer(
    IAnalysisAgentLoopFactory loops,
    IExecutionRuntimeProvider runtime,
    IOptions<AnalysisOptions> options,
    IOptions<GpuStackLlmOptions> llm,
    ILogger<ManualSynthesizer> logger) : IManualSynthesizer
{
    private const string TimeBudgetReached = "time budget reached";
    private const string TokenBudgetReached = "token budget reached";

    public async Task<ProjectModelPayload> SynthesizeAsync(
        ProjectModelPayload skeleton, ExecutionSessionHandle session, ProjectInfo project, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var model = string.IsNullOrWhiteSpace(o.Model) ? null : o.Model;
        var generator = new CoverageGeneratorPayload(model ?? llm.Value.Model, AnalysisPrompts.Version);
        var draft = new SynthesisDraft(skeleton);
        var verifier = new EvidenceVerifier(runtime, session, skeleton.BaseCommit);
        var total = new TokenBudget(o.MaxTotalTokens);
        var gaps = new List<CoverageGapPayload>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(o.MaxDuration);

        var overview = draft.Stage(section: null);
        var failure = await RunPassAsync(new SynthesisTools(overview, verifier, o.MaxChapters, logger).ForOverview(),
            AnalysisPrompts.Overview(project, skeleton, o.MaxChapters));
        if (failure is not null)
        {
            gaps.Add(new CoverageGapPayload("manual-outline", $"The overview pass did not finish ({failure}); no chapters were written."));
            return ModelAssembler.Assemble(draft, gaps, generator, chaptersPlanned: 0);
        }

        overview.Commit();
        foreach (var section in draft.Sections.ToList())
        {
            var stage = draft.Stage(section);
            failure = await RunPassAsync(new SynthesisTools(stage, verifier, o.MaxChapters, logger).ForChapter(), AnalysisPrompts.Chapter(section, draft));
            if (failure is null)
            {
                stage.Commit();
            }
            else
            {
                gaps.Add(new CoverageGapPayload("manual-chapter", $"{section.Path}: {failure}"));
            }
        }

        var assembled = ModelAssembler.Assemble(draft, gaps, generator, draft.Sections.Count);
        logger.LogInformation(
            "Analysis passes planned {Planned} chapter(s) and added {Claims} claim(s); {Rejected} proposal(s) rejected, {Tokens} token(s) used",
            draft.Sections.Count, draft.Claims.Count, draft.Rejections.Values.Sum(), total.Used);
        return assembled;

        // Null when the pass finished; otherwise why it did not. Lets only a shutdown through.
        async Task<string?> RunPassAsync(IReadOnlyList<AIFunction> passTools, string prompt)
        {
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return TimeBudgetReached;
            }

            if (total.IsExhausted)
            {
                return TokenBudgetReached;
            }

            try
            {
                using (TokenBudget.Enter(new TokenBudget(o.MaxChapterTokens, total)))
                {
                    var loop = await loops.CreateAnalysisLoopAsync(session, project.Id, passTools, model, AnalysisPrompts.System,
                        new AnalysisContextOptions(o.MaxCommandOutputChars, o.MaxContextTokens, o.ProtectedToolRounds), deadline.Token);
                    await loop.RunAsync(prompt, deadline.Token);
                }

                return null;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return TimeBudgetReached;
            }
            catch (UsageLimitExceededException)
            {
                return TokenBudgetReached;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A provider failure or an unexpected answer costs this pass, not the analysis. An
                // HTTP client's own timeout lands here too — it is not this analysis's deadline.
                logger.LogWarning(ex, "An analysis pass failed");
                return $"the model call failed ({ex.GetType().Name})";
            }
        }
    }
}
