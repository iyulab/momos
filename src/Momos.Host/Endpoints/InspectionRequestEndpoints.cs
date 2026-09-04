using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

        app.MapPost("/inspection-requests/claim-next", async (
            ClaimNextRequest claimRequest, MomosDbContext db, IOptions<InspectionClaimOptions> claimOptions,
            IOptions<WorkerCompatibilityOptions> compatOptions, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var compat = compatOptions.Value;
            if (claimRequest.ProtocolVersion < compat.MinSupportedProtocolVersion)
            {
                // A version-mismatched Worker can't safely interpret what claim-next would hand
                // it, so it gets no work — only the update signal that tells it to stop asking
                // until it's upgraded.
                return Results.Ok(new ClaimNextResponse(Request: null, UpdateRequired: true, compat.RecommendedWorkerVersion));
            }

            var recommendedHint = claimRequest.WorkerVersion == compat.RecommendedWorkerVersion ? null : compat.RecommendedWorkerVersion;

            // A request is eligible when it's Pending, or when it's Running but was claimed
            // before the reclaim cutoff — its worker is presumed gone. There's no heartbeat to
            // tell "still running, just slow" from "dead" (see InspectionClaimOptions), so this
            // cutoff is the only signal; a request reclaimed too early can end up running twice,
            // but report/fail's Running-only guard stops the loser from corrupting the result.
            var reclaimCutoff = timeProvider.GetUtcNow() - claimOptions.Value.ReclaimTimeout;

            // Conditional ExecuteUpdate (not a read-then-write) so the eligibility recheck and
            // the transition to Running happen as one statement — under PostgreSQL's default
            // READ COMMITTED isolation, ExecuteUpdate re-evaluates the WHERE predicate against
            // the current row before applying, so a second concurrent claim against the same row
            // affects zero rows instead of double-claiming it. If that happens, retry against
            // whatever is left.
            while (true)
            {
                // Npgsql translates DateTimeOffset ordering/range comparisons natively (the
                // column is timestamp with time zone), so the oldest-first pick and the
                // reclaim-eligibility check both run server-side instead of materializing the
                // whole Pending+Running set on every worker poll.
                var candidate = await db.InspectionRequests
                    .Where(r => r.Status == InspectionRequestStatus.Pending
                        || (r.Status == InspectionRequestStatus.Running && r.ClaimedAt < reclaimCutoff))
                    .OrderBy(r => r.SubmittedAt)
                    .Select(r => new { r.Id, r.SubmittedAt, r.Status, r.ClaimedAt })
                    .FirstOrDefaultAsync(cancellationToken);

                if (candidate is null)
                {
                    return Results.Ok(new ClaimNextResponse(Request: null, UpdateRequired: false, recommendedHint));
                }

                var claimedAt = timeProvider.GetUtcNow();

                // Guards on the exact (Status, ClaimedAt) observed above — a reclaim leaves
                // Status at Running (unlike the one-way Pending→Running transition), so Status
                // alone isn't a strong enough guard here; this is a compare-and-swap on both so
                // a second concurrent claim of the same reclaim-eligible row affects zero rows.
                var claimed = await db.InspectionRequests
                    .Where(r => r.Id == candidate.Id && r.Status == candidate.Status && r.ClaimedAt == candidate.ClaimedAt)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(r => r.Status, InspectionRequestStatus.Running)
                            .SetProperty(r => r.ClaimedAt, claimedAt),
                        cancellationToken);

                if (claimed == 0)
                {
                    continue;
                }

                var inspectionRequest = await db.InspectionRequests.FindAsync([candidate.Id], cancellationToken);
                return Results.Ok(new ClaimNextResponse(InspectionRequestResponse.FromEntity(inspectionRequest!), UpdateRequired: false, recommendedHint));
            }
        })
            .WithName("ClaimNextInspectionRequest")
            .AddEndpointFilter<WorkerApiKeyFilter>()
            .Produces<ClaimNextResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

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
            .AddEndpointFilter<WorkerApiKeyFilter>()
            .Produces<InspectionRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
