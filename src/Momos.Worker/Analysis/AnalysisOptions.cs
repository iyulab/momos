namespace Momos.Worker.Analysis;

/// <summary>
/// Limits and switches for an analysis request's language-model passes (the deterministic
/// extraction always runs). Separate from <see cref="Momos.Worker.Agent.AgentLoopLimitsOptions"/>,
/// which bounds one inspection's agent loop.
/// </summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Momos:Worker:Analysis";

    /// <summary>When false, an analysis submits the deterministic model only.</summary>
    public bool Synthesis { get; set; } = true;

    /// <summary>The model the analysis passes ask for; empty means the provider's configured model.</summary>
    public string? Model { get; set; }

    /// <summary>The most chapters the overview pass may plan. The overview tends to plan up to this
    /// limit, and each chapter gets an equal share of <see cref="MaxDuration"/>, so a high limit
    /// leaves every chapter too little time.</summary>
    public int MaxChapters { get; set; } = 8;

    /// <summary>Tokens one chapter pass may spend before that pass stops.</summary>
    public long MaxChapterTokens { get; set; } = 500_000;

    /// <summary>Tokens the overview pass may spend before it stops. The overview surveys the whole
    /// repository once to plan the chapters, so it needs more than one chapter does; it still counts
    /// against <see cref="MaxTotalTokens"/>.</summary>
    public long MaxOverviewTokens { get; set; } = 400_000;

    /// <summary>Tokens all passes of one analysis may spend together. The default is
    /// <see cref="MaxOverviewTokens"/> plus <see cref="MaxChapters"/> times <see cref="MaxChapterTokens"/>,
    /// so the per-pass limits can all be reached without the total cutting a pass short; keep that
    /// relation when raising one of them.</summary>
    public long MaxTotalTokens { get; set; } = 4_400_000;

    /// <summary>Characters of one command's output the analysis agent gets back; the rest is cut
    /// with a note telling it to read a narrower part. The outputs of the last
    /// <see cref="ProtectedToolRounds"/> rounds are re-sent in full on each later model call, so this
    /// bounds how fast a pass spends its tokens.</summary>
    public int MaxCommandOutputChars { get; set; } = 8_000;

    /// <summary>The model's context window in tokens. Needed because a self-hosted model is not in
    /// the agent library's catalog; without it compaction works against a guess.</summary>
    public int MaxContextTokens { get; set; } = 32_000;

    /// <summary>Most recent tool rounds whose outputs the agent keeps verbatim; older outputs are
    /// replaced by short placeholders before each model call. Every output otherwise rides along
    /// on every later call, which is what exhausted chapter budgets.</summary>
    public int ProtectedToolRounds { get; set; } = 4;

    /// <summary>Reading calls (commands, knowledge queries) in a row after which the agent is
    /// reminded to propose what it found. Left alone, an agent reads a chapter's whole subject
    /// before writing anything, and a pass cut by its budget keeps only what was proposed.</summary>
    public int ReadsBeforeNudge { get; set; } = 6;

    /// <summary>
    /// Wall-clock time the passes may take. The default is 2 minutes for the clone and deterministic
    /// extraction, 3 for the overview and 5 for each of the default 8 chapters — sized from measured
    /// chapter passes, to be recalibrated by the next measurement. Keep it a quarter hour or more
    /// below the Host's <c>Momos:Host:InspectionClaim:ReclaimTimeout</c> (60 minutes by default): the
    /// Host treats a request running longer than that as abandoned and hands it to another Worker.
    /// The Worker cannot read the Host's setting, so the two are kept apart by configuration.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(45);

    public static bool IsValid(AnalysisOptions o) =>
        o.MaxChapters > 0 && o.MaxCommandOutputChars > 0 && o.MaxChapterTokens > 0 && o.MaxOverviewTokens > 0 && o.MaxTotalTokens > 0 && o.MaxDuration > TimeSpan.Zero
        && o.MaxContextTokens > 0 && o.ProtectedToolRounds > 0 && o.ReadsBeforeNudge > 0;
}
