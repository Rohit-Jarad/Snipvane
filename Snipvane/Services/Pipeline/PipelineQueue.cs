using System.Threading.Channels;

namespace Snipvane.Services.Pipeline;

public interface IPipelineQueue
{
    ValueTask EnqueueAsync(Guid videoId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

public class PipelineQueue : IPipelineQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(videoId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
