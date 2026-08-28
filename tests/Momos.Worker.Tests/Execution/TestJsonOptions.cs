using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momos.Worker.Tests.Execution;

/// <summary>
/// Matches the Host's own <c>ConfigureHttpJsonOptions</c> (Momos.Host/Program.cs) —
/// for the raw <see cref="HttpClient"/> calls in these tests that bypass
/// <see cref="Momos.Worker.Execution.HostApiClient"/> (which already carries its own
/// matching options). Needed wherever a test reads a response DTO carrying a named
/// enum (e.g. <c>InspectionRequestResponse.Status</c>).
/// </summary>
internal static class TestJsonOptions
{
    public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
