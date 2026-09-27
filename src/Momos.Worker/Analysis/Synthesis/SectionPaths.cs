using System.Text.RegularExpressions;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>
/// A chapter's file name, derived from its title rather than chosen by the model: a lowercase
/// slug at the report's root, never one of the report's reserved pages, unique within the model.
/// Deriving it means a model cannot propose a path the Host would reject.
/// </summary>
public static partial class SectionPaths
{
    private const int MaxSlugLength = 50;

    public static string FromTitle(string title, ISet<string> taken)
    {
        var slug = NotSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        if (slug.Length == 0)
        {
            slug = "chapter";
        }

        if (slug is "index" or "unknowns")
        {
            slug += "-chapter";
        }

        var candidate = $"{slug}.md";
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{slug}-{n}.md";
        }

        return candidate;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotSlug();
}
