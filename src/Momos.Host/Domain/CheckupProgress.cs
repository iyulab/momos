using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;

namespace Momos.Host.Domain;

/// <summary>Advances a checkup when the request behind one of its exams ends. Requests that no
/// exam run owns are ignored. Changes are only tracked here; the caller's SaveChangesAsync
/// persists them together with the request's own status change.</summary>
public static class CheckupProgress
{
    private const int MaxReason = 500;

    /// <summary>A design analysis that had to cut a manual chapter (or the outline or synthesis)
    /// is partial, with the recorded reasons; otherwise it is complete.</summary>
    public static ExamRunStatus DesignAnalysisOutcome(ProjectModel model, out string? reason)
    {
        var cut = (model.Coverage?.NotAnalyzed ?? [])
            .Where(g => g.Area.StartsWith("manual-", StringComparison.Ordinal))
            .Select(g => g.Reason)
            .ToList();
        reason = cut.Count == 0 ? null : Truncate(string.Join("; ", cut));
        return cut.Count == 0 ? ExamRunStatus.Completed : ExamRunStatus.Partial;
    }

    public static async Task OnModelSubmittedAsync(MomosDbContext db, InspectionRequest request, ProjectModel model, TimeProvider time, CancellationToken ct)
    {
        if (await ExamOfAsync(db, request.Id, ct) is not { } exam)
        {
            return;
        }

        exam.Status = DesignAnalysisOutcome(model, out var reason);
        exam.Reason = reason;
        exam.ProjectModelId = model.Id;
        var checkup = await db.Checkups.Include(c => c.Exams).SingleAsync(c => c.Id == exam.CheckupId, ct);
        checkup.BaseCommit = model.BaseCommit;
        checkup.ModelVersion = model.ModelVersion;
        Advance(checkup, time);
    }

    public static async Task OnRequestFailedAsync(MomosDbContext db, InspectionRequest request, string? reason, TimeProvider time, CancellationToken ct)
    {
        if (await ExamOfAsync(db, request.Id, ct) is not { } exam)
        {
            return;
        }

        exam.Status = ExamRunStatus.NotRun;
        exam.Reason = Truncate(string.IsNullOrWhiteSpace(reason) ? "The exam's request failed without a reason." : reason);
        Advance(await db.Checkups.Include(c => c.Exams).SingleAsync(c => c.Id == exam.CheckupId, ct), time);
    }

    private static Task<ExamRun?> ExamOfAsync(MomosDbContext db, Guid requestId, CancellationToken ct) =>
        db.ExamRuns.SingleOrDefaultAsync(e => e.RequestId == requestId, ct);

    private static void Advance(Checkup checkup, TimeProvider time)
    {
        if (checkup.Status == CheckupStatus.Running && checkup.Exams.All(e => e.Status != ExamRunStatus.Pending))
        {
            checkup.Status = CheckupStatus.Completed;
            checkup.CompletedAt = time.GetUtcNow();
        }
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxReason)
        {
            return text;
        }

        // Never end on half of a surrogate pair: a lone surrogate is not valid text for the database.
        var end = char.IsHighSurrogate(text[MaxReason - 1]) ? MaxReason - 1 : MaxReason;
        return text[..end];
    }
}
