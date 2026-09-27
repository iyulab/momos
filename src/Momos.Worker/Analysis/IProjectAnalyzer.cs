using Momos.Worker.Execution;

namespace Momos.Worker.Analysis;

/// <summary>Builds the project model an analysis request submits, from a repository already checked out into the session.</summary>
public interface IProjectAnalyzer
{
    Task<ProjectModelPayload> AnalyzeAsync(ExecutionSessionHandle session, ProjectInfo project, CancellationToken cancellationToken);
}
