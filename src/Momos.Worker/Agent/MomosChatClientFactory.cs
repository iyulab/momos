using IronHive.Agent.Context;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Agent;

/// <summary>
/// Builds momos's chat-client pipeline over the registered <see cref="IChatClientProvider"/>s, with
/// an optional per-tool-round context reducer.
/// </summary>
/// <remarks>
/// <see cref="IChatClientFactory"/> takes a single decorator fixed when the factory is built, so it
/// cannot place a reducer bound to one loop's <see cref="ContextManager"/> inside the function
/// invocation of that loop's client alone. This type owns the pipeline instead: <see cref="For"/>
/// hands out an <see cref="IChatClientFactory"/> whose clients carry the given reducer — or none,
/// which is what the registered <see cref="IChatClientFactory"/> is — so provider resolution stays
/// the library's and the pipeline is written once.
/// </remarks>
public sealed class MomosChatClientFactory
{
    private readonly IReadOnlyDictionary<string, IChatClientProvider> _providers;
    private readonly IChatClientProvider _defaultProvider;
    private readonly Func<IChatClient, ContextManager?, IChatClient> _build;

    /// <param name="providers">The providers by name; the first is the default.</param>
    /// <param name="build">Builds the pipeline over a provider's raw client. It must place a non-null
    /// tool-round context inside function invocation, so the reduction runs on every tool round of
    /// a turn and not only on the turn's first model call.</param>
    public MomosChatClientFactory(
        IReadOnlyDictionary<string, IChatClientProvider> providers,
        Func<IChatClient, ContextManager?, IChatClient> build)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(build);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No {nameof(IChatClientProvider)} is registered — register at least one before resolving {nameof(MomosChatClientFactory)}.");
        }

        _providers = providers;
        _defaultProvider = providers.Values.First();
        _build = build;
    }

    /// <summary>
    /// A factory whose clients reduce each tool round's request with <paramref name="toolRoundContext"/>,
    /// or pass requests through unchanged when it is null. A client bound to a manager serves that
    /// manager's loop only; create one per loop.
    /// </summary>
    public IChatClientFactory For(ContextManager? toolRoundContext) =>
        new ChatClientFactory(_providers, _defaultProvider, client => _build(client, toolRoundContext));
}
