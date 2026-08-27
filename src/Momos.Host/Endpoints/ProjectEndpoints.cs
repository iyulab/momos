using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects", async (CreateProjectRequest request, MomosDbContext db) =>
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
            await db.SaveChangesAsync();

            var response = ProjectResponse.FromEntity(project);
            return Results.Created($"/projects/{project.Id}", response);
        });

        app.MapGet("/projects/{id:guid}", async (Guid id, MomosDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            return project is null ? Results.NotFound() : Results.Ok(ProjectResponse.FromEntity(project));
        });

        return app;
    }
}
