namespace Momos.Worker.Agent;

/// <summary>
/// How much of its own conversation an analysis pass's agent keeps sending to the model.
/// </summary>
/// <param name="MaxCommandOutputChars">Characters of one command's output the agent gets back.</param>
/// <param name="MaxContextTokens">The model's context window in tokens.</param>
/// <param name="ProtectedToolTokens">Token room for the newest tool outputs, sent verbatim; older
/// outputs are replaced by short placeholders before each model call.</param>
public sealed record AnalysisContextOptions(int MaxCommandOutputChars, int MaxContextTokens, int ProtectedToolTokens);
