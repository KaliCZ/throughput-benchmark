using Microsoft.EntityFrameworkCore;
using ThroughputBenchmark.Shared.Domain;

namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>
/// Every <see cref="IntervalSeconds"/> seconds, while a run is active, records a sample
/// of how many orders have been processed so far. Comparing consecutive samples gives
/// throughput (orders/sec) and reveals spikes or slowdowns over the run.
///
/// NOTE: this assumes a single API replica. With multiple API replicas you'd get one
/// sampler per replica (duplicate samples) and the enqueued counter would be split, so
/// keep Scale:ApiReplicas = 1 unless you move sampling into a dedicated singleton.
/// </summary>
public sealed class SamplerService(
    BenchmarkState state,
    IServiceScopeFactory scopeFactory,
    ILogger<SamplerService> logger) : BackgroundService
{
    public const int IntervalSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));

        Guid lastRunId = Guid.Empty;
        long lastProcessed = 0;
        long lastEnqueued = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var run = state.Current;
            if (run is null)
            {
                lastRunId = Guid.Empty;
                continue;
            }

            // New run? reset the deltas baseline.
            if (run.Id != lastRunId)
            {
                lastRunId = run.Id;
                lastProcessed = 0;
                lastEnqueued = 0;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BenchmarkDbContext>();

                long processed = await db.Orders
                    .CountAsync(o => o.RunId == run.Id && o.Status == OrderStatus.Completed, stoppingToken);
                long enqueued = Interlocked.Read(ref run.Enqueued);

                var now = DateTimeOffset.UtcNow;
                db.BenchmarkSamples.Add(new BenchmarkSample
                {
                    RunId = run.Id,
                    SampledAt = now,
                    ElapsedSeconds = (now - run.StartedAt).TotalSeconds,
                    OrdersProcessedTotal = processed,
                    OrdersProcessedDelta = processed - lastProcessed,
                    OrdersEnqueuedTotal = enqueued,
                    OrdersEnqueuedDelta = enqueued - lastEnqueued,
                });
                await db.SaveChangesAsync(stoppingToken);

                lastProcessed = processed;
                lastEnqueued = enqueued;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record benchmark sample");
            }
        }
    }
}
