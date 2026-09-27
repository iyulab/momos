using System.Security.Cryptography;
using System.Text;

namespace Momos.Worker.Analysis;

/// <summary>
/// Deterministic ids for model elements and claims. The same observation gets the same id on every
/// analysis, which is what lets the Host carry a developer's correction over to the next model
/// version; hashing keeps ids safe inside a URL path or a file name whatever the repository's file
/// names are. Claims and elements a language model proposes are keyed by the topic it names, so
/// restating the same topic next time yields the same key.
/// </summary>
public static class ModelIds
{
    public static string Component(string projectPath) => "cmp." + Hash(projectPath);

    public static string Claim(string canonical) => "clm." + Hash(canonical);

    public const string FlowPrefix = "flw";
    public const string InvariantPrefix = "inv";
    public const string PatternPrefix = "pat";
    public const string DecisionPrefix = "dec";
    public const string IntentPrefix = "int";

    /// <summary>A proposed claim's key. The "synth|" namespace keeps it apart from every
    /// deterministic key, whatever topic a model names.</summary>
    public static string SynthesizedClaim(string topic) => "clm." + Hash("synth|" + NormalizeTopic(topic));

    public static string Element(string prefix, string topic) => $"{prefix}." + Hash($"synth|{prefix}|{NormalizeTopic(topic)}");

    public static string Section(string path) => "sec." + Hash("section|" + path);

    /// <summary>Lower case, single spaces: a model restating a topic rarely repeats its exact casing
    /// or spacing, and should still land on the same key.</summary>
    public static string NormalizeTopic(string topic) =>
        string.Join(' ', topic.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}
