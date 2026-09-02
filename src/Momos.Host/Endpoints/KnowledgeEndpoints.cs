using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Knowledge;

namespace Momos.Host.Endpoints;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/knowledge/documents", async (
            Guid projectId, RegisterKnowledgeDocumentRequest request,
            MomosDbContext db, IKnowledgeIndex knowledgeIndex, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([projectId], cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            var documentId = $"doc:{Guid.NewGuid()}";
            await knowledgeIndex.IndexAsync(
                request.Content,
                documentId,
                new Dictionary<string, object>
                {
                    ["ProjectId"] = projectId.ToString(),
                    ["SourceType"] = "document",
                    ["Title"] = request.Title,
                },
                cancellationToken);

            return Results.Created(
                $"/projects/{projectId}/knowledge/documents/{documentId}",
                new RegisterKnowledgeDocumentResponse(documentId));
        })
            .WithName("RegisterKnowledgeDocument")
            .Produces<RegisterKnowledgeDocumentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
