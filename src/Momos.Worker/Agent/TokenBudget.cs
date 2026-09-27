namespace Momos.Worker.Agent;

/// <summary>
/// A token allowance for the model calls made inside a scope, optionally nested in a larger one:
/// every token charged to a budget is charged to its parent too, and a budget is spent once it or
/// any ancestor is. <see cref="UsageLimitingChatClient"/> enforces whichever budget is current, so
/// a caller bounds a stretch of work by entering a budget around it — the per-loop
/// <c>UsageLimiter</c> reset that inspections rely on cannot carry an allowance across loops.
/// </summary>
public sealed class TokenBudget
{
    private static readonly AsyncLocal<TokenBudget?> Ambient = new();

    private readonly TokenBudget? _parent;
    private long _used;

    public TokenBudget(long limit, TokenBudget? parent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Limit = limit;
        _parent = parent;
    }

    /// <summary>The budget model calls on this async flow are charged to, or null outside any scope.</summary>
    public static TokenBudget? Current => Ambient.Value;

    public long Limit { get; }

    public long Used => Interlocked.Read(ref _used);

    public bool IsExhausted => Used >= Limit || _parent?.IsExhausted == true;

    /// <summary>Makes <paramref name="budget"/> current until the returned scope is disposed, then
    /// restores whatever was current before.</summary>
    public static IDisposable Enter(TokenBudget budget)
    {
        var previous = Ambient.Value;
        Ambient.Value = budget;
        return new Scope(previous);
    }

    public void Charge(long tokens)
    {
        Interlocked.Add(ref _used, tokens);
        _parent?.Charge(tokens);
    }

    private sealed class Scope(TokenBudget? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
