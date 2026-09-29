using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Agent;

/// <summary>
/// Watches one analysis pass's tool calls. It counts every call and every failed call by tool, so
/// a pass that proposed nothing can be told apart from one whose proposals broke before any rule
/// saw them. A call that throws — arguments the tool cannot bind, most often — is answered with
/// what went wrong instead of function invocation's generic failure text, which gives the agent
/// nothing to correct. And after <c>readsBeforeNudge</c> reading calls in a row it appends a
/// reminder to propose what was found: a pass cut by its budget keeps only what it proposed.
/// </summary>
public sealed class AnalysisToolMonitor(int readsBeforeNudge, ILogger? logger = null)
{
    /// <summary>Tools that read the repository or the knowledge index and change nothing.</summary>
    public static IReadOnlySet<string> ReadingTools { get; } = new HashSet<string>(StringComparer.Ordinal) { "RunCommand", "QueryProjectKnowledge" };

    private readonly ConcurrentDictionary<string, (int Calls, int Failed)> _counts = new(StringComparer.Ordinal);
    private int _readsInARow;

    public int Calls => _counts.Values.Sum(c => c.Calls);

    public int Failed => _counts.Values.Sum(c => c.Failed);

    public int CallsTo(string tool) => _counts.TryGetValue(tool, out var c) ? c.Calls : 0;

    public int FailedCallsTo(string tool) => _counts.TryGetValue(tool, out var c) ? c.Failed : 0;

    /// <summary>Calls by tool, such as <c>RunCommand 12, ProposeClaim 3 (1 failed)</c>.</summary>
    public string Summary() => _counts.IsEmpty
        ? "none"
        : string.Join(", ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c =>
            c.Value.Failed == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value.Calls}")
                : string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value.Calls} ({c.Value.Failed} failed)")));

    public IReadOnlyList<AIFunction> Watch(IEnumerable<AIFunction> tools) => [.. tools.Select(t => new Watched(t, this))];

    private object? Completed(string tool, object? result)
    {
        _counts.AddOrUpdate(tool, (1, 0), (_, c) => (c.Calls + 1, c.Failed));
        if (!ReadingTools.Contains(tool))
        {
            Interlocked.Exchange(ref _readsInARow, 0);
            return result;
        }

        var reads = Interlocked.Increment(ref _readsInARow);
        if (reads < readsBeforeNudge || AsText(result) is not { } text)
        {
            return result;
        }

        Interlocked.Exchange(ref _readsInARow, 0);
        return text + string.Create(CultureInfo.InvariantCulture,
            $"\n\n[momos] {reads} reads in a row without a proposal. Propose what you have found so far before reading more: when this pass runs out of time or tokens it keeps only what you proposed.");
    }

    private string Threw(string tool, Exception ex)
    {
        _counts.AddOrUpdate(tool, (1, 1), (_, c) => (c.Calls + 1, c.Failed + 1));
        Interlocked.Exchange(ref _readsInARow, 0);
        // The type and, for JSON, the path of the bad argument — never the message, which may
        // quote the argument's value and so the repository.
        var where = ex is JsonException { Path: { } path } ? $" at {path}" : string.Empty;
        logger?.LogWarning("{Tool} call failed before it ran — {Exception}{Where}", tool, ex.GetType().Name, where);
        return $"Rejected: this call could not be carried out ({ex.GetType().Name}{where}: {ex.Message}). Check the argument names and types against the tool's schema and call again.";
    }

    private static string? AsText(object? result) => result switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private sealed class Watched(AIFunction inner, AnalysisToolMonitor monitor) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            object? result;
            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return monitor.Threw(Name, ex);
            }

            return monitor.Completed(Name, result);
        }
    }
}
