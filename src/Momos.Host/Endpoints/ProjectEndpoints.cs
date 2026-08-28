using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects", async (CreateProjectRequest request, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)
                || string.IsNullOrWhiteSpace(request.Purpose)
                || string.IsNullOrWhiteSpace(request.Vision)
                || string.IsNullOrWhiteSpace(request.Scope))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Name, Purpose, Vision, and Scope are required."],
                });
            }

            var project = new Project
            {
                Name = request.Name,
                RepositoryUrl = request.RepositoryUrl,
                DeploymentUrl = request.DeploymentUrl,
                Purpose = request.Purpose,
                Vision = request.Vision,
                Scope = request.Scope,
            };

            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken);

            var response = ProjectResponse.FromEntity(project);
            return Results.Created($"/projects/{project.Id}", response);
        })
            .WithName("CreateProject")
            .Produces<ProjectResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        app.MapGet("/projects/{id:guid}", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([id], cancellationToken);
            return project is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.")
                : Results.Ok(ProjectResponse.FromEntity(project));
        })
            .WithName("GetProject")
            .Produces<ProjectResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
