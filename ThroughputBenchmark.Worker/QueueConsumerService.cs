using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ThroughputBenchmark.Shared.Messaging;

namespace ThroughputBenchmark.Worker;

/// <summary>
/// Consumes order messages from RabbitMQ. To use multiple CPU cores within a single
/// worker process we open several channels, each with its own consumer; a channel
/// dispatches its messages sequentially, so N channels gives N concurrent orders.
/// </summary>
public sealed class QueueConsumerService(
    IConnection connection,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<QueueConsumerService> logger) : BackgroundService
{
    private readonly int _consumers = config.GetValue("Worker:Consumers", Environment.ProcessorCount);
    private readonly ushort _prefetch = (ushort)config.GetValue("Worker:Prefetch", 50);
    private readonly List<IChannel> _channels = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {Count} consumer channel(s), prefetch {Prefetch}", _consumers, _prefetch);

        for (int i = 0; i < _consumers; i++)
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(QueueNames.Orders, durable: false, exclusive: false,
                autoDelete: false, cancellationToken: stoppingToken);
            await channel.BasicQosAsync(0, _prefetch, global: false, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (sender, ea) => HandleAsync(channel, ea, stoppingToken);
            await channel.BasicConsumeAsync(QueueNames.Orders, autoAck: false, consumer, stoppingToken);

            _channels.Add(channel);
        }

        // Stay alive until shutdown.
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<OrderMessage>(ea.Body.Span);
            if (msg is not null)
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OrderProcessor>();
                await processor.ProcessAsync(msg, ct);
            }
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process order message");
            // Drop poison messages instead of requeuing to keep the benchmark flowing.
            try { await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct); }
            catch { /* channel may be closing */ }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var channel in _channels)
        {
            try { await channel.DisposeAsync(); } catch { }
        }
    }
}
