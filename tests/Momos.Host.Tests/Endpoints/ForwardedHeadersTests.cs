using System.Net;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Momos.Host.Tests.Endpoints;

/// <summary>
/// This app sits behind a reverse proxy that terminates TLS and forwards plain
/// HTTP internally — a standard pattern for platform-managed ingress. Without
/// trusting <c>X-Forwarded-Proto</c>, every scheme-sensitive check downstream
/// (redirect decisions, secure-cookie flags, generated absolute URLs) sees the
/// request as plain HTTP even when the original client used HTTPS. These tests
/// give the pipeline an actual HTTPS port to redirect to (the app itself has
/// none configured, so <c>UseHttpsRedirection</c> would otherwise be a silent
/// no-op here) so the forwarded-header behavior is genuinely exercised rather
/// than tautologically passing either way.
/// </summary>
public sealed class ForwardedHeadersTests : IClassFixture<MomosHostFactory>
{
    private readonly MomosHostFactory _factory;

    public ForwardedHeadersTests(MomosHostFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> WithHttpsPort() =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.PostConfigure<HttpsRedirectionOptions>(o => o.HttpsPort = 443)));

    [Fact]
    public async Task Health_WithoutForwardedProto_RedirectsToHttps()
    {
        using var factory = WithHttpsPort();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithForwardedHttpsProto_DoesNotRedirect()
    {
        using var factory = WithHttpsPort();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
