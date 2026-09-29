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
    public long MaxChapterTokens { get; set; } = 250_000;

    /// <summary>Tokens the overview pass may spend before it stops. The overview surveys the whole
    /// repository once to plan the chapters, so it needs more than one chapter does; it still counts
    /// against <see cref="MaxTotalTokens"/>.</summary>
    public long MaxOverviewTokens { get; set; } = 400_000;

    /// <summary>Tokens all passes of one analysis may spend together.</summary>
    public long MaxTotalTokens { get; set; } = 2_000_000;

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

    /// <summary>
    /// Wall-clock time the passes may take. Keep it below the Host's
    /// <c>Momos:Host:InspectionClaim:ReclaimTimeout</c> (30 minutes by default): the Host treats a
    /// request running longer than that as abandoned and hands it to another Worker. The Worker
    /// cannot read the Host's setting, so the two are kept apart by configuration.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(20);

    public static bool IsValid(AnalysisOptions o) =>
        o.MaxChapters > 0 && o.MaxCommandOutputChars > 0 && o.MaxChapterTokens > 0 && o.MaxOverviewTokens > 0 && o.MaxTotalTokens > 0 && o.MaxDuration > TimeSpan.Zero
        && o.MaxContextTokens > 0 && o.ProtectedToolRounds > 0;
}
