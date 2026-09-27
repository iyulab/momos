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

    /// <summary>The most chapters the overview pass may plan.</summary>
    public int MaxChapters { get; set; } = 12;

    /// <summary>Tokens one pass (the overview, or one chapter) may spend before that pass stops.</summary>
    public long MaxChapterTokens { get; set; } = 250_000;

    /// <summary>Tokens all passes of one analysis may spend together.</summary>
    public long MaxTotalTokens { get; set; } = 2_000_000;

    /// <summary>
    /// Wall-clock time the passes may take. Keep it below the Host's
    /// <c>Momos:Host:InspectionClaim:ReclaimTimeout</c> (30 minutes by default): the Host treats a
    /// request running longer than that as abandoned and hands it to another Worker. The Worker
    /// cannot read the Host's setting, so the two are kept apart by configuration.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(20);

    public static bool IsValid(AnalysisOptions o) =>
        o.MaxChapters > 0 && o.MaxChapterTokens > 0 && o.MaxTotalTokens > 0 && o.MaxDuration > TimeSpan.Zero;
}
