using IronHive.Agent.Loop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Execution;

/// <summary>
/// Polls Host for the next Pending inspection request (ADR-0008 pull protocol), checks
/// the target repo out into a fresh code-beaker session's workspace, runs the agent loop
/// against it, and reports the outcome back. The agent loop has a code-execution tool and
/// a finding-reporting tool (ADR-0009 decision 3 = B) — the agent decides what, if
/// anything, to report; an inspection that finds nothing submits zero findings rather than
/// fabricate one, per momos's 근거 기반 엄밀함 non-negotiable. Computer Use activation is
/// still a separate, undecided "반드시 논의" item (momos improvement protocol).
/// </summary>
public sealed class PullExecutionBackgroundService(
    IHostApiClient hostApiClient,
    ISessionAwareAgentLoopFactory agentLoopFactory,
    IExecutionRuntimeProvider executionRuntimeProvider,
    IOptions<PullExecutionOptions> options,
    ILogger<PullExecutionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await hostApiClient.ClaimNextAsync(stoppingToken);
                if (claimed is null)
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                    continue;
                }

                await RunInspectionAsync(claimed, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A BackgroundService that throws out of ExecuteAsync takes the whole
                // host down — a transient Host-unreachable blip must not do that. Retry
                // policy/backoff is explicitly out of scope (BD-20260826-05); this only
                // stops one bad poll from killing the process, at the existing cadence.
                logger.LogError(ex, "Pull loop iteration failed, will retry next poll");
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

            // Opened here, not by the agent-loop factory (ISessionAwareAgentLoopFactory)
            // — the repo has to be checked out into the session's workspace before the
            // agent's first turn, so the session must exist first (ADR-0009 decision 2:
            // one session per inspection request).
            session = await executionRuntimeProvider.CreateSessionAsync(new ExecutionSessionRequest("native"), cancellationToken);

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
            }
            // No RepositoryUrl declared — an honest "nothing to check out" case, not a
            // failure (D-25 non-invasiveness: momos never assumes a repo it wasn't told about).

            var (agentLoop, findings) = await agentLoopFactory.CreateAsync(new AgentLoopFactoryOptions(), session, cancellationToken);
            await agentLoop.RunAsync(BuildPrompt(project, request.Focus), cancellationToken);

            await hostApiClient.SubmitReportAsync(request.Id, findings.Findings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
