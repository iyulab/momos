using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class AnalysisRequestEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/analysis-requests", async (
            Guid projectId, CreateAnalysisRequestRequest request, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([projectId], cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            // A project model is extracted from the repository, so there is nothing to analyze
            // without one — reject now rather than claim work that can only fail.
            if (string.IsNullOrEmpty(project.RepositoryUrl))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Analysis requires the project to have a RepositoryUrl.");
            }

            var analysisRequest = new InspectionRequest
            {
                ProjectId = projectId,
                Kind = InspectionRequestKind.Analysis,
                CommitRef = request.CommitRef,
            };
            db.InspectionRequests.Add(analysisRequest);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/analysis-requests/{analysisRequest.Id}", InspectionRequestResponse.FromEntity(analysisRequest));
        })
            .WithName("CreateAnalysisRequest")
            .Produces<InspectionRequestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/analysis-requests/{id:guid}", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var analysisRequest = await db.InspectionRequests.FindAsync([id], cancellationToken);
            return analysisRequest is null || analysisRequest.Kind != InspectionRequestKind.Analysis
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Analysis request not found.")
                : Results.Ok(InspectionRequestResponse.FromEntity(analysisRequest));
        })
            .WithName("GetAnalysisRequest")
            .Produces<InspectionRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
