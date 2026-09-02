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

    /// <summary>
    /// Optional third kind of provided material (alongside <see cref="RepositoryUrl"/> and
    /// <see cref="DeploymentUrl"/>) — extends D-41's "depth varies by what's provided" model
    /// rather than replacing it with a mutually-exclusive intake-type enum. Materials are
    /// additive: a Project can supply any combination of the three.
    /// <see cref="AppInstallerUri"/>, <see cref="AppInstallPlatform"/>, and
    /// <see cref="AppInstallLaunchCommand"/> travel together — either all set or all null;
    /// <see cref="AppInstallArgs"/> alone stays optional even when the other three are set.
    /// </summary>
    public string? AppInstallerUri { get; set; }
    public string? AppInstallPlatform { get; set; }
    public string? AppInstallArgs { get; set; }
    public string? AppInstallLaunchCommand { get; set; }
    public required string Purpose { get; set; }
    public required string Vision { get; set; }
    public required string Scope { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
