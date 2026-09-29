namespace Momos.Worker.Agent;

/// <summary>
/// How much of its own conversation an analysis pass's agent keeps sending to the model.
/// </summary>
/// <param name="MaxCommandOutputChars">Characters of one command's output the agent gets back.</param>
/// <param name="MaxContextTokens">The model's context window in tokens.</param>
/// <param name="ProtectedToolRounds">Most recent tool rounds whose outputs are sent verbatim; older
/// outputs are replaced by short placeholders before each model call.</param>
public sealed record AnalysisContextOptions(int MaxCommandOutputChars, int MaxContextTokens, int ProtectedToolRounds);
