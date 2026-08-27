using System.Net;
using System.Net.Http.Json;

namespace Momos.Worker.Execution;

public sealed class HostApiClient(HttpClient httpClient) : IHostApiClient
{
    public async Task<ClaimedInspectionRequest?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsync("/inspection-requests/claim-next", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimedInspectionRequest>(cancellationToken);
    }

    public async Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await httpClient.GetFromJsonAsync<ProjectInfo>($"/projects/{projectId}", cancellationToken);
        return project ?? throw new InvalidOperationException($"Host returned an empty body for project {projectId}.");
    }

    public async Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/report", new SubmitReportRequest(findings), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitFailureAsync(Guid inspectionRequestId, string reason, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/fail", new SubmitFailureRequest(reason), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
