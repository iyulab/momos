using Microsoft.EntityFrameworkCore;
using Momos.Host.Contracts;
using Momos.Host.Data;

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

        return app;
    }
}
