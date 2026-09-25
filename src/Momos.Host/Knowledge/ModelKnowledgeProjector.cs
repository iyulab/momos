using System.Security.Cryptography;
using System.Text;
using Momos.Host.Domain;

namespace Momos.Host.Knowledge;

/// <summary>
/// Projects project-model claims into the knowledge index, so the inspection agent's existing
/// knowledge query reads the model — corrections included — without a tool of its own. The
/// index is derived: the database stays the source of truth and every document id is stable per
/// (project, claim key), so re-projecting a claim replaces its document instead of adding one.
/// </summary>
public sealed class ModelKnowledgeProjector(IKnowledgeIndex index, ILogger<ModelKnowledgeProjector> logger)
{
    public const string SourceType = "model-claim";

    /// <summary>The stable index document id for one claim of one project. Claim keys have no
    /// length bound, so (project, key) is hashed to a fixed-length id; the readable identity is
    /// kept in the document metadata (<c>ProjectId</c>, <c>ClaimKey</c>).</summary>
    // TODO(upstream): FluxIndex.Storage.PostgreSQL 0.28.6 stores document ids in varchar(50) with
    // no validation at the API; ids are hashed to a fixed length until the column is widened.
    public static string DocumentId(Guid projectId, string claimKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId:N}:{claimKey}"));
        return $"model-claim:{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    public static string Render(ModelClaim claim)
    {
        var text = new StringBuilder()
            .Append("Project model claim ").Append(claim.Key)
            .Append(" [").Append(claim.Tier).Append(", ").Append(claim.Confidence).Append(" confidence, ").Append(claim.Status).Append("]: ")
            .AppendLine(claim.Statement)
            .Append("Evidence: ")
            .AppendLine(string.Join("; ", claim.Evidence.Select(DescribeEvidence)));
        if (claim.Correction is not null)
        {
            // A developer's correction states the intended design; the inspection agent should
            // judge behavior against it rather than against the extracted statement above.
            text.Append("Developer correction (authoritative): ").AppendLine(claim.Correction);
        }

        return text.ToString();
    }

    /// <summary>Indexes every claim of <paramref name="model"/> and removes the documents of
    /// claims that only <paramref name="previous"/> had, so the agent never reads structure the
    /// latest analysis no longer finds.</summary>
    public async Task ProjectModelAsync(ProjectModel model, ProjectModel? previous, CancellationToken cancellationToken)
    {
        foreach (var claim in model.Claims)
        {
            await ProjectClaimAsync(model.ProjectId, claim, cancellationToken);
        }

        var currentKeys = model.Claims.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in previous?.Claims.Where(c => !currentKeys.Contains(c.Key)) ?? [])
        {
            await BestEffortAsync(() => index.DeleteAsync(DocumentId(model.ProjectId, gone.Key), cancellationToken), gone.Key);
        }
    }

    public Task ProjectClaimAsync(Guid projectId, ModelClaim claim, CancellationToken cancellationToken) =>
        BestEffortAsync(async () =>
        {
            // Delete first: indexing under an existing document id would otherwise leave the old
            // chunks beside the new ones, and the agent would read both versions of the claim.
            var documentId = DocumentId(projectId, claim.Key);
            await index.DeleteAsync(documentId, cancellationToken);
            await index.IndexAsync(Render(claim), documentId, new Dictionary<string, object>
            {
                ["ProjectId"] = projectId.ToString(),
                ["SourceType"] = SourceType,
                ["ClaimKey"] = claim.Key,
            }, cancellationToken);
        }, claim.Key);

    private static string DescribeEvidence(ClaimEvidence e) => e.Kind switch
    {
        EvidenceKind.Code => $"code {e.Path}{(e.Symbol is null ? "" : $" ({e.Symbol})")}{(e.Lines is null ? "" : $" lines {e.Lines}")}",
        EvidenceKind.Commit => $"commit {e.Sha}",
        EvidenceKind.PullRequest or EvidenceKind.Issue => $"{e.Kind} {e.Url}",
        EvidenceKind.Finding => $"reproduced finding from inspection {e.InspectionRequestId}",
        EvidenceKind.Claim => $"interprets claim {e.ClaimKey}",
        _ => e.Kind.ToString(),
    };

    // Same posture as finding indexing: the model is already committed, so an index failure is
    // logged, not surfaced — the database stays correct and the next projection repairs the index.
    private async Task BestEffortAsync(Func<Task> action, string claimKey)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to project model claim {ClaimKey} into the project knowledge base.", claimKey);
        }
    }
}
