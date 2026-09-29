using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Knowledge;
using Momos.Host.Reporting;

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

        app.MapGet("/projects/{projectId:guid}/model/report", async (Guid projectId, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            var model = await ProjectModelQueries.LatestAsync(db, projectId, cancellationToken);
            return model is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "This project has no model yet.")
                : Results.Ok(new ProjectModelReportResponse(
                    model.ModelVersion,
                    model.BaseCommit,
                    DeepReportRenderer.Render(project.Name, model).Select(d => new ReportDocumentDto(d.Path, d.Content)).ToList()));
        })
            .WithName("GetProjectModelReport")
            .Produces<ProjectModelReportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The claim key is matched ordinally against the latest model only: a verdict on an older
        // version would describe a statement the project no longer carries. Keys are not
        // constrained by the route; an unknown key is simply a claim that is not there (404).
        app.MapPost("/projects/{projectId:guid}/model/claims/{claimKey}/corrections", async (
            Guid projectId, string claimKey, CorrectClaimRequest request, MomosDbContext db,
            ModelProjectionSignal projection, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (request.Status == ClaimStatus.Proposed)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "A correction must confirm, dispute or correct the claim.");
            }

            if (request.Status == ClaimStatus.Corrected && string.IsNullOrWhiteSpace(request.Correction))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "A Corrected verdict needs the corrected understanding.");
            }

            // Loaded with tracking, so the verdict below is saved on the claim entity itself.
            var model = await ProjectModelQueries.LatestAsync(db, projectId, cancellationToken);
            var claim = model?.Claims.SingleOrDefault(c => string.Equals(c.Key, claimKey, StringComparison.Ordinal));
            if (claim is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Claim not found in the latest model.");
            }

            // The statement itself is never rewritten or deleted: the verdict sits beside it, so
            // the report can always show what Momos said and what the developer said instead.
            claim.Status = request.Status;
            claim.Correction = string.IsNullOrWhiteSpace(request.Correction) ? null : request.Correction.Trim();
            claim.CorrectedAt = timeProvider.GetUtcNow();

            // The indexed document still carries the old verdict: the model is unindexed again
            // until ModelProjectionService re-projects it in the background.
            model!.KnowledgeIndexedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            projection.Notify();
            return Results.Ok(ClaimResponse.FromEntity(claim));
        })
            .WithName("CorrectProjectModelClaim")
            .Produces<ClaimResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
