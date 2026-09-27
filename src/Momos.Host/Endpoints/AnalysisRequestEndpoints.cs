using Momos.Host.Contracts;
using Momos.Host.Data;
using Momos.Host.Domain;
using Momos.Host.Knowledge;

namespace Momos.Host.Endpoints;

public static class AnalysisRequestEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/analysis-requests", async (
            Guid projectId, CreateAnalysisRequestRequest? request, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FindAsync([projectId], cancellationToken);
            if (project is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Project not found.");
            }

            // A project model is extracted from the repository, so there is nothing to analyze
            // without one — reject now rather than claim work that can only fail.
            if (string.IsNullOrEmpty(project.RepositoryUrl))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Analysis requires the project to have a RepositoryUrl.");
            }

            var analysisRequest = new InspectionRequest
            {
                ProjectId = projectId,
                Kind = InspectionRequestKind.Analysis,
                CommitRef = request?.CommitRef,
            };
            db.InspectionRequests.Add(analysisRequest);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/analysis-requests/{analysisRequest.Id}", InspectionRequestResponse.FromEntity(analysisRequest));
        })
            .WithName("CreateAnalysisRequest")
            .Produces<InspectionRequestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/analysis-requests/{id:guid}", async (Guid id, MomosDbContext db, CancellationToken cancellationToken) =>
        {
            var analysisRequest = await db.InspectionRequests.FindAsync([id], cancellationToken);
            return analysisRequest is null || analysisRequest.Kind != InspectionRequestKind.Analysis
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Analysis request not found.")
                : Results.Ok(InspectionRequestResponse.FromEntity(analysisRequest));
        })
            .WithName("GetAnalysisRequest")
            .Produces<InspectionRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/analysis-requests/{id:guid}/model", async (
            Guid id, SubmitProjectModelRequest request, MomosDbContext db, ModelKnowledgeProjector projector,
            CancellationToken cancellationToken) =>
        {
            var analysisRequest = await db.InspectionRequests.FindAsync([id], cancellationToken);
            if (analysisRequest is null || analysisRequest.Kind != InspectionRequestKind.Analysis)
            {
                return analysisRequest is null
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Analysis request not found.")
                    : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Wrong completion contract.",
                        detail: "An inspection request completes with an inspection report, not a project model.");
            }

            if (analysisRequest.Status != InspectionRequestStatus.Running)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Invalid status transition.",
                    detail: $"Cannot submit a model for an analysis request in status '{analysisRequest.Status}'.");
            }

            var missing = request.MissingFields();
            if (missing.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["model"] = [.. missing] });
            }

            var previous = await ProjectModelQueries.LatestAsync(db, analysisRequest.ProjectId, cancellationToken);
            var model = new ProjectModel
            {
                ProjectId = analysisRequest.ProjectId,
                ModelVersion = (previous?.ModelVersion ?? 0) + 1,
                BaseCommit = request.BaseCommit,
                AnalysisRequestId = id,
            };
            var elements = request.ToElements();
            model.Components.AddRange(elements.Components);
            model.Relations.AddRange(elements.Relations);
            model.Patterns.AddRange(elements.Patterns);
            model.Decisions.AddRange(elements.Decisions);
            model.Intents.AddRange(elements.Intents);

            var priorByKey = previous?.Claims.ToDictionary(c => c.Key, StringComparer.Ordinal)
                ?? new Dictionary<string, ModelClaim>(StringComparer.Ordinal);
            foreach (var submitted in request.Claims)
            {
                var claim = new ModelClaim
                {
                    ProjectModelId = model.Id,
                    Key = submitted.Key,
                    Tier = submitted.Tier,
                    Statement = submitted.Statement,
                    Evidence = submitted.Evidence.Select(e => e.ToDomain()).ToList(),
                    Confidence = submitted.Confidence,
                    // MissingFields() has already rejected a claim without an origin.
                    Origin = submitted.Origin!.Value,
                };

                // A developer's verdict describes a statement, so it carries over only while the
                // re-analysis still says the same thing; a changed statement starts over as Proposed.
                if (priorByKey.TryGetValue(claim.Key, out var prior) && prior.Statement == claim.Statement)
                {
                    claim.Status = prior.Status;
                    claim.Correction = prior.Correction;
                    claim.CorrectedAt = prior.CorrectedAt;
                }

                model.Claims.Add(claim);
            }

            var errors = ClaimValidator.Validate(model.Claims.ToList(), elements);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["claims"] = [.. errors] });
            }

            db.ProjectModels.Add(model);
            analysisRequest.Status = InspectionRequestStatus.Completed;
            await db.SaveChangesAsync(cancellationToken);

            // After the commit: the index is derived from the stored model, and projection is
            // best-effort, so a failure here never turns an accepted model into an error.
            await projector.ProjectModelAsync(model, previous, cancellationToken);

            return Results.Created($"/projects/{model.ProjectId}/model", ProjectModelResponse.FromEntity(model));
        })
            .WithName("SubmitProjectModel")
            .AddEndpointFilter<WorkerApiKeyFilter>()
            .Produces<ProjectModelResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
