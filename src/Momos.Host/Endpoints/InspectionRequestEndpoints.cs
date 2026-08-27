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

        return app;
    }
}
