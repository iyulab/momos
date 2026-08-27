using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class InspectionRequestEndpoints
{
    public static IEndpointRouteBuilder MapInspectionRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/inspection-requests", async (Guid projectId, CreateInspectionRequestRequest request, MomosDbContext db) =>
        {
            var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId);
            if (!projectExists)
            {
                return Results.NotFound();
            }

            var inspectionRequest = new InspectionRequest
            {
                ProjectId = projectId,
                Focus = request.Focus,
            };

            db.InspectionRequests.Add(inspectionRequest);
            await db.SaveChangesAsync();

            var response = InspectionRequestResponse.FromEntity(inspectionRequest);
            return Results.Created($"/inspection-requests/{inspectionRequest.Id}", response);
        });

        app.MapGet("/inspection-requests/{id:guid}", async (Guid id, MomosDbContext db) =>
        {
            var inspectionRequest = await db.InspectionRequests.FindAsync(id);
            return inspectionRequest is null
                ? Results.NotFound()
                : Results.Ok(InspectionRequestResponse.FromEntity(inspectionRequest));
        });

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
        });

        app.MapPost("/inspection-requests/{id:guid}/fail", async (Guid id, FailInspectionRequestRequest request, MomosDbContext db) =>
        {
            var inspectionRequest = await db.InspectionRequests.FindAsync(id);
            if (inspectionRequest is null)
            {
                return Results.NotFound();
            }

            if (inspectionRequest.Status != InspectionRequestStatus.Running)
            {
                return Results.Conflict($"Cannot fail an inspection request in status '{inspectionRequest.Status}'.");
            }

            inspectionRequest.Status = InspectionRequestStatus.Failed;
            inspectionRequest.FailureReason = request.Reason;
            await db.SaveChangesAsync();

            return Results.Ok(InspectionRequestResponse.FromEntity(inspectionRequest));
        });

        return app;
    }
}
