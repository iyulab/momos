using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momos.Worker.Execution;

public sealed class HostApiClient(HttpClient httpClient) : IHostApiClient
{
    // Mirrors the Host's ConfigureHttpJsonOptions (Momos.Host/Program.cs) — separate
    // deployable, so this converter is a local copy rather than a shared reference
    // (same reasoning as the duplicated enums in HostApiContracts.cs).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ClaimNextResult> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/inspection-requests/claim-next",
            new ClaimNextRequest(WorkerVersionInfo.ProtocolVersion, WorkerVersionInfo.Version),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimNextResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Host returned an empty body for claim-next.");
    }

    public async Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await httpClient.GetFromJsonAsync<ProjectInfo>($"/projects/{projectId}", JsonOptions, cancellationToken);
        return project ?? throw new InvalidOperationException($"Host returned an empty body for project {projectId}.");
    }

    public async Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, IReadOnlyList<ToolCallPayload> toolCalls, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/report", new SubmitReportRequest(findings, toolCalls), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitFailureAsync(Guid inspectionRequestId, string reason, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/fail", new SubmitFailureRequest(reason), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> QueryKnowledgeAsync(Guid projectId, string query, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/projects/{projectId}/knowledge/query", new QueryKnowledgeRequest(query), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QueryKnowledgeResponse>(JsonOptions, cancellationToken);
        return result?.Snippets.Select(s => s.Content).ToList() ?? [];
    }
}
