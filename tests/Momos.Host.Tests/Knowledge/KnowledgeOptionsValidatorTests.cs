using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Momos.Host.Knowledge;

namespace Momos.Host.Tests.Knowledge;

public sealed class KnowledgeOptionsValidatorTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Momos.Host";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static KnowledgeOptions Options(string? endpoint = null, string? apiKey = null) =>
        new() { ConnectionString = "Host=localhost", EmbeddingEndpoint = endpoint, EmbeddingApiKey = apiKey };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopment_AMissingEndpoint_Fails(string environment)
    {
        var result = new KnowledgeOptionsValidator(new FakeEnvironment(environment)).Validate(null, Options());

        Assert.True(result.Failed);
        Assert.Contains("EmbeddingEndpoint", result.FailureMessage);
    }

    [Fact]
    public void InDevelopment_AMissingEndpoint_IsAllowed() =>
        Assert.True(new KnowledgeOptionsValidator(new FakeEnvironment("Development")).Validate(null, Options()).Succeeded);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void AnApiKeyWithoutAnEndpoint_Fails(string environment)
    {
        var result = new KnowledgeOptionsValidator(new FakeEnvironment(environment)).Validate(null, Options(apiKey: "key"));

        Assert.True(result.Failed);
        Assert.Contains("EmbeddingApiKey", result.FailureMessage);
    }

    [Fact]
    public void OutsideDevelopment_AnEndpoint_IsValid() =>
        Assert.True(new KnowledgeOptionsValidator(new FakeEnvironment("Production"))
            .Validate(null, Options(endpoint: "http://embedding.invalid/v1", apiKey: "key")).Succeeded);
}
