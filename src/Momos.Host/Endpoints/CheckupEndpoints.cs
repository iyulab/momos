using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Reporting;

namespace Momos.Host.Endpoints;

public static class CheckupEndpoints
{
    public static IEndpointRouteBuilder MapCheckupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/checkups", async (
            Guid projectId, CreateCheckupRequest? request, MomosDbContext db, IOptions<ReportingOptions> reporting,
            CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([projectId], cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            if (string.IsNullOrEmpty(project.RepositoryUrl))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A checkup requires the project to have a RepositoryUrl.");
            }

            if (!ReportLanguage.TryNormalize(request?.Language, out var requested))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["language"] = [$"Supported languages: {string.Join(", ", ReportLanguage.Supported.Order())}."],
                });
            }

            // The request names it, else the project, else this Host's default (validated at startup).
            var language = requested ?? project.ReportLanguage ?? reporting.Value.DefaultLanguage.Trim().ToLowerInvariant();
            var analysis = new InspectionRequest
            {
                ProjectId = projectId,
                Kind = InspectionRequestKind.Analysis,
                CommitRef = request?.CommitRef,
                Language = language,
            };
            var checkup = new Checkup { ProjectId = projectId, Language = language, CommitRef = request?.CommitRef };
            checkup.Exams.Add(new ExamRun { Program = ExamProgram.DesignAnalysis, RequestId = analysis.Id });
            db.InspectionRequests.Add(analysis);
            db.Checkups.Add(checkup);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/checkups/{checkup.Id}", CheckupResponse.FromEntity(checkup, new Dictionary<Guid, int>()));
        })
            .WithName("CreateCheckup")
            .Produces<CheckupResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/checkups/{id:guid}", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var checkup = await db.Checkups.Include(c => c.Exams).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            return checkup is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Checkup not found.")
                : Results.Ok(CheckupResponse.FromEntity(checkup, await ModelVersionsAsync(db, [checkup], cancellationToken)));
        })
            .WithName("GetCheckup")
            .Produces<CheckupResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/projects/{projectId:guid}/checkups", async (Guid projectId, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            var checkups = await db.Checkups.Include(c => c.Exams)
                .Where(c => c.ProjectId == projectId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(cancellationToken);
            var versions = await ModelVersionsAsync(db, checkups, cancellationToken);
            return Results.Ok(checkups.Select(c => CheckupResponse.FromEntity(c, versions)).ToList());
        })
            .WithName("ListCheckups")
            .Produces<List<CheckupResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Dictionary<Guid, int>> ModelVersionsAsync(
        MomosDbContext db, IEnumerable<Checkup> checkups, CancellationToken cancellationToken)
    {
        var ids = checkups.SelectMany(c => c.Exams).Select(e => e.ProjectModelId).OfType<Guid>().Distinct().ToList();
        return await db.ProjectModels.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.ModelVersion, cancellationToken);
    }
}
