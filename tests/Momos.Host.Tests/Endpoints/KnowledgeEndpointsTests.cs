using System.Net;
using System.Net.Http.Json;
using Momos.Host.Contracts;

namespace Momos.Host.Tests.Endpoints;

public sealed class KnowledgeEndpointsTests : IClassFixture<MomosHostFactory>
{
    private readonly HttpClient _client;

    public KnowledgeEndpointsTests(MomosHostFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterDocument_ForAnExistingProject_Returns201()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-docs", null, null, "purpose", "vision", "scope"));
        var created = await project.Content.ReadFromJsonAsync<ProjectResponse>();

        var response = await _client.PostAsJsonAsync(
            $"/projects/{created!.Id}/knowledge/documents",
            new RegisterKnowledgeDocumentRequest("PRD", "The system must support single sign-on."));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterKnowledgeDocumentResponse>();
        Assert.False(string.IsNullOrEmpty(body!.DocumentId));
    }

    [Fact]
    public async Task RegisterDocument_ForAMissingProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/projects/{Guid.NewGuid()}/knowledge/documents",
            new RegisterKnowledgeDocumentRequest("PRD", "content"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Query_AfterRegisteringADocument_ReturnsAMatchingSnippet()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-query", null, null, "purpose", "vision", "scope"));
        var created = await project.Content.ReadFromJsonAsync<ProjectResponse>();
        await _client.PostAsJsonAsync(
            $"/projects/{created!.Id}/knowledge/documents",
            new RegisterKnowledgeDocumentRequest("PRD", "The system must support single sign-on via SAML."));

        var response = await _client.PostAsJsonAsync(
            $"/projects/{created.Id}/knowledge/query",
            new QueryKnowledgeRequest("single sign-on", MaxResults: 5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QueryKnowledgeResponse>();
        Assert.Contains(body!.Snippets, s => s.Content.Contains("single sign-on"));
    }

    [Fact]
    public async Task Query_AgainstAProjectWithNoKnowledge_ReturnsEmptyNotAnError()
    {
        var project = await _client.PostAsJsonAsync("/projects",
            new CreateProjectRequest("acme-empty", null, null, "purpose", "vision", "scope"));
        var created = await project.Content.ReadFromJsonAsync<ProjectResponse>();

        var response = await _client.PostAsJsonAsync(
            $"/projects/{created!.Id}/knowledge/query",
            new QueryKnowledgeRequest("anything", MaxResults: 5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QueryKnowledgeResponse>();
        Assert.Empty(body!.Snippets);
    }
}
