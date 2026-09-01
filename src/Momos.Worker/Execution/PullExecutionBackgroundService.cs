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
                _consecutiveFailures = 0;

                if (result.UpdateRequired)
                {
                    // Host가 이 protocol version을 더 이상 안 받아준다 — 일감이 없으니(Request는
                    // 항상 null) 곧장 self-update. 실패해도 프로세스는 안 죽는다(D-51 패턴, 아래
                    // catch에서 그대로 처리), 다음 poll에서 다시 시도.
                    await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                    continue;
                }

                if (result.Request is null)
                {
                    if (result.RecommendedWorkerVersion is not null)
                    {
                        // 일감도 없고 마침 업데이트 힌트도 있다 — 지금이 가장 확실한 유휴
                        // 경계이므로 여기서 처리한다.
                        await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                    }

                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                    continue;
                }

                await RunInspectionAsync(result.Request, stoppingToken);

                // 방금 일감을 끝냈다 — 이 poll 응답에 실려온 힌트를 다음 poll을 기다리지 않고
                // 바로 반영한다(작업 완료 직후가 GitHub Actions runner의 job-경계 트리거와
                // 동일한 유휴 지점).
                if (result.RecommendedWorkerVersion is not null)
                {
                    await selfUpdater.UpdateAsync(result.RecommendedWorkerVersion, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            var (agentLoop, findings) = await agentLoopFactory.CreateAsync(new AgentLoopFactoryOptions(), session, cancellationToken);
            await agentLoop.RunAsync(BuildPrompt(project, request.Focus), cancellationToken);

            logger.LogInformation(
                "Inspection for request {RequestId} completed with {FindingCount} finding(s)",
                request.Id, findings.Findings.Count);
            await hostApiClient.SubmitReportAsync(request.Id, findings.Findings, cancellationToken);
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
            logger.LogError(ex, "Inspection run failed for request {RequestId}", request.Id);
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
        """;
}
