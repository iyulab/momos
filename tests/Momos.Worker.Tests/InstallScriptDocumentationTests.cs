using System.Text.RegularExpressions;

namespace Momos.Worker.Tests;

/// <summary>
/// The install scripts are configured through environment variables, and the README is
/// the only place an operator learns their names. Nothing tied the two together: a
/// variable added to a script stays undiscoverable, and one renamed or removed leaves
/// the README pointing at a setting the scripts no longer read. These tests compare the
/// names each side mentions, in both directions.
/// </summary>
public sealed partial class InstallScriptDocumentationTests
{
    private static readonly string[] Scripts = ["install-worker.sh", "install-worker.ps1"];

    [Fact]
    public void EverySettingTheInstallScriptsRead_IsDocumentedInTheReadme()
    {
        var undocumented = ScriptSettings().Except(ReadmeSettings(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            undocumented.Count == 0,
            $"The install scripts read {string.Join(", ", undocumented)}, but the README never names "
            + "them — an operator has no way to find those settings.");
    }

    [Fact]
    public void EverySettingTheReadmeNames_IsReadByAnInstallScript()
    {
        var stale = ReadmeSettings().Except(ScriptSettings(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            $"The README tells operators to set {string.Join(", ", stale)}, but no install script "
            + "reads them — setting them does nothing.");
    }

    private static HashSet<string> ScriptSettings()
    {
        var root = RepositoryRoot();
        var names = Scripts
            .SelectMany(script => SettingName().Matches(File.ReadAllText(Path.Combine(root.FullName, "scripts", script))))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        // A pattern that stops matching would make both tests pass vacuously.
        Assert.NotEmpty(names);
        return names;
    }

    private static HashSet<string> ReadmeSettings() =>
        SettingName().Matches(File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "README.md")))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\bMOMOS_[A-Z0-9_]*[A-Z0-9]\b")]
    private static partial Regex SettingName();

    /// <summary>
    /// Walks up from the test binaries until the directory holding the install scripts
    /// appears, so the suite works wherever it runs from.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", Scripts[0])))
            {
                return dir;
            }
        }

        throw new InvalidOperationException(
            $"No ancestor of '{AppContext.BaseDirectory}' contains 'scripts/{Scripts[0]}'.");
    }
}
