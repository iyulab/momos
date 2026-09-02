using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Execution;

/// <summary>
/// Exposes Host's project-knowledge query API as a native in-process agent tool — same
/// wrapping pattern as <see cref="CodeExecutionTools"/> and <see cref="FindingReportingTools"/>.
/// Unlike those two, this one is genuinely optional per-run (a project with no registered
/// documents or prior findings has nothing for it to return), but it is still always offered
/// to the agent — see <see cref="Agent.MomosAgentLoopFactory"/>'s <c>AlwaysInclude</c> list —
/// rather than left to keyword-based tool retrieval to surface.
/// </summary>
public sealed class KnowledgeQueryTools(
    IHostApiClient hostApiClient,
    Guid projectId,
    ILogger<KnowledgeQueryTools> logger)
{
    [Description("Search this project's registered documents and past inspection findings for context relevant to a query. Returns matching snippets, or nothing if the project has no relevant knowledge yet.")]
    public async Task<string> QueryProjectKnowledge(
        [Description("What to search for, e.g. \"login flow\" or \"payment errors\".")] string query,
        CancellationToken cancellationToken = default)
    {
        var snippets = await hostApiClient.QueryKnowledgeAsync(projectId, query, cancellationToken);
        logger.LogInformation("QueryProjectKnowledge({Query}) returned {Count} snippet(s)", query, snippets.Count);
        return snippets.Count == 0
            ? "No relevant project knowledge found."
            : string.Join("\n---\n", snippets);
    }
}
