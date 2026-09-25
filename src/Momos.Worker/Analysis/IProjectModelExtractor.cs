using Momos.Worker.Execution;

namespace Momos.Worker.Analysis;

/// <summary>Builds a project model from a repository already checked out into an execution session.</summary>
public interface IProjectModelExtractor
{
    Task<ProjectModelPayload> ExtractAsync(ExecutionSessionHandle session, CancellationToken cancellationToken);
}
