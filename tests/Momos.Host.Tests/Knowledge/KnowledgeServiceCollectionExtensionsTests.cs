using FluxIndex.Core.Application.Interfaces;
using FluxIndex.Providers.OpenAI.Services;
using FluxIndex.SDK;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    [Fact]
    public void AnUnreachableKnowledgeStore_FailsStartupNamingTheSetting()
    {
        // The store used to be built on first use, so the Host started cleanly and the first
        // unrelated request failed with a 500. A broken store is a configuration error.
        using var broken = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Momos:Host:Knowledge:ConnectionString", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"));

        var thrown = Assert.ThrowsAny<Exception>(() => broken.Services);

        Assert.Contains("Momos:Host:Knowledge:ConnectionString", thrown.ToString());
    }

    [Fact]
    public void AnEmbeddingApiKeyWithoutAnEndpoint_FailsStartup()
    {
        // Proves the validator is wired into startup; its rules are covered in
        // KnowledgeOptionsValidatorTests without starting a host per environment.
        using var inconsistent = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Momos:Host:Knowledge:EmbeddingApiKey", "key"));

        var thrown = Assert.ThrowsAny<OptionsValidationException>(() => inconsistent.Services);

        Assert.Contains("EmbeddingEndpoint", thrown.Message);
    }
}
