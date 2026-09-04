using IronHive.Agent.Loop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Execution;

/// <summary>
/// Polls Host for the next Pending inspection request, checks the target repo out into
/// a fresh code-beaker session's workspace, runs the agent loop against it, and reports
/// the outcome back. The agent loop has a code-execution tool and a finding-reporting
/// tool — the agent decides what, if
/// anything, to report; an inspection that finds nothing submits zero findings rather than
/// fabricate one, per momos's 근거 기반 엄밀함 non-negotiable. Computer Use activation is
/// still a separate, undecided "반드시 논의" item (momos improvement protocol).
/// </summary>
public sealed class PullExecutionBackgroundService(
    IHostApiClient hostApiClient,
    ISessionAwareAgentLoopFactory agentLoopFactory,
    IExecutionRuntimeProvider executionRuntimeProvider,
    IWorkerSelfUpdater selfUpdater,
    IOptions<PullExecutionOptions> options,
    ILogger<PullExecutionBackgroundService> logger) : BackgroundService
{
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await hostApiClient.ClaimNextAsync(stoppingToken);
                var claimedWork = false;

                if (result.UpdateRequired)
                {
                    // Host no longer accepts this Worker's protocol version, so it withheld work
                    // (Request is always null here) — nothing to finish first, update right away.
                    await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                }
                else if (result.Request is null)
                {
                    if (result.RecommendedWorkerVersion is not null)
                    {
                        // No work and an update on offer: the least disruptive moment there is.
                        await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                    }
                }
                else
                {
                    await RunInspectionAsync(result.Request, stoppingToken);
                    claimedWork = true;

                    // Just-finished work is an idle boundary too, so act on the hint this poll
                    // already carried rather than waiting for a poll that finds the queue empty —
                    // a Worker whose queue never drains would otherwise never update at all.
                    if (result.RecommendedWorkerVersion is not null)
                    {
                        await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                    }
                }

                // Counts consecutive failed iterations, so it resets only once an iteration has run
                // to completion — resetting right after a successful claim would pin the count at 1
                // whenever the failing step is a later one, and the threshold below would never trip.
                _consecutiveFailures = 0;

                // Straight back to claiming after real work: an idle wait only makes sense when the
                // last poll found nothing to do.
                if (!claimedWork)
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A deliberate shutdown, not a failure — let the while loop's own check end it.
                // Checking the token (not just the exception type) matters: HttpClient.Timeout
                // also throws OperationCanceledException (a TaskCanceledException) when a call
                // hangs, and that one is a transient failure, not a shutdown signal, even though
                // it is indistinguishable from one by type alone.
            }
            catch (Exception ex)
            {
                // A BackgroundService that throws out of ExecuteAsync takes the whole
                // host down — a transient Host-unreachable blip must not do that.
                //
                // This is where a request stays stuck if RunInspectionAsync's own
                // catch (below) can't even report the failure — e.g. Host itself is
                // unreachable. That's not a gap: it left the request Running, and the
                // claim-next reclaim lease (InspectionClaimOptions.ReclaimTimeout) picks
                // it back up once the lease expires, whether that's this worker on its
                // next poll or another one. No separate retry/backoff for this case is
                // needed on top of that.
                //
                // The loop itself never stops retrying — giving up would sacrifice
                // availability for no benefit once Host recovers — but nothing capped
                // how long it kept logging at error level while Host stayed down. Past
                // ConsecutiveFailureLogThreshold, downgrade to a single critical log and
                // go quiet until the next success, so a prolonged outage doesn't drown
                // out other log signal without needing the poll loop to actually stop.
                //
                // A failing self-update lands here for the same reason and gets the same
                // treatment: it retries every poll interval, and a Worker Host has locked
                // out is doing nothing else, so without the threshold it would log an error
                // and hit the release API every few seconds indefinitely.
                _consecutiveFailures++;
                if (_consecutiveFailures < options.Value.ConsecutiveFailureLogThreshold)
                {
                    logger.LogError(ex, "Pull loop iteration failed, will retry next poll");
                }
                else if (_consecutiveFailures == options.Value.ConsecutiveFailureLogThreshold)
                {
                    logger.LogCritical(
                        ex,
                        "Pull loop has failed {ConsecutiveFailures} consecutive times — suppressing further per-iteration error logs until it succeeds again",
                        _consecutiveFailures);
                }

                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
        }
    }

    private async Task RunInspectionAsync(ClaimedInspectionRequest request, CancellationToken cancellationToken)
    {
        ExecutionSessionHandle? session = null;
        // Declared outside the try so a failure can still report what was attempted before it —
        // assigned as soon as the agent loop factory returns it, so it stays null only for a
        // failure that happened before the sink even existed (e.g. chat-client setup).
        ToolCallTraceSink? toolCalls = null;
        try
        {
            var project = await hostApiClient.GetProjectAsync(request.ProjectId, cancellationToken);
            logger.LogInformation(
                "Starting inspection for request {RequestId}, project {ProjectName} ({ProjectId}), commitRef={CommitRef}",
                request.Id, project.Name, request.ProjectId, request.CommitRef ?? "(default branch)");

            // Opened here, not by the agent-loop factory (ISessionAwareAgentLoopFactory)
            // — the repo has to be checked out into the session's workspace before the
            // agent's first turn, so the session must exist first (one session per
            // inspection request).
            //
            // "dotnet" is a stand-in for real repo language detection, which doesn't
            // exist yet — it names the only language the current pilots target. A string
            // an isolated runtime doesn't recognize would fail session creation outright,
            // so this can't stay a generic placeholder the way an always-available
            // native-only runtime could afford.
            session = await executionRuntimeProvider.CreateSessionAsync(new ExecutionSessionRequest("dotnet"), cancellationToken);

            if (!string.IsNullOrEmpty(project.RepositoryUrl))
            {
                // "--" pins the following token as a positional argument so a
                // RepositoryUrl value that happens to start with "-" (e.g.
                // "--upload-pack=...") cannot be smuggled in as a git flag.
                var clone = await executionRuntimeProvider.ExecuteAsync(
                    session, new ExecutionCommand("git", ["clone", "--", project.RepositoryUrl, "."]), cancellationToken);
                if (!clone.Success)
                {
                    throw new InvalidOperationException($"Failed to check out {project.RepositoryUrl}: {clone.Error}");
                }

                if (!string.IsNullOrEmpty(request.CommitRef))
                {
                    // "--detach" (not "--") pins the following token as the ref to switch HEAD
                    // to — checkout's own "--" instead means "the rest are pathspecs", which
                    // would restore a path named after the ref rather than move HEAD.
                    // Reject a leading "-" up front so a CommitRef value cannot be smuggled in
                    // as a git flag (same defensive posture as the clone call above).
                    if (request.CommitRef.StartsWith('-'))
                    {
                        throw new InvalidOperationException($"Invalid commit ref: {request.CommitRef}");
                    }

                    var checkout = await executionRuntimeProvider.ExecuteAsync(
                        session, new ExecutionCommand("git", ["checkout", "--detach", request.CommitRef]), cancellationToken);
                    if (!checkout.Success)
                    {
                        throw new InvalidOperationException($"Failed to check out commit {request.CommitRef}: {checkout.Error}");
                    }
                }
            }
            // No RepositoryUrl declared — an honest "nothing to check out" case, not a
            // failure: momos never assumes a repo it wasn't told about.

            var (agentLoop, findings, toolCallSink) = await agentLoopFactory.CreateAsync(
                new AgentLoopFactoryOptions(), session, projectId: request.ProjectId, cancellationToken);
            toolCalls = toolCallSink;
            await agentLoop.RunAsync(BuildPrompt(project, request.Focus), cancellationToken);

            logger.LogInformation(
                "Inspection for request {RequestId} completed with {FindingCount} finding(s)",
                request.Id, findings.Findings.Count);
            var toolCallPayloads = toolCalls.Calls
                .Select(c => new ToolCallPayload(c.Tool, c.Summary, c.Success, c.DurationMs))
                .ToList();
            await hostApiClient.SubmitReportAsync(request.Id, findings.Findings, toolCallPayloads, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Covers a mid-run Host hiccup (GetProjectAsync/SubmitReportAsync) and an
            // LLM-provider failure alike — both surface here as "the run threw", and
            // both are handled the same way: report it and move on. If SubmitFailureAsync
            // itself also throws (Host is the one that's down), the exception propagates
            // to ExecuteAsync's catch and the request is left Running — the claim-next
            // reclaim lease (InspectionClaimOptions.ReclaimTimeout) picks it back up once
            // it expires, so no separate retry/backoff is needed for that case either.
            // The trace itself isn't sent to Host yet (see
            // claudedocs/issues/ISSUE-momos-20260903-failed-inspection-trace-discarded.md —
            // that needs a schema decision), but logging what was attempted before the
            // failure is a pure Worker-local improvement and doesn't need to wait on it.
            if (toolCalls is { Calls.Count: > 0 })
            {
                logger.LogError(
                    ex,
                    "Inspection run failed for request {RequestId} after {ToolCallCount} tool call(s), last: {LastTool} ({LastToolSuccess})",
                    request.Id, toolCalls.Calls.Count, toolCalls.Calls[^1].Tool, toolCalls.Calls[^1].Success ? "succeeded" : "failed");
            }
            else
            {
                logger.LogError(ex, "Inspection run failed for request {RequestId}", request.Id);
            }
            await hostApiClient.SubmitFailureAsync(request.Id, ex.Message, cancellationToken);
        }
        finally
        {
            // CancellationToken.None, not `cancellationToken`: this runs during shutdown
            // cancellation too, and the token that triggered the cleanup must not also
            // be able to abort it and leak the session.
            if (session is not null)
            {
                await executionRuntimeProvider.CloseSessionAsync(session, CancellationToken.None);
            }
        }
    }

    private static string BuildPrompt(ProjectInfo project, string? focus) =>
        $"""
        You are inspecting a project.
        Purpose: {project.Purpose}
        Vision: {project.Vision}
        Scope: {project.Scope}
        {(focus is null ? string.Empty : $"Focus for this run: {focus}")}

        Use RunCommand to explore and exercise the checked-out project (build it, run its
        tests, start it, poke at its behavior — whatever fits its Purpose and Scope). When
        you actually reproduce a functional defect or UX inconsistency, call ReportFinding
        with the command output that shows it. If nothing turns up, say so and finish
        without calling ReportFinding — do not report a finding you did not reproduce.

        Use QueryProjectKnowledge if you want context from this project's registered
        documents or past inspection findings before deciding what to check.
        """;
}
