using FluxIndex.Core.Application.Interfaces;
using FluxIndex.Providers.OpenAI.Services;
using FluxIndex.SDK;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Momos.Host.Tests.Knowledge;

public sealed class KnowledgeServiceCollectionExtensionsTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    private static IEmbeddingService EmbeddingServiceOf(IServiceProvider hostServices) =>
        ((FluxIndexContext)hostServices.GetRequiredService<IFluxIndexContext>())
            .ServiceProvider.GetRequiredService<IEmbeddingService>();

    [Fact]
    public void AConfiguredEmbeddingEndpoint_IsTheEmbeddingServiceTheIndexUses()
    {
        // Nothing is embedded here, so the endpoint is never called.
        using var configured = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Momos:Host:Knowledge:EmbeddingEndpoint", "http://embedding.invalid/v1")
            .UseSetting("Momos:Host:Knowledge:EmbeddingApiKey", "key"));

        Assert.IsType<OpenAICompatibleEmbeddingService>(EmbeddingServiceOf(configured.Services));
    }

    [Fact]
    public void WithoutAnEmbeddingEndpoint_TheIndexFallsBackToTheInMemoryService() =>
        Assert.IsNotType<OpenAICompatibleEmbeddingService>(EmbeddingServiceOf(factory.Services));
}
