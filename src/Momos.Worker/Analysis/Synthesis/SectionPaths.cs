using System.Text.RegularExpressions;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>
/// A chapter's file name, derived from its title rather than chosen by the model: a lowercase
/// slug at the report's root, never one of the report's reserved pages, unique within the model.
/// Deriving it means a model cannot propose a path the Host would reject. A title the slug cannot
/// hold — one written wholly in a non-Latin script — takes the name of the guide chapter it follows.
/// </summary>
public static partial class SectionPaths
{
    private const int MaxSlugLength = 50;

    public static string FromTitle(string title, string? guide, ISet<string> taken)
    {
        var slug = Slug(title);
        if (slug.Length == 0)
        {
            slug = Slug(guide ?? string.Empty);
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

    private static string Slug(string text)
    {
        var slug = NotSlug().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length > MaxSlugLength ? slug[..MaxSlugLength].TrimEnd('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotSlug();
}
