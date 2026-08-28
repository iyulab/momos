using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momos.Host.Tests;

/// <summary>
/// Matches the Host's own <c>ConfigureHttpJsonOptions</c> (Momos.Host/Program.cs) —
/// <see cref="System.Net.Http.Json.HttpClientJsonExtensions"/> read/write with
/// <see cref="JsonSerializerOptions.Default"/> unless told otherwise, which does not
/// see the server's DI-configured options. Needed wherever a test reads a response
/// DTO carrying a named enum (e.g. <c>InspectionRequestResponse.Status</c>).
/// </summary>
internal static class TestJsonOptions
{
    public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
