using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Momos.Host.Endpoints;

/// <summary>
/// Rejects requests to the endpoints it's applied to unless they carry
/// <c>Authorization: Bearer {WorkerAuthOptions.ApiKey}</c>. Scoped to the worker-facing
/// write endpoints (claim-next/report/fail) via <c>AddEndpointFilter</c> — project and
/// inspection-request read/create endpoints are a separate integration surface and are
/// not covered by this filter.
/// </summary>
public sealed class WorkerApiKeyFilter(IOptions<WorkerAuthOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[HeaderNames.Authorization].ToString();
        if (provided != $"Bearer {options.Value.ApiKey}")
        {
            return ValueTask.FromResult<object?>(TypedResults.Unauthorized());
        }

        return next(context);
    }
}
