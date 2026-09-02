namespace Momos.Host.Contracts;

public sealed record RegisterKnowledgeDocumentRequest(string Title, string Content);

public sealed record RegisterKnowledgeDocumentResponse(string DocumentId);

public sealed record QueryKnowledgeRequest(string Query, int MaxResults = 5);

public sealed record QueryKnowledgeResponse(IReadOnlyList<KnowledgeSnippet> Snippets);

public sealed record KnowledgeSnippet(string Content, double Score);
