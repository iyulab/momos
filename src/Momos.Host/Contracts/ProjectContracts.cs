using Momos.Host.Domain;

namespace Momos.Host.Contracts;

public sealed record CreateProjectRequest(
    string Name,
    string? RepositoryUrl,
    string? DeploymentUrl,
    string Purpose,
    string Vision,
    string Scope);

public sealed record ProjectResponse(
    Guid Id,
    string Name,
    string? RepositoryUrl,
    string? DeploymentUrl,
    string Purpose,
    string Vision,
    string Scope,
    DateTimeOffset CreatedAt)
{
    public static ProjectResponse FromEntity(Project project) => new(
        project.Id,
        project.Name,
        project.RepositoryUrl,
        project.DeploymentUrl,
        project.Purpose,
        project.Vision,
        project.Scope,
        project.CreatedAt);
}
