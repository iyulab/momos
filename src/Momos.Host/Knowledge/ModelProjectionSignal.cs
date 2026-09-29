using System.Threading.Channels;

namespace Momos.Host.Knowledge;

/// <summary>Wakes <see cref="ModelProjectionService"/> when a model needs indexing. Carries no
/// payload: the database says what is unindexed, so a lost or coalesced signal loses nothing.</summary>
public sealed class ModelProjectionSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);

    public void Consume() => _channel.Reader.TryRead(out _);
}
