using Microsoft.EntityFrameworkCore;
using ThroughputBenchmark.Shared.Domain;

namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>
/// Every <see cref="IntervalSeconds"/> second(s), while a run is active, records a sample of
/// how many orders have been processed. Consecutive samples give per-second throughput and
/// reveal spikes or slowdowns over the run.
///
/// The processed count is computed INCREMENTALLY: each tick counts only the orders processed
/// since the previous tick (an indexed range scan on ProcessedAt), so the cost stays
/// proportional to orders-per-second and does not grow with the table size — important when a
/// 30-minute run accumulates millions of rows.
///
/// NOTE: this assumes a single API replica. With multiple API replicas you'd get one sampler
/// per replica (duplicate samples) and the enqueued counter would be split, so keep
/// Scale:ApiReplicas = 1 unless you move sampling into a dedicated singleton.
/// </summary>
public sealed class SamplerService(
    BenchmarkState state,
    IServiceScopeFactory scopeFactory,
    SystemMetrics systemMetrics,
    ILogger<SamplerService> logger) : BackgroundService
{
    public const int IntervalSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));

        Guid lastRunId = Guid.Empty;
        DateTimeOffset watermark = DateTimeOffset.MinValue;
        long processedTotal = 0;
        long lastEnqueued = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var run = state.Current;
            if (run is null)
            {
                lastRunId = Guid.Empty;
                continue;
            }

            // New run? reset the running totals.
            if (run.Id != lastRunId)
            {
                lastRunId = run.Id;
                watermark = DateTimeOffset.MinValue;
                processedTotal = 0;
                lastEnqueued = 0;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BenchmarkDbContext>();

                // Only the orders processed since the last sample (bounded so Count and Max see
                // a consistent set), via the (RunId, Status, ProcessedAt) index.
                var boundary = DateTimeOffset.UtcNow;
                var newOrders = db.Orders.Where(o =>
                    o.RunId == run.Id && o.Status == OrderStatus.Completed &&
                    o.ProcessedAt > watermark && o.ProcessedAt <= boundary);

                long delta = await newOrders.CountAsync(stoppingToken);
                if (delta > 0)
                    watermark = await newOrders.MaxAsync(o => o.ProcessedAt!.Value, stoppingToken);
                processedTotal += delta;

                long enqueued = Interlocked.Read(ref run.Enqueued);
                var sys = systemMetrics.Read();
                db.BenchmarkSamples.Add(new BenchmarkSample
                {
                    RunId = run.Id,
                    SampledAt = boundary,
                    ElapsedSeconds = (boundary - run.StartedAt).TotalSeconds,
                    OrdersProcessedTotal = processedTotal,
                    OrdersProcessedDelta = delta,
                    OrdersEnqueuedTotal = enqueued,
                    OrdersEnqueuedDelta = enqueued - lastEnqueued,
                    CpuPercent = sys.CpuPercent,
                    CpuTempC = sys.CpuTempC,
                    PowerWatts = sys.PowerWatts,
                    BatteryPercent = sys.BatteryPercent,
                });
                await db.SaveChangesAsync(stoppingToken);

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
