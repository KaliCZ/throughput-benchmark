using System.Collections.Concurrent;
using RabbitMQ.Client;
using ThroughputBenchmark.Shared.Messaging;

namespace ThroughputBenchmark.ApiService.Messaging;

/// <summary>
/// Publishes order messages to RabbitMQ. RabbitMQ channels are not safe for
/// concurrent use, so we keep a fixed pool of channels and hand one out per publish.
/// </summary>
public sealed class RabbitMqPublisher : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly int _poolSize;
    private readonly ConcurrentQueue<IChannel> _idle = new();
    private readonly SemaphoreSlim _gate;
    private bool _initialized;

    public RabbitMqPublisher(IConnection connection, IConfiguration config)
    {
        _connection = connection;
        _poolSize = Math.Max(1, config.GetValue("Rabbit:PublisherChannels", 32));
        _gate = new SemaphoreSlim(_poolSize, _poolSize);
    }

    /// <summary>Declares the queue and pre-creates the channel pool.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        for (int i = 0; i < _poolSize; i++)
        {
            var channel = await _connection.CreateChannelAsync(cancellationToken: ct);
            if (!_initialized)
            {
                await channel.QueueDeclareAsync(QueueNames.Orders, durable: false, exclusive: false,
                    autoDelete: false, cancellationToken: ct);
                _initialized = true;
            }
            _idle.Enqueue(channel);
        }
    }

    public async Task PublishAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        _idle.TryDequeue(out var channel);
        try
        {
            var props = new BasicProperties { DeliveryMode = DeliveryModes.Transient };
            await channel!.BasicPublishAsync(exchange: "", routingKey: QueueNames.Orders,
                mandatory: false, basicProperties: props, body: body, cancellationToken: ct);
        }
        finally
        {
            _idle.Enqueue(channel!);
            _gate.Release();
        }
    }

    /// <summary>Removes all ready messages from the queue. Returns the count purged.</summary>
    public async Task<uint> PurgeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        _idle.TryDequeue(out var channel);
        try
        {
            return await channel!.QueuePurgeAsync(QueueNames.Orders, ct);
        }
        finally
        {
            _idle.Enqueue(channel!);
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        while (_idle.TryDequeue(out var channel))
            await channel.DisposeAsync();
    }
}
