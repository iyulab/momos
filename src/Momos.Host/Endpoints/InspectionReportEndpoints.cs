using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Knowledge;

namespace Momos.Host.Endpoints;

public static class InspectionReportEndpoints
{
    public static IEndpointRouteBuilder MapInspectionReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/inspection-requests/{id:guid}/report", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var report = await db.InspectionReports
                .Include(r => r.Findings.OrderBy(f => f.Order))
                .FirstOrDefaultAsync(r => r.InspectionRequestId == id, cancellationToken);

            if (report is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Inspection report not found.");
            }

            // Joined by InspectionRequestId, not an Include off `report` — ToolCall hangs off
            // InspectionRequest now (see InspectionReport's doc comment), a separate query since
            // EF Core has no navigation from InspectionReport to sibling ToolCall rows.
            var toolCalls = await db.ToolCalls
                .Where(t => t.InspectionRequestId == id)
                .OrderBy(t => t.Order)
                .ToListAsync(cancellationToken);

            return Results.Ok(InspectionReportResponse.FromEntity(report, toolCalls));
        })
            .WithName("GetInspectionReport")
            .Produces<InspectionReportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/inspection-requests/{id:guid}/report", async (
            Guid id, SubmitInspectionReportRequest request, MomosDbContext db,
            IKnowledgeIndex knowledgeIndex, ILogger<Program> logger, CancellationToken cancellationToken) =>
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
                    detail: $"Cannot submit a report for an inspection request in status '{inspectionRequest.Status}'.");
            }

            var report = new InspectionReport { InspectionRequestId = id };
            for (var order = 0; order < request.Findings.Count; order++)
            {
                var finding = request.Findings[order];
                report.Findings.Add(new Finding
                {
                    InspectionReportId = report.Id,
                    Category = finding.Category,
                    Description = finding.Description,
                    Evidence = finding.Evidence,
                    Order = order,
                });
            }

            // Added to the DbSet directly, not to inspectionRequest.ToolCalls — that
            // navigation isn't loaded on this tracked-but-not-Added entity, so a new
            // ToolCall discovered only via graph fixup would have its already-populated
            // Id read as "this key already exists" and get UPDATEd instead of INSERTed.
            var toolCalls = new List<ToolCall>();
            for (var order = 0; order < request.ToolCalls.Count; order++)
            {
                var call = request.ToolCalls[order];
                var toolCall = new ToolCall
                {
                    InspectionRequestId = id,
                    Tool = call.Tool,
                    Summary = call.Summary,
                    Success = call.Success,
                    DurationMs = call.DurationMs,
                    Order = order,
                };
                toolCalls.Add(toolCall);
                db.ToolCalls.Add(toolCall);
            }

            db.InspectionReports.Add(report);
            inspectionRequest.Status = InspectionRequestStatus.Completed;
            await db.SaveChangesAsync(cancellationToken);

            // The report is already committed above — indexing it into the knowledge base
            // is a best-effort side effect, not part of the submission's success criteria.
            // A failure here (embedding endpoint unreachable, database connection timeout, etc.)
            // must not turn an already-persisted report into an apparent 500 to the Worker,
            // which would retry against a request that no longer accepts submissions
            // (Status is already Completed) and see a confusing 409 instead.
            foreach (var finding in report.Findings)
            {
                try
                {
                    await knowledgeIndex.IndexAsync(
                        $"{finding.Category}: {finding.Description}\nEvidence: {finding.Evidence}",
                        $"finding:{finding.Id}",
                        new Dictionary<string, object>
                        {
                            ["ProjectId"] = inspectionRequest.ProjectId.ToString(),
                            ["SourceType"] = "finding",
                            ["InspectionReportId"] = report.Id.ToString(),
                        },
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to index finding {FindingId} into the project knowledge base.", finding.Id);
                }
            }

            return Results.Created($"/inspection-requests/{id}/report", InspectionReportResponse.FromEntity(report, toolCalls));
        })
            .WithName("SubmitInspectionReport")
            .AddEndpointFilter<WorkerApiKeyFilter>()
            .Produces<InspectionReportResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
