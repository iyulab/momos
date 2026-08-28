using System.ComponentModel;

namespace Momos.Worker.Execution;

/// <summary>
/// Exposes finding submission as a native in-process agent tool (mirrors
/// <see cref="CodeExecutionTools"/>'s <c>AIFunctionFactory.Create</c> wrapping) — the
/// agent calls this once per observed defect, citing output it actually saw from
/// <see cref="CodeExecutionTools.RunCommand"/>. Rejects an empty evidence string outright:
/// momos's 근거 기반 엄밀함 non-negotiable means a finding with no cited output is a guess,
/// not a finding.
/// </summary>
public sealed class FindingReportingTools(FindingSink sink)
{
    [Description(
        "Report one concrete finding (a functional defect or a UX inconsistency) observed " +
        "while inspecting the project. Only call this for an issue you actually reproduced " +
        "with RunCommand, citing its output as Evidence. Do not call this to report a clean " +
        "bill of health — if nothing was found, simply finish without calling it.")]
    public string ReportFinding(
        FindingCategory category,
        [Description("A concise description of the defect or inconsistency.")] string description,
        [Description("The actual RunCommand output (or reproduction steps plus that output) that supports this finding. Must not be empty.")] string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return "Rejected: Evidence must not be empty — cite the actual command output that supports this finding.";
        }

        sink.Add(new FindingPayload(category, description, evidence));
        return "Recorded.";
    }
}
