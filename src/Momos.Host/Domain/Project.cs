namespace Momos.Host.Domain;

/// <summary>
/// A target's purpose/vision/scope declaration. Registered once, changes rarely.
/// Momos never writes anything into the target's own repository (non-invasiveness) —
/// this declaration lives here, not there.
/// </summary>
public sealed class Project
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? DeploymentUrl { get; set; }
    public required string Purpose { get; set; }
    public required string Vision { get; set; }
    public required string Scope { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
