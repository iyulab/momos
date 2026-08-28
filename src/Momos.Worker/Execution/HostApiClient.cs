using System.Net;
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

    public async Task<ClaimedInspectionRequest?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsync("/inspection-requests/claim-next", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimedInspectionRequest>(JsonOptions, cancellationToken);
    }

    public async Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await httpClient.GetFromJsonAsync<ProjectInfo>($"/projects/{projectId}", JsonOptions, cancellationToken);
        return project ?? throw new InvalidOperationException($"Host returned an empty body for project {projectId}.");
    }

    public async Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/report", new SubmitReportRequest(findings), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SubmitFailureAsync(Guid inspectionRequestId, string reason, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/inspection-requests/{inspectionRequestId}/fail", new SubmitFailureRequest(reason), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
