using IronHive.Agent.Loop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Momos.Worker.Execution;

/// <summary>
/// Polls Host for the next Pending inspection request (ADR-0008 pull protocol), runs
/// the agent loop against it, and reports the outcome back. No execution tool is wired
/// into the agent yet — Computer Use activation is a separate, undecided "반드시 논의"
/// item (momos improvement protocol) and static-tier tooling is B-12 — so a run that
/// reaches the LLM has nothing concrete to inspect. It reports zero findings rather
/// than fabricate one without evidence, per momos's 근거 기반 엄밀함 non-negotiable.
/// </summary>
public sealed class PullExecutionBackgroundService(
    IHostApiClient hostApiClient,
    IAgentLoopFactory agentLoopFactory,
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
        try
        {
            var project = await hostApiClient.GetProjectAsync(request.ProjectId, cancellationToken);
            var agentLoop = await agentLoopFactory.CreateAsync(cancellationToken);
            await agentLoop.RunAsync(BuildPrompt(project, request.Focus), cancellationToken);

            await hostApiClient.SubmitReportAsync(request.Id, [], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Inspection run failed for request {RequestId}", request.Id);
            await hostApiClient.SubmitFailureAsync(request.Id, ex.Message, cancellationToken);
        }
    }

    private static string BuildPrompt(ProjectInfo project, string? focus) =>
        $"""
        You are inspecting a project.
        Purpose: {project.Purpose}
        Vision: {project.Vision}
        Scope: {project.Scope}
        {(focus is null ? string.Empty : $"Focus for this run: {focus}")}
        """;
}
