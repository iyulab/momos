using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Endpoints;

public static class InspectionReportEndpoints
{
    public static IEndpointRouteBuilder MapInspectionReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/inspection-requests/{id:guid}/report", async (Guid id, MomosDbContext db) =>
        {
            var report = await db.InspectionReports
                .Include(r => r.Findings)
                .FirstOrDefaultAsync(r => r.InspectionRequestId == id);

            return report is null ? Results.NotFound() : Results.Ok(InspectionReportResponse.FromEntity(report));
        });

        app.MapPost("/inspection-requests/{id:guid}/report", async (Guid id, SubmitInspectionReportRequest request, MomosDbContext db) =>
        {
            var inspectionRequest = await db.InspectionRequests.FindAsync(id);
            if (inspectionRequest is null)
            {
                return Results.NotFound();
            }

            if (inspectionRequest.Status != InspectionRequestStatus.Running)
            {
                return Results.Conflict($"Cannot submit a report for an inspection request in status '{inspectionRequest.Status}'.");
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
            await db.SaveChangesAsync();

            return Results.Created($"/inspection-requests/{id}/report", InspectionReportResponse.FromEntity(report));
        });

        return app;
    }
}
