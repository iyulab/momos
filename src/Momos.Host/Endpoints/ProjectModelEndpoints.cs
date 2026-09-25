using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class ProjectModelQueries
{
    public static Task<ProjectModel?> LatestAsync(MomosDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        db.ProjectModels
            .Include(m => m.Claims)
            .Where(m => m.ProjectId == projectId)
            .OrderByDescending(m => m.ModelVersion)
            .FirstOrDefaultAsync(cancellationToken);
}

public static class ProjectModelEndpoints
{
    public static IEndpointRouteBuilder MapProjectModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/projects/{projectId:guid}/model", async (Guid projectId, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var model = await ProjectModelQueries.LatestAsync(db, projectId, cancellationToken);
            return model is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "This project has no model yet.")
                : Results.Ok(ProjectModelResponse.FromEntity(model));
        })
            .WithName("GetProjectModel")
            .Produces<ProjectModelResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
