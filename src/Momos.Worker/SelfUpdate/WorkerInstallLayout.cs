using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Momos.Worker.SelfUpdate;

/// <summary>
/// Locates the root of an installed Worker. The install scripts lay one out as
/// <c>&lt;InstallRoot&gt;/installs/&lt;version&gt;/</c> for the binaries plus a
/// <c>&lt;InstallRoot&gt;/current</c> pointer they are launched through, and the operator's
/// settings file sits at <c>&lt;InstallRoot&gt;/appsettings.Production.json</c> — one level above
/// anything a versioned binary can reach with a content-root-relative lookup.
/// </summary>
public static class WorkerInstallLayout
{
    /// <summary>Name of the pointer directory that resolves to the version currently in service.</summary>
    public const string PointerDirectoryName = "current";

    /// <summary>Name of the directory each downloaded version is staged under.</summary>
    public const string VersionsDirectoryName = "installs";

    /// <summary>Settings file the install scripts write, and the source of truth for an installed Worker.</summary>
    public const string SettingsFileName = "appsettings.Production.json";

    /// <summary>
    /// Derives the install root from the directory the running binary sits in, or <see langword="null"/>
    /// when the binary is not running out of an installed layout at all (a plain <c>dotnet run</c>, a
    /// test host, an xcopy deployment).
    /// <para>
    /// This matches on directory names rather than resolving the pointer, because the two platforms
    /// disagree about what the running binary's directory even reports: on Linux it comes back fully
    /// resolved as <c>.../installs/&lt;version&gt;/</c>, while a Windows junction is not traversed back
    /// to its target and comes back as <c>.../current/</c> verbatim. Walking up for whichever of the two
    /// names appears first makes the answer identical on both, and does not depend on either platform
    /// behaving like the other.
    /// </para>
    /// </summary>
    public static string? ResolveInstallRoot(string baseDirectory)
    {
        for (var candidate = new DirectoryInfo(Path.GetFullPath(baseDirectory)); candidate is not null; candidate = candidate.Parent)
        {
            if (string.Equals(candidate.Name, VersionsDirectoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, PointerDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Parent?.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers the installed Worker's settings file as an extra configuration source, so a binary
    /// running out of a version directory still reads the operator's settings from the install root.
    /// Without it a fresh install has nowhere to find its Host URL and API key — the content root is
    /// the version directory, and only a self-update ever puts a copy of the file there. Does nothing
    /// when <paramref name="baseDirectory"/> is not inside an installed layout, and the file itself is
    /// optional, so an uninstalled run is unaffected.
    /// </summary>
    public static void AddInstalledSettings(IConfigurationBuilder configuration, string baseDirectory)
    {
        var installRoot = ResolveInstallRoot(baseDirectory);
        if (installRoot is null)
        {
            return;
        }

        var source = new JsonConfigurationSource
        {
            Path = Path.Combine(installRoot, SettingsFileName),
            Optional = true,
            ReloadOnChange = false,
        };
        source.ResolveFileProvider();

        // Inserted ahead of the environment-variable source instead of appended: later sources win,
        // and an operator setting Momos__Worker__Host__ApiKey expects that to override the file on
        // disk rather than be silently shadowed by it.
        var insertAt = configuration.Sources.Count;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                insertAt = i;
                break;
            }
        }

        configuration.Sources.Insert(insertAt, source);
    }
}
