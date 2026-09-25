using System.Security.Cryptography;
using System.Text;

namespace Momos.Worker.Analysis;

/// <summary>
/// Deterministic ids for model elements and claims. The same observation gets the same id on every
/// analysis, which is what lets the Host carry a developer's correction over to the next model
/// version; hashing keeps ids safe inside a URL path or a file name whatever the repository's file
/// names are.
/// </summary>
public static class ModelIds
{
    public static string Component(string projectPath) => "cmp." + Hash(projectPath);

    public static string Claim(string canonical) => "clm." + Hash(canonical);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}
