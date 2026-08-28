using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class InspectionReportEndpoints
{
    public static IEndpointRouteBuilder MapInspectionReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/inspection-requests/{id:guid}/report", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var report = await db.InspectionReports
                .Include(r => r.Findings)
                .FirstOrDefaultAsync(r => r.InspectionRequestId == id, cancellationToken);

            return report is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Inspection report not found.")
                : Results.Ok(InspectionReportResponse.FromEntity(report));
        })
            .WithName("GetInspectionReport")
            .Produces<InspectionReportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/inspection-requests/{id:guid}/report", async (Guid id, SubmitInspectionReportRequest request, MomosDbContext db, CancellationToken cancellationToken) =>
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
            foreach (var finding in request.Findings)
            {
                report.Findings.Add(new Finding
                {
                    InspectionReportId = report.Id,
                    Category = finding.Category,
                    Description = finding.Description,
                    Evidence = finding.Evidence,
                });
            }

            db.InspectionReports.Add(report);
            inspectionRequest.Status = InspectionRequestStatus.Completed;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/inspection-requests/{id}/report", InspectionReportResponse.FromEntity(report));
        })
            .WithName("SubmitInspectionReport")
            .Produces<InspectionReportResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
