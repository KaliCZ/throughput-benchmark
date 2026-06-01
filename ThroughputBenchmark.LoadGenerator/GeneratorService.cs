using System.Net.Http.Json;

namespace ThroughputBenchmark.LoadGenerator;

/// <summary>
/// Polls the API for an active benchmark run and, while one is active, spams
/// order-create requests using a configurable number of concurrent senders.
/// </summary>
public sealed class GeneratorService(
    IHttpClientFactory clientFactory,
    IConfiguration config,
    ILogger<GeneratorService> logger) : BackgroundService
{
    private readonly int _parallelism = Math.Max(1, config.GetValue("Generator:Parallelism", 32));

    private volatile bool _active;
    private int _userCount;
    private int _productCount;
    private long _sent;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Load generator ready: {Parallelism} concurrent senders", _parallelism);

        var poller = Task.Run(() => PollStatusAsync(ct), ct);
        var senders = Enumerable.Range(0, _parallelism)
            .Select(_ => Task.Run(() => SendLoopAsync(ct), ct))
            .ToArray();

        await Task.WhenAll(senders.Append(poller));
    }

    private async Task PollStatusAsync(CancellationToken ct)
    {
        var client = clientFactory.CreateClient("api");
        long lastReported = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await client.GetFromJsonAsync<StatusDto>("/api/benchmark/status", ct);
                if (status is not null)
                {
                    _userCount = status.UserCount;
                    _productCount = status.ProductCount;
                    bool was = _active;
                    _active = status.Producing;
                    if (_active && !was) logger.LogInformation("Run active — generating load");
                    if (!_active && was)
                        logger.LogInformation("Run stopped — sent {Sent} requests total", Interlocked.Read(ref _sent));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Status poll failed");
            }

            // Occasional heartbeat of throughput.
            long sent = Interlocked.Read(ref _sent);
            if (_active && sent - lastReported >= 50_000)
            {
                logger.LogInformation("Sent {Sent} requests so far", sent);
                lastReported = sent;
            }

            await Task.Delay(1000, ct);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var client = clientFactory.CreateClient("api");
        var rnd = new Random(Guid.NewGuid().GetHashCode());

        while (!ct.IsCancellationRequested)
        {
            if (!_active || _productCount == 0 || _userCount == 0)
            {
                await Task.Delay(200, ct);
                continue;
            }

            var request = BuildRandomOrder(rnd);
            try
            {
                using var resp = await client.PostAsJsonAsync("/api/orders", request, ct);
                if (resp.IsSuccessStatusCode)
                    Interlocked.Increment(ref _sent);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // API may be saturated or restarting; back off briefly and retry.
                await Task.Delay(50, ct);
            }
        }
    }

    private OrderRequestDto BuildRandomOrder(Random rnd)
    {
        int lineCount = rnd.Next(1, 5);
        var lines = new List<OrderLineDto>(lineCount);
        for (int i = 0; i < lineCount; i++)
            lines.Add(new OrderLineDto(rnd.Next(1, _productCount + 1), rnd.Next(1, 6)));

        return new OrderRequestDto(rnd.Next(1, _userCount + 1), lines);
    }

    private record StatusDto(bool Active, bool Producing, Guid? RunId, int ProductCount, int UserCount);
    private record OrderRequestDto(int UserId, List<OrderLineDto> Lines);
    private record OrderLineDto(int ProductId, int Quantity);
}
