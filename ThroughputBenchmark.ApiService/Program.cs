using Microsoft.EntityFrameworkCore;
using ThroughputBenchmark.ApiService.Benchmarking;
using ThroughputBenchmark.ApiService.Data;
using ThroughputBenchmark.ApiService.Messaging;
using ThroughputBenchmark.Shared.Domain;
using ThroughputBenchmark.Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Aspire-wired infrastructure (connection strings come from the AppHost).
// The API only touches the DB for the sampler + start/stop, so a small pool is plenty;
// keeping it bounded leaves connection headroom for the worker processes.
int apiMaxPool = builder.Configuration.GetValue("Db:MaxPoolSize", 20);
builder.AddNpgsqlDbContext<BenchmarkDbContext>("benchmarkdb", settings =>
{
    if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        settings.ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            MaxPoolSize = apiMaxPool,
        }.ConnectionString;
});
builder.AddRabbitMQClient("messaging");

builder.Services.AddSingleton<BenchmarkState>();
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddSingleton<SystemMetrics>();
builder.Services.AddHostedService<SamplerService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

// Create schema + seed setup data, and prepare the publisher channel pool, on startup.
await DbInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<RabbitMqPublisher>().InitializeAsync();

// ---- Benchmark control ----

// durationSeconds > 0 runs a fixed-length benchmark that the sampler auto-stops at the deadline
// (the "Run 1 min" / "Run 20 min" buttons). durationSeconds = 0 is an open-ended run you stop
// manually with the Stop buttons.
app.MapPost("/api/benchmark/start", async (BenchmarkState state, BenchmarkDbContext db, int durationSeconds = 0) =>
{
    var startedAt = DateTimeOffset.UtcNow;
    DateTimeOffset? endsAt = durationSeconds > 0 ? startedAt.AddSeconds(durationSeconds) : null;

    var run = new BenchmarkRun
    {
        Id = Guid.NewGuid(),
        StartedAt = startedAt,
        Status = RunStatus.Running,
        MachineName = Environment.MachineName,
    };
    db.BenchmarkRuns.Add(run);
    await db.SaveChangesAsync();

    state.Start(run.Id, startedAt, endsAt);
    return Results.Ok(new { runId = run.Id, startedAt, endsAt });
});

// Stop everything: stop producing, purge the queue so workers go idle, end the run.
app.MapPost("/api/benchmark/stop", async (BenchmarkState state, BenchmarkDbContext db, RabbitMqPublisher publisher) =>
{
    var current = state.Current;
    state.StopProducing(); // API rejects new orders immediately (no enqueue race)

    uint purged = await publisher.PurgeAsync();
    state.Stop();

    if (current is not null)
    {
        var run = await db.BenchmarkRuns.FindAsync(current.Id);
        if (run is not null)
        {
            run.Status = RunStatus.Stopped;
            run.StoppedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }
    return Results.Ok(new { stopped = true, purged });
});

// Reset for the next run: purge the queue and clear all order + benchmark data
// (the seeded Products/Users setup data is kept). Not allowed mid-run.
app.MapPost("/api/benchmark/wipe", async (BenchmarkState state, BenchmarkDbContext db, RabbitMqPublisher publisher) =>
{
    if (state.Current is not null)
        return Results.Conflict(new { error = "Stop the current run before wiping." });

    await publisher.PurgeAsync();
    await db.Database.ExecuteSqlRawAsync(
        """TRUNCATE TABLE "OrderItems", "Payments", "Orders", "BenchmarkSamples", "BenchmarkRuns" RESTART IDENTITY CASCADE;""");
    return Results.Ok(new { wiped = true });
});

// Lightweight status the load generators poll to decide whether to send load.
app.MapGet("/api/benchmark/status", (BenchmarkState state) =>
{
    var run = state.Current;
    return Results.Ok(new
    {
        active = run is not null,
        producing = run?.Producing ?? false,
        runId = run?.Id,
        productCount = DbInitializer.ProductCount,
        userCount = DbInitializer.UserCount,
    });
});

// Current run + samples for the dashboard. `bucket` (seconds) downsamples the table for the
// display only — the DB always keeps 1s resolution; the metric cards stay 1s-accurate.
app.MapGet("/api/benchmark/current", async (BenchmarkState state, BenchmarkDbContext db, int bucket = 1) =>
{
    var run = state.Current;
    if (run is null) return Results.Ok(new { active = false });

    // Last 2h of 1s samples (bounds memory on very long runs), ascending.
    var raw = await db.BenchmarkSamples
        .Where(s => s.RunId == run.Id)
        .OrderByDescending(s => s.SampledAt)
        .Take(7200)
        .Select(s => new
        {
            s.ElapsedSeconds,
            s.OrdersProcessedTotal,
            s.OrdersProcessedDelta,
            s.OrdersEnqueuedTotal,
            s.OrdersEnqueuedDelta,
            s.CpuPercent,
            s.BatteryPercent,
            s.OnAcPower,
        })
        .ToListAsync();
    raw.Reverse();

    // Average the present (non-null) values in a bucket; null if none are available.
    static double? AvgOrNull(IEnumerable<double?> xs)
    {
        var present = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return present.Count > 0 ? Math.Round(present.Average(), 1) : null;
    }

    // Cumulative average (per second) and a rolling last-10s rate — both from raw 1s data,
    // so they don't change with the chosen table granularity.
    double elapsedForAvg = raw.Count > 0 ? Math.Max(1, raw[^1].ElapsedSeconds) : 1;
    double avgPerSec = raw.Count > 0 ? raw[^1].OrdersProcessedTotal / elapsedForAvg : 0;
    double avgEnqPerSec = raw.Count > 0 ? raw[^1].OrdersEnqueuedTotal / elapsedForAvg : 0;
    var recent = raw.TakeLast(10).ToList();
    double recentWindow = Math.Max(1, recent.Count * SamplerService.IntervalSeconds);
    double recentPerSec = recent.Count > 0 ? recent.Sum(x => x.OrdersProcessedDelta) / recentWindow : 0;
    double recentEnqPerSec = recent.Count > 0 ? recent.Sum(x => x.OrdersEnqueuedDelta) / recentWindow : 0;

    // Run-level battery aggregate (from the raw 1s samples): charge % consumed start - end.
    // Only valid for a pure on-battery run: if AC was connected at any sample (started/ended on
    // AC, or charged mid-run), the delta is meaningless, so we flag it and withhold the number.
    bool chargedDuringRun = raw.Any(x => x.OnAcPower == true);
    var batterySamples = raw.Where(x => x.BatteryPercent.HasValue).Select(x => x.BatteryPercent!.Value).ToList();
    int? batteryUsedPercent = (!chargedDuringRun && batterySamples.Count >= 2)
        ? Math.Max(0, batterySamples[0] - batterySamples[^1])
        : null;

    // Bucket the table rows: sum deltas over each window, totals = end of window.
    int g = Math.Max(1, bucket);
    var samples = raw
        .GroupBy(s => (int)Math.Ceiling(s.ElapsedSeconds / g))
        .Select(grp =>
        {
            // Each raw sample covers IntervalSeconds, so a bucket spans Count * IntervalSeconds
            // seconds. Divide the summed deltas by that span to get a per-second RATE (not a
            // per-window total) — this also handles partial buckets like the final tick.
            double secs = Math.Max(1, grp.Count() * SamplerService.IntervalSeconds);
            return new
            {
                ElapsedSeconds = grp.Max(x => x.ElapsedSeconds),
                OrdersProcessedPerSec = Math.Round(grp.Sum(x => x.OrdersProcessedDelta) / secs),
                OrdersEnqueuedPerSec = Math.Round(grp.Sum(x => x.OrdersEnqueuedDelta) / secs),
                OrdersProcessedTotal = grp.Last().OrdersProcessedTotal,
                OrdersEnqueuedTotal = grp.Last().OrdersEnqueuedTotal,
                CpuPercent = AvgOrNull(grp.Select(x => x.CpuPercent)),
                BatteryPercent = grp.Last().BatteryPercent,
            };
        })
        .ToList();

    return Results.Ok(new
    {
        active = true,
        producing = run.Producing,
        runId = run.Id,
        startedAt = run.StartedAt,
        endsAt = run.EndsAt,
        enqueued = Interlocked.Read(ref run.Enqueued),
        averagePerSecond = avgPerSec,
        recentPerSecond = recentPerSec,
        averageEnqueuedPerSecond = avgEnqPerSec,
        recentEnqueuedPerSecond = recentEnqPerSec,
        batteryUsedPercent,
        batteryChargedDuringRun = chargedDuringRun,
        intervalSeconds = SamplerService.IntervalSeconds,
        bucketSeconds = g,
        samples,
    });
});

// ---- Hot path: accept an order request and enqueue it ----

app.MapPost("/api/orders", async (OrderRequest req, BenchmarkState state, RabbitMqPublisher publisher) =>
{
    var run = state.Current;
    if (run is null || !run.Producing) return Results.StatusCode(StatusCodes.Status409Conflict); // not accepting load

    var msg = new OrderMessage(run.Id, req.UserId, req.Lines, DateTimeOffset.UtcNow);
    var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(msg);
    await publisher.PublishAsync(body);

    Interlocked.Increment(ref run.Enqueued);
    return Results.Accepted();
});

app.Run();

record OrderRequest(int UserId, List<OrderLine> Lines);
