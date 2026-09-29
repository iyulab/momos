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
/// pass that plans the chapters, then one pass per chapter, each chapter getting an equal share of
/// the time still left. A pass that runs out of tokens or time keeps what it already got verified:
/// an overview keeps the chapters it planned, a chapter becomes a partial chapter. A pass whose
/// model call fails loses its additions — for the overview, the whole plan. Either way the analysis
/// still completes with what the passes wrote, and the coverage says what was partial or lost and
/// why. Only a shutdown of the Worker itself ends the analysis early. Every pass logs when it starts
/// and how it ended — time, tokens, tool calls by tool, rejections — so a pass that proposed
/// nothing can be told apart from one whose proposals failed.
/// </summary>
public sealed class ManualSynthesizer(
    IAnalysisAgentLoopFactory loops,
    IExecutionRuntimeProvider runtime,
    IOptions<AnalysisOptions> options,
    IOptions<GpuStackLlmOptions> llm,
    TimeProvider time,
    ILogger<ManualSynthesizer> logger) : IManualSynthesizer
{
    private const string TimeBudgetReached = "time budget reached";
    private const string TokenBudgetReached = "token budget reached";

    public async Task<ProjectModelPayload> SynthesizeAsync(
        ProjectModelPayload skeleton, ExecutionSessionHandle session, ProjectInfo project, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
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
        var failure = await RunPassAsync("overview", new SynthesisTools(overview, verifier, o.MaxChapters, logger).ForOverview(),
            AnalysisPrompts.Overview(project, skeleton, o.MaxChapters), o.MaxOverviewTokens);
        // Only planned chapters make a plan worth keeping; a claim the overview verified on its
        // own has no chapter to live in.
        if (failure is null || KeepsPartial(failure, overview.SectionCount > 0))
        {
            overview.Commit();
            if (failure is not null)
            {
                gaps.Add(new CoverageGapPayload("manual-outline", $"partial — {failure}"));
            }

            await WriteChaptersAsync();
        }
        else
        {
            gaps.Add(new CoverageGapPayload("manual-outline", $"The overview pass did not finish ({failure}); no chapters were written."));
        }

        var assembled = ModelAssembler.Assemble(draft, gaps, generator, draft.Sections.Count);
        logger.LogInformation(
            "Analysis passes planned {Planned} chapter(s) and added {Claims} claim(s); {Rejected} proposal(s) rejected, {Tokens} token(s) used",
            draft.Sections.Count, draft.Claims.Count, draft.Rejections.Values.Sum(), total.Used);
        return assembled;

        async Task WriteChaptersAsync()
        {
            var sections = draft.Sections.ToList();
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var stage = draft.Stage(section);
                // A chapter may use its share of what is left, so an early chapter that never
                // settles cannot starve the ones after it.
                var remaining = o.MaxDuration - (time.GetUtcNow() - started);
                var share = remaining / (sections.Count - i);
                var chapterFailure = await RunPassAsync(section.Path, new SynthesisTools(stage, verifier, o.MaxChapters, logger).ForChapter(),
                    AnalysisPrompts.Chapter(section, draft, share, o.MaxChapterTokens), o.MaxChapterTokens, share);
                if (chapterFailure is null)
                {
                    stage.Commit();
                }
                else if (KeepsPartial(chapterFailure, stage.HasContent))
                {
                    stage.Commit();
                    gaps.Add(new CoverageGapPayload("manual-chapter", $"{section.Path}: partial — {chapterFailure}"));
                }
                else
                {
                    gaps.Add(new CoverageGapPayload("manual-chapter", $"{section.Path}: {chapterFailure}"));
                }
            }
        }

        // Null when the pass finished; otherwise why it did not. Lets only a shutdown through.
        // A slice bounds the pass further, inside the analysis's own deadline.
        async Task<string?> RunPassAsync(string pass, IReadOnlyList<AIFunction> passTools, string prompt, long tokens, TimeSpan? slice = null)
        {
            var passStarted = time.GetUtcNow();
            var budget = new TokenBudget(tokens, total);
            var monitor = new AnalysisToolMonitor(o.ReadsBeforeNudge, logger);
            var rejectedBefore = draft.Rejections.Values.Sum();
            logger.LogInformation("Analysis pass {Pass} started — {Slice} and {Tokens} token(s) at most",
                pass, slice is { } sl ? $"{sl.TotalSeconds:0}s" : "the remaining time", tokens);
            var outcome = await RunAsync();
            logger.LogInformation(
                "Analysis pass {Pass} {Outcome} after {Seconds:0}s — {Tokens} token(s); tool calls: {Calls}; {Rejected} proposal(s) rejected",
                pass, outcome ?? "finished", (time.GetUtcNow() - passStarted).TotalSeconds, budget.Used, monitor.Summary(),
                draft.Rejections.Values.Sum() - rejectedBefore);
            return outcome;

            async Task<string?> RunAsync()
            {
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return TimeBudgetReached;
                }

                // The deadline's timer may not have fired yet although no time is left.
                if (slice <= TimeSpan.Zero)
                {
                    return TimeBudgetReached;
                }

                if (total.IsExhausted)
                {
                    return TokenBudgetReached;
                }

                using var passDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                if (slice is { } s)
                {
                    passDeadline.CancelAfter(s);
                }

                try
                {
                    using (TokenBudget.Enter(budget))
                    {
                        var loop = await loops.CreateAnalysisLoopAsync(session, project.Id, passTools, model, AnalysisPrompts.System,
                            new AnalysisContextOptions(o.MaxCommandOutputChars, o.MaxContextTokens, o.ProtectedToolRounds), monitor, passDeadline.Token);
                        await loop.RunAsync(prompt, passDeadline.Token);
                    }

                    return null;
                }
                catch (OperationCanceledException) when (passDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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

    /// <summary>
    /// Whether a pass that did not finish still commits its stage. Every proposal in a stage already
    /// passed the verifier, so a budget running out says nothing against them; a failed model call
    /// does, and drops the stage whole.
    /// </summary>
    private static bool KeepsPartial(string failure, bool hasWork) =>
        failure is (TokenBudgetReached or TimeBudgetReached) && hasWork;
}
