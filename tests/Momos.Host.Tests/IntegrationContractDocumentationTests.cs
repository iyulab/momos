using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Momos.Host.Tests;

/// <summary>
/// <c>docs/integration-contract.md</c> is how an integrator learns what the Host serves, and
/// nothing tied it to the routes the Host actually maps: two knowledge endpoints went
/// unlisted. These tests compare both sides — every mapped route is documented, and every
/// documented route is mapped. Route parameter names and constraints are ignored
/// (<c>{projectId:guid}</c> and <c>{id}</c> are the same slot).
/// </summary>
public sealed partial class IntegrationContractDocumentationTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private const string ContractPath = "docs/integration-contract.md";

    [Fact]
    public void EveryMappedRoute_IsDocumentedInTheIntegrationContract()
    {
        var undocumented = MappedRoutes().Except(DocumentedRoutes()).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            undocumented.Count == 0,
            $"The Host maps {string.Join(", ", undocumented)}, but {ContractPath} never mentions them "
            + "— an integrator has no way to find those endpoints.");
    }

    [Fact]
    public void EveryDocumentedRoute_IsMappedByTheHost()
    {
        var stale = DocumentedRoutes().Except(MappedRoutes()).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            $"{ContractPath} documents {string.Join(", ", stale)}, but the Host maps no such route.");
    }

    private HashSet<string> MappedRoutes()
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            // The OpenAPI document is mapped in Development only — it is not part of the contract.
            if (pattern.StartsWith("/openapi/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                routes.Add(Normalize(method, pattern));
            }
        }

        return routes;
    }

    private static HashSet<string> DocumentedRoutes()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, ContractPath));
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TableRow().Matches(text))
        {
            routes.Add(Normalize(match.Groups["method"].Value, match.Groups["path"].Value));
        }

        foreach (Match match in InlineRoute().Matches(text))
        {
            routes.Add(Normalize(match.Groups["method"].Value, match.Groups["path"].Value));
        }

        return routes;
    }

    private static string Normalize(string method, string path) =>
        $"{method.ToUpperInvariant()} {RouteParameter().Replace(path, "{}")}";

    // | `POST` | `/projects` | …
    [GeneratedRegex(@"^\|\s*`(?<method>GET|POST|PUT|PATCH|DELETE)`\s*\|\s*`(?<path>/[^`]*)`", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    // `POST /analysis-requests/{id}/model`
    [GeneratedRegex(@"`(?<method>GET|POST|PUT|PATCH|DELETE) (?<path>/[^`\s]*)`")]
    private static partial Regex InlineRoute();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    /// <summary>
    /// Walks up from the test binary's directory to the first ancestor holding the contract,
    /// so the suite works wherever it runs from.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ContractPath)))
            {
                return dir;
            }
        }

        throw new InvalidOperationException($"No ancestor of '{AppContext.BaseDirectory}' contains '{ContractPath}'.");
    }
}
