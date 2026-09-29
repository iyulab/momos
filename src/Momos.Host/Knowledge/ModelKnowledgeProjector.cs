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

    /// <summary>The stable index document id for one claim of one project.</summary>
    public static string DocumentId(Guid projectId, string claimKey) => $"model-claim:{projectId:N}:{claimKey}";

    public static string Render(ModelClaim claim)
    {
        var text = new StringBuilder()
            .Append("Project model claim ").Append(claim.Key)
            .Append(" [").Append(claim.Tier).Append(", ").Append(claim.Confidence).Append(" confidence, ").Append(claim.Status)
            .Append(", ").Append(claim.Origin == ClaimOrigin.Synthesized ? "synthesized by a language model" : "extracted deterministically").Append("]: ")
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
    /// <paramref name="staleClaimKeys"/>. True only when every document operation succeeded —
    /// the caller records the model as indexed on true and retries on false.</summary>
    public async Task<bool> ProjectModelAsync(ProjectModel model, IReadOnlyCollection<string> staleClaimKeys, CancellationToken cancellationToken)
    {
        var ok = true;
        foreach (var claim in model.Claims)
        {
            ok &= await ProjectClaimAsync(model.ProjectId, claim, cancellationToken);
        }

        foreach (var key in staleClaimKeys)
        {
            ok &= await TryAsync(() => index.DeleteAsync(DocumentId(model.ProjectId, key), cancellationToken), key);
        }

        return ok;
    }

    private Task<bool> ProjectClaimAsync(Guid projectId, ModelClaim claim, CancellationToken cancellationToken) =>
        TryAsync(async () =>
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

    // One claim the index refuses must not stop the others from being projected: the failure is
    // logged and counted, and the caller keeps the model unindexed so the whole model is retried.
    private async Task<bool> TryAsync(Func<Task> action, string claimKey)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to project model claim {ClaimKey} into the project knowledge base.", claimKey);
            return false;
        }
    }
}
