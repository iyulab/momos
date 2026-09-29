using Momos.Host.Domain;

namespace Momos.Host.Reporting;

/// <summary>
/// The fixed text of a checkup report's own pages, in one of <see cref="ReportLanguage.Supported"/>.
/// Only the report's own sentences live here; free text (project names, reasons, chapter titles)
/// is passed in already escaped and is never translated.
/// </summary>
internal sealed record ReportText
{
    public required Func<string, string> Title { get; init; }

    /// <summary>The line under the title: commit (a code span), date, report language.</summary>
    public required Func<string, string, string, string> Header { get; init; }

    public required string DefaultBranch { get; init; }

    public required string ExamStatusHeading { get; init; }
    public required string LimitsHeading { get; init; }
    public required string LimitsIntro { get; init; }
    public required string NoLimits { get; init; }

    /// <summary>Program, then its reason — already escaped, or null.</summary>
    public required Func<string, string?, string> DidNotRun { get; init; }

    public required Func<string, string?, string> RanInPart { get; init; }
    public required Func<string, string> NotFinished { get; init; }

    /// <summary>The number of chapters, then the markdown link to the unknowns page.</summary>
    public required Func<int, string, string> ChaptersNotAnalyzed { get; init; }

    public required Func<int, string> ChaptersPartial { get; init; }
    public required Func<string, string> ProgramsNotIncluded { get; init; }

    public required string AppendicesHeading { get; init; }
    public required string ResultsTitle { get; init; }
    public required string FollowUpsTitle { get; init; }
    public required string ManualTitle { get; init; }
    public required string UnknownsTitle { get; init; }
    public required string EvidenceTitle { get; init; }
    public required Func<string, string> ExamRecordTitle { get; init; }

    /// <summary>The markdown link target of the summary page, relative to the page it is on.</summary>
    public required Func<string, string> BackToSummary { get; init; }

    public required string ResultsIntro { get; init; }
    public required string ProgramColumn { get; init; }
    public required string StatusColumn { get; init; }
    public required string DetailColumn { get; init; }
    public required string NotIncluded { get; init; }
    public required Func<ExamRunStatus, string> Status { get; init; }

    /// <summary>Chapters written, then chapters in the outline.</summary>
    public required Func<int, int, string> ChaptersWritten { get; init; }

    public required string NoFollowUps { get; init; }

    public required string RequestLabel { get; init; }
    public required string ReasonLabel { get; init; }
    public required string ModelVersionLabel { get; init; }
    public required string CommitLabel { get; init; }
    public required string NoModel { get; init; }
    public required string NoCoverage { get; init; }
    public required string AnalyzedHeading { get; init; }
    public required string NotAnalyzedHeading { get; init; }
    public required string RejectedHeading { get; init; }
    public required string CountColumn { get; init; }
    public required string NoneRecorded { get; init; }
    public required string NoChapters { get; init; }

    public static ReportText For(string language) => language switch
    {
        "en" => English,
        "ko" => Korean,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "No report text for this language."),
    };

    private static string WithReason(string sentence, string? reason) =>
        reason is null ? $"{sentence}." : $"{sentence}: {reason.TrimEnd('.')}.";

    private static readonly ReportText English = new()
    {
        Title = project => $"{project} checkup results",
        Header = (commit, date, language) => $"Commit {commit} · {date} · report language `{language}`.",
        DefaultBranch = "the default branch",
        ExamStatusHeading = "Exam status",
        LimitsHeading = "Limits of this checkup",
        LimitsIntro = "What this checkup did not look at, or did not finish. None of this says those parts are sound — only that this checkup does not know them.",
        NoLimits = "Every program in this checkup ran to the end.",
        DidNotRun = (program, reason) => WithReason($"`{program}` did not run", reason),
        RanInPart = (program, reason) => WithReason($"`{program}` ran only in part", reason),
        NotFinished = program => $"`{program}` has not finished.",
        ChaptersNotAnalyzed = (n, link) => $"{n} chapter{(n == 1 ? " was" : "s were")} not analyzed — see {link}.",
        ChaptersPartial = n => $"{n} chapter{(n == 1 ? " was" : "s were")} written only in part.",
        ProgramsNotIncluded = programs => $"Not included in this checkup: {programs}.",
        AppendicesHeading = "Appendices",
        ResultsTitle = "Exam results",
        FollowUpsTitle = "Follow-ups",
        ManualTitle = "Engineering manual",
        UnknownsTitle = "What the design analysis does not know",
        EvidenceTitle = "Evidence: every component and claim",
        ExamRecordTitle = program => $"Exam record: {program}",
        BackToSummary = summary => $"Back to the [summary]({summary}).",
        ResultsIntro = "Every exam program and how it ended in this checkup. A program the checkup did not include is listed as such, so its absence is not read as a clean result.",
        ProgramColumn = "Program",
        StatusColumn = "Status",
        DetailColumn = "Detail",
        NotIncluded = "Not included in this checkup",
        Status = status => status switch
        {
            ExamRunStatus.Completed => "Completed",
            ExamRunStatus.Partial => "Partial",
            ExamRunStatus.NotRun => "Not run",
            _ => "Not finished",
        },
        ChaptersWritten = (written, total) => $"{written} of {total} chapters written",
        NoFollowUps = "No follow-ups — the programs in this checkup do not raise findings.",
        RequestLabel = "Request",
        ReasonLabel = "Reason",
        ModelVersionLabel = "Project model version",
        CommitLabel = "Commit",
        NoModel = "This exam produced no project model.",
        NoCoverage = "This model version records no analysis coverage, so what the analysis left unread is unknown.",
        AnalyzedHeading = "Analyzed",
        NotAnalyzedHeading = "Not analyzed",
        RejectedHeading = "Discarded during analysis, by reason",
        CountColumn = "Count",
        NoneRecorded = "None recorded.",
        NoChapters = "The design analysis wrote no chapters for this model version.",
    };

    private static readonly ReportText Korean = new()
    {
        Title = project => $"{project} 검진 결과",
        Header = (commit, date, language) => $"커밋 {commit} · {date} · 결과지 언어 `{language}`.",
        DefaultBranch = "기본 브랜치",
        ExamStatusHeading = "검사 현황",
        LimitsHeading = "이번 검진의 한계",
        LimitsIntro = "이번 검진이 보지 않았거나 끝내지 못한 것이다. 어느 것도 그곳에 문제가 없다는 뜻이 아니다 — 이번 검진이 알지 못한다는 뜻이다.",
        NoLimits = "이 검진의 모든 프로그램이 끝까지 실행됐다.",
        DidNotRun = (program, reason) => WithReason($"`{program}`이(가) 실행되지 않았다", reason),
        RanInPart = (program, reason) => WithReason($"`{program}`이(가) 일부만 실행됐다", reason),
        NotFinished = program => $"`{program}`이(가) 아직 끝나지 않았다.",
        ChaptersNotAnalyzed = (n, link) => $"{n}개 장을 분석하지 못했다 — {link} 참고.",
        ChaptersPartial = n => $"{n}개 장은 일부만 작성됐다.",
        ProgramsNotIncluded = programs => $"이 검진에 포함되지 않음: {programs}.",
        AppendicesHeading = "부록",
        ResultsTitle = "검사 결과",
        FollowUpsTitle = "재검 권고",
        ManualTitle = "엔지니어링 매뉴얼",
        UnknownsTitle = "설계 분석이 모르는 것",
        EvidenceTitle = "근거: 모든 구성 요소와 진술",
        ExamRecordTitle = program => $"검사 원자료: {program}",
        BackToSummary = summary => $"[요약]({summary})으로 돌아가기.",
        ResultsIntro = "검사 프로그램마다 이번 검진에서 어떻게 끝났는지를 적는다. 이 검진에 포함되지 않은 프로그램도 그렇다고 적어, 빠진 것을 이상 없음으로 읽지 않게 한다.",
        ProgramColumn = "프로그램",
        StatusColumn = "상태",
        DetailColumn = "내용",
        NotIncluded = "이 검진에 포함되지 않음",
        Status = status => status switch
        {
            ExamRunStatus.Completed => "완료",
            ExamRunStatus.Partial => "부분 완료",
            ExamRunStatus.NotRun => "미시행",
            _ => "미완",
        },
        ChaptersWritten = (written, total) => $"장 {total}개 중 {written}개 작성",
        NoFollowUps = "재검 권고 없음 — 이 검진의 프로그램은 지적을 내지 않는다.",
        RequestLabel = "요청",
        ReasonLabel = "사유",
        ModelVersionLabel = "프로젝트 모델 버전",
        CommitLabel = "커밋",
        NoModel = "이 검사는 프로젝트 모델을 만들지 않았다.",
        NoCoverage = "이 모델 버전은 분석 범위를 기록하지 않아, 분석이 읽지 않은 곳을 알 수 없다.",
        AnalyzedHeading = "분석한 영역",
        NotAnalyzedHeading = "분석하지 못한 영역",
        RejectedHeading = "분석 중 버린 진술(사유별)",
        CountColumn = "수",
        NoneRecorded = "기록 없음.",
        NoChapters = "이 모델 버전에는 설계 분석이 쓴 장이 없다.",
    };
}
