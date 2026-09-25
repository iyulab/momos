using System.Text.Json;
using CodeBeaker.Commands.Models;

namespace Momos.Worker.Execution;

/// <summary>
/// Reads a shell command's outcome out of a code-beaker <see cref="CommandResult"/> the same
/// way whichever runtime produced it. Runtimes disagree on the shape: one reports stdout as a
/// string result with success already derived from the exit code, another always reports
/// success and carries stdout, stderr and the exit code in an object. When an exit code is
/// present it decides success; otherwise the reported success and error are kept as they are.
/// </summary>
// TODO(upstream: code-beaker unified shell result): collapse to the typed result once a release that returns one shell result shape from every runtime is consumed.
internal static class ShellResultReader
{
    public readonly record struct ShellResult(bool Success, string? Stdout, string? Error, int? ExitCode);

    public static ShellResult Read(CommandResult result)
    {
        switch (result.Result)
        {
            case null:
                return new ShellResult(result.Success, null, result.Error, null);
            case string stdout:
                return new ShellResult(result.Success, stdout, result.Error, null);
        }

        var element = JsonSerializer.SerializeToElement(result.Result, result.Result.GetType());
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new ShellResult(result.Success, null, result.Error, null);
        }

        var stdoutText = ReadString(element, "stdout");
        var stderrText = ReadString(element, "stderr");
        var exitCode = ReadExitCode(element);

        if (exitCode is not { } code)
        {
            return new ShellResult(result.Success, stdoutText, result.Error ?? NullIfEmpty(stderrText), null);
        }

        var error = NullIfEmpty(stderrText) ?? (code != 0 ? $"exit code {code}" : null);
        return new ShellResult(code == 0, stdoutText, error, code);
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadExitCode(JsonElement element) =>
        TryGetProperty(element, "exitCode", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var code)
            ? (int)code
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind != JsonValueKind.Null)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
