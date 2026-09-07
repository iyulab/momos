using System.Text.Json;

namespace Momos.Host.Tests;

/// <summary>
/// The .NET generation the Host runs on is written down in four places: the target
/// framework every project builds against, the SDK pin CI provisions itself from, and
/// the two stages of the container image. Nothing made them move together.
///
/// The sharp failure is the runtime stage falling behind. Raising the target framework
/// and the build stage together still restores, builds and publishes, so CI stays
/// green — the image only fails when the container starts, which is after everything
/// that would have caught it. These tests read the files themselves, so that gap closes
/// before a deployment finds it.
///
/// Support is the other half of the same question. A pin that nobody touches stops
/// receiving fixes on a date rather than on a commit, and dependency updates report
/// only that a newer tag exists, never that the current one went out of support. The
/// window below is recorded here so the check needs no network and no Docker daemon.
/// </summary>
public sealed class HostRuntimeGenerationTests
{
    /// <summary>
    /// The date the pinned generation stops being known-supported, and where that date
    /// came from. A failure against it is not a flake: confirm upstream support, then
    /// either move the pins to a supported generation or extend the window with a fresh
    /// upstream date.
    /// </summary>
    private static readonly (string Generation, DateOnly ReviewBy, string Basis)[] SupportWindows =
    [
        ("10.0", new DateOnly(2028, 11, 14),
            ".NET 10 end-of-life, per endoflife.date as recorded on 2026-09-07"),
    ];

    [Fact]
    public void BuildAndRuntimeStages_PinTheSameGeneration()
    {
        var (build, runtime) = ImageStages();

        Assert.True(
            GenerationOf(build) == GenerationOf(runtime),
            $"The image builds on '{build}' and runs on '{runtime}'. Publishing with one generation "
            + "and running on another is not reported by any build step — the container fails when it "
            + "starts, long after CI has gone green.");
    }

    [Fact]
    public void TheRuntimeStage_ServesTheFrameworkTheAppIsBuiltFor()
    {
        var (_, runtime) = ImageStages();
        var target = TargetFrameworkGeneration();

        Assert.True(
            GenerationOf(runtime) == target,
            $"Every project targets net{target}, but the image runs on '{runtime}'. A runtime older "
            + "than the assemblies it loads fails at container start, and nothing before that says so.");
    }

    [Fact]
    public void TheSdkPin_CoversTheFrameworkTheAppIsBuiltFor()
    {
        var sdk = SdkPinGeneration();
        var target = TargetFrameworkGeneration();

        Assert.True(
            sdk == target,
            $"global.json pins the {sdk} SDK while every project targets net{target}. CI provisions "
            + "itself from global.json, so a mismatch decides which of the two a contributor's build "
            + "actually follows.");
    }

    [Fact]
    public void ThePinnedGeneration_HasARecordedSupportWindow()
    {
        var target = TargetFrameworkGeneration();

        Assert.True(
            SupportWindows.Any(w => w.Generation == target),
            $"The projects now target net{target}, so this suite needs that generation's support "
            + "window before it can vouch for it — add it to SupportWindows with the upstream date "
            + "it came from.");
    }

    [Fact]
    public void ThePinnedGeneration_IsNotPastItsSupportWindow()
    {
        var target = TargetFrameworkGeneration();
        var window = SupportWindows.SingleOrDefault(w => w.Generation == target);

        // ThePinnedGeneration_HasARecordedSupportWindow is what reports a missing entry.
        if (window.Basis is null)
        {
            return;
        }

        Assert.True(
            DateOnly.FromDateTime(DateTime.UtcNow) < window.ReviewBy,
            $"net{target} was only established as supported through {window.ReviewBy:yyyy-MM-dd} "
            + $"({window.Basis}). Running production on a generation that no longer receives fixes is "
            + "the state this check exists to prevent: confirm upstream support, then move the pins or "
            + "extend the window with a fresh upstream date.");
    }

    /// <summary>
    /// The image the Host is built with and the one it runs on, in that order. Both are
    /// declared as <c>FROM &lt;image&gt; AS &lt;stage&gt;</c>.
    /// </summary>
    private static (string Build, string Runtime) ImageStages()
    {
        var froms = File
            .ReadLines(Path.Combine(RepositoryRoot().FullName, "src", "Momos.Host", "Dockerfile"))
            .Where(l => l.StartsWith("FROM ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToArray();

        Assert.True(
            froms.Length == 2,
            $"This guard reads a two-stage build; the Dockerfile declares {froms.Length} stage(s). "
            + "If the image genuinely changed shape, teach the guard the new one rather than leaving "
            + "it to pass over whatever it happens to find.");

        return (froms[0], froms[1]);
    }

    /// <summary>
    /// The tag's leading component: <c>10.0</c> stays <c>10.0</c>, and a variant suffix
    /// such as <c>10.0-alpine</c> names a base OS, not a generation.
    /// </summary>
    private static string GenerationOf(string image)
    {
        var tag = image[(image.LastIndexOf(':') + 1)..];
        return tag.Split('-')[0];
    }

    /// <summary>
    /// <c>net10.0</c> → <c>10.0</c>. Directory.Build.props is the single source every
    /// project inherits its target framework from.
    /// </summary>
    private static string TargetFrameworkGeneration()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "Directory.Build.props"));
        var open = props.IndexOf("<TargetFramework>", StringComparison.Ordinal);

        Assert.True(open >= 0, "Directory.Build.props is where every project's target framework comes from.");

        var start = open + "<TargetFramework>".Length;
        var end = props.IndexOf("</TargetFramework>", start, StringComparison.Ordinal);

        return props[start..end].Trim()["net".Length..];
    }

    /// <summary>
    /// <c>10.0.100</c> → <c>10.0</c>. The feature band is deliberately dropped: it moves
    /// on its own schedule and says nothing about which generation is in use.
    /// </summary>
    private static string SdkPinGeneration()
    {
        using var json = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "global.json")));

        var version = json.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        var parts = version.Split('.');

        return $"{parts[0]}.{parts[1]}";
    }

    /// <summary>
    /// Walks up from the test binaries until the directory holding the Host project
    /// appears. Resolving from <see cref="AppContext.BaseDirectory"/> keeps this working
    /// wherever the suite runs from, unlike a path relative to the caller.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Momos.Host", "Dockerfile")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException(
            $"No ancestor of '{AppContext.BaseDirectory}' contains 'src/Momos.Host/Dockerfile'.");
    }
}
