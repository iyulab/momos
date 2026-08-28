using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class InspectionRequestEndpoints
{
    public static IEndpointRouteBuilder MapInspectionRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/inspection-requests", async (Guid projectId, CreateInspectionRequestRequest request, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([projectId], cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            if (!string.IsNullOrEmpty(request.CommitRef) && string.IsNullOrEmpty(project.RepositoryUrl))
            {
                // A CommitRef only means something once the Worker clones RepositoryUrl and
                // checks it out (PullExecutionBackgroundService) — without a RepositoryUrl
                // there's nothing to check the ref out of, so this is a caller mistake to
                // reject now rather than a value to silently ignore at run time.
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "CommitRef requires the project to have a RepositoryUrl.");
            }

            var inspectionRequest = new InspectionRequest
            {
                ProjectId = projectId,
                Focus = request.Focus,
                CommitRef = request.CommitRef,
            };

            db.InspectionRequests.Add(inspectionRequest);
            await db.SaveChangesAsync(cancellationToken);

            var response = InspectionRequestResponse.FromEntity(inspectionRequest);
            return Results.Created($"/inspection-requests/{inspectionRequest.Id}", response);
        })
            .WithName("CreateInspectionRequest")
            .Produces<InspectionRequestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/inspection-requests/{id:guid}", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var inspectionRequest = await db.InspectionRequests.FindAsync([id], cancellationToken);
            return inspectionRequest is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Inspection request not found.")
                : Results.Ok(InspectionRequestResponse.FromEntity(inspectionRequest));
        })
            .WithName("GetInspectionRequest")
            .Produces<InspectionRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/inspection-requests/claim-next", async (MomosDbContext db, CancellationToken cancellationToken) =>
        {
            // Conditional ExecuteUpdate (not a read-then-write) so the Pending check and the
            // transition to Running happen as one statement — SQLite serializes writes, so a
            // second concurrent claim against the same row affects zero rows instead of
            // double-claiming it. If that happens, retry against whatever is left.
            while (true)
            {
                // SQLite's EF Core provider refuses to translate ORDER BY over DateTimeOffset
                // (offset-aware string comparison isn't guaranteed instant-correct), so the
                // oldest-first pick happens client-side over the (small, Pending-only) id+
                // timestamp projection rather than in SQL.
                var pending = await db.InspectionRequests
                    .Where(r => r.Status == InspectionRequestStatus.Pending)
                    .Select(r => new { r.Id, r.SubmittedAt })
                    .ToListAsync(cancellationToken);

                if (pending.Count == 0)
                {
                    return Results.NoContent();
                }

                var candidateId = pending.OrderBy(r => r.SubmittedAt).First().Id;

                var claimed = await db.InspectionRequests
                    .Where(r => r.Id == candidateId && r.Status == InspectionRequestStatus.Pending)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(r => r.Status, InspectionRequestStatus.Running),
                        cancellationToken);

                if (claimed == 0)
                {
                    continue;
                }

                var inspectionRequest = await db.InspectionRequests.FindAsync([candidateId], cancellationToken);
                return Results.Ok(InspectionRequestResponse.FromEntity(inspectionRequest!));
            }
        })
            .WithName("ClaimNextInspectionRequest")
            .Produces<InspectionRequestResponse>()
            .Produces(StatusCodes.Status204NoContent);

        app.MapPost("/inspection-requests/{id:guid}/fail", async (Guid id, FailInspectionRequestRequest request, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var inspectionRequest = await db.InspectionRequests.FindAsync([id], cancellationToken);
            if (inspectionRequest is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Inspection request not found.");
            }

            if (inspectionRequest.Status != InspectionRequestStatus.Running)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Invalid status transition.",
                    detail: $"Cannot fail an inspection request in status '{inspectionRequest.Status}'.");
            }

            inspectionRequest.Status = InspectionRequestStatus.Failed;
            inspectionRequest.FailureReason = request.Reason;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(InspectionRequestResponse.FromEntity(inspectionRequest));
        })
            .WithName("FailInspectionRequest")
            .Produces<InspectionRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
