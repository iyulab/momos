using System.Net.Http.Json;
using Momos.Host.Contracts;

namespace Momos.Host.Tests.Endpoints;

public sealed class ClaimNextCompatibilityTests : IClassFixture<MomosHostFactory>
{
    private readonly MomosHostFactory _factory;

    public ClaimNextCompatibilityTests(MomosHostFactory factory) => _factory = factory;

    [Fact]
    public async Task ClaimNext_WithProtocolVersionBelowMinimum_ReturnsUpdateRequiredAndNoWork()
    {
        var client = _factory.CreateAuthorizedClient();
        var response = await client.PostAsJsonAsync(
            "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 0, WorkerVersion: "0.1.0"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);

        Assert.True(body!.UpdateRequired);
        Assert.Null(body.Request);
    }

    [Fact]
    public async Task ClaimNext_WithCurrentProtocolVersionAndNoPendingWork_ReturnsNoUpdateRequired()
    {
        var client = _factory.CreateAuthorizedClient();
        // Drain whatever earlier tests in this shared fixture left pending.
        ClaimNextResponse? drained;
        do
        {
            var r = await client.PostAsJsonAsync(
                "/inspection-requests/claim-next", new ClaimNextRequest(ProtocolVersion: 1, WorkerVersion: "0.1.0"));
            drained = await r.Content.ReadFromJsonAsync<ClaimNextResponse>(TestJsonOptions.Value);
        } while (drained!.Request is not null);

        Assert.False(drained.UpdateRequired);
        Assert.Null(drained.Request);
    }
}
