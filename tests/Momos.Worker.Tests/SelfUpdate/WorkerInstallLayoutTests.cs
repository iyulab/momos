using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Momos.Worker.SelfUpdate;

namespace Momos.Worker.Tests.SelfUpdate;

/// <summary>
/// Covers the two directory shapes an installed Worker can report as the directory its binary sits
/// in. They differ by platform — Linux resolves the pointer back to the version directory it
/// targets, Windows does not traverse a junction and reports the pointer path verbatim — so the
/// layout has to answer the same for both without either being able to observe the other's shape.
/// </summary>
public sealed class WorkerInstallLayoutTests : IDisposable
{
    private readonly string _installRoot = Directory.CreateTempSubdirectory("momos-worker-layout-test").FullName;

    public void Dispose() => Directory.Delete(_installRoot, recursive: true);

    [Fact]
    public void ResolveInstallRoot_FromAResolvedVersionDirectory_ReturnsTheDirectoryHoldingInstalls()
    {
        // Trailing separator included on purpose: AppContext.BaseDirectory always carries one.
        var baseDirectory = Path.Combine(_installRoot, WorkerInstallLayout.VersionsDirectoryName, "0.2.0")
            + Path.DirectorySeparatorChar;

        Assert.Equal(_installRoot, WorkerInstallLayout.ResolveInstallRoot(baseDirectory));
    }

    [Fact]
    public void ResolveInstallRoot_FromAnUntraversedPointerDirectory_ReturnsTheDirectoryHoldingThePointer()
    {
        var baseDirectory = Path.Combine(_installRoot, WorkerInstallLayout.PointerDirectoryName)
            + Path.DirectorySeparatorChar;

        Assert.Equal(_installRoot, WorkerInstallLayout.ResolveInstallRoot(baseDirectory));
    }

    [Fact]
    public void ResolveInstallRoot_OutsideAnInstalledLayout_ReturnsNull()
    {
        var baseDirectory = Path.Combine(_installRoot, "app", "bin") + Path.DirectorySeparatorChar;

        Assert.Null(WorkerInstallLayout.ResolveInstallRoot(baseDirectory));
    }

    [Fact]
    public void AddInstalledSettings_FromAVersionDirectory_ReadsTheSettingsFileOneLevelUp()
    {
        WriteInstalledSettings("""{"Momos":{"Worker":{"Host":{"BaseUrl":"https://host.example.invalid"}}}}""");
        var baseDirectory = Path.Combine(_installRoot, WorkerInstallLayout.VersionsDirectoryName, "0.2.0")
            + Path.DirectorySeparatorChar;

        var configuration = new ConfigurationManager();
        WorkerInstallLayout.AddInstalledSettings(configuration, baseDirectory);

        // The value has to arrive through the configuration itself, not just through a source
        // sitting in the list: a source added to an already-built ConfigurationManager only takes
        // effect if adding it reloads the providers.
        Assert.Equal("https://host.example.invalid", configuration["Momos:Worker:Host:BaseUrl"]);
    }

    [Fact]
    public void AddInstalledSettings_WithEnvironmentVariablesAlreadyRegistered_LeavesThemWinning()
    {
        const string variable = "Momos__Worker__Test__InstalledSettingsProbe";
        const string key = "Momos:Worker:Test:InstalledSettingsProbe";
        WriteInstalledSettings("""{"Momos":{"Worker":{"Test":{"InstalledSettingsProbe":"from-file"}}}}""");

        Environment.SetEnvironmentVariable(variable, "from-environment");
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables();
            IConfigurationBuilder builder = configuration;

            WorkerInstallLayout.AddInstalledSettings(
                builder,
                Path.Combine(_installRoot, WorkerInstallLayout.PointerDirectoryName) + Path.DirectorySeparatorChar);

            // Later sources win, so appending the file would silently override whatever an operator
            // set in the environment — the file has to land ahead of the environment source.
            var fileIndex = IndexOfSource<JsonConfigurationSource>(builder);
            var environmentIndex = IndexOfSource<EnvironmentVariablesConfigurationSource>(builder);
            Assert.True(fileIndex >= 0, "the installed settings file was never registered as a source");
            Assert.True(fileIndex < environmentIndex, "the installed settings file was registered after the environment variables");
            Assert.Equal("from-environment", configuration[key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void AddInstalledSettings_OutsideAnInstalledLayout_RegistersNothing()
    {
        var configuration = new ConfigurationManager();
        IConfigurationBuilder builder = configuration;
        var before = builder.Sources.Count;

        WorkerInstallLayout.AddInstalledSettings(builder, Path.Combine(_installRoot, "app", "bin") + Path.DirectorySeparatorChar);

        Assert.Equal(before, builder.Sources.Count);
    }

    private void WriteInstalledSettings(string json) =>
        File.WriteAllText(Path.Combine(_installRoot, WorkerInstallLayout.SettingsFileName), json);

    private static int IndexOfSource<TSource>(IConfigurationBuilder builder)
    {
        for (var i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is TSource)
            {
                return i;
            }
        }

        return -1;
    }
}
