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
app.MapPost("/api/benchmark/start", async (BenchmarkState state, BenchmarkDbContext db, RabbitMqPublisher publisher, int durationSeconds = 0) =>
{
    // Every run starts from a clean operational state: purge the queue and drop all transactional
    // order data so a prior run's millions of rows can't bloat the indexes and skew this run's
    // insert throughput. Seeded Products/Users and the run history (Runs/Samples) are kept.
    await publisher.PurgeAsync();
    await db.Database.ExecuteSqlRawAsync(
        """TRUNCATE TABLE "OrderItems", "Payments", "Orders" RESTART IDENTITY CASCADE;""");

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

// Summary of every recorded run (for the history view). Samples are 1/second, so this aggregate
// stays cheap even though the Orders table is huge — the numbers come from BenchmarkSamples only.
app.MapGet("/api/benchmark/runs", async (BenchmarkState state, BenchmarkDbContext db) =>
{
    var runs = await db.BenchmarkRuns
        .OrderByDescending(r => r.StartedAt)
        .Take(100)
        .Select(r => new { r.Id, r.StartedAt, r.StoppedAt, r.Status })
        .ToListAsync();

    // One grouped pass over the (small) samples table: totals are monotonic, so MAX = final.
    var agg = (await db.BenchmarkSamples
        .GroupBy(s => s.RunId)
        .Select(g => new
        {
            RunId = g.Key,
            Processed = g.Max(s => s.OrdersProcessedTotal),
            Enqueued = g.Max(s => s.OrdersEnqueuedTotal),
            Elapsed = g.Max(s => s.ElapsedSeconds),
            BattMax = g.Max(s => s.BatteryPercent),
            BattMin = g.Min(s => s.BatteryPercent),
        })
        .ToListAsync()).ToDictionary(a => a.RunId);

    var chargedRunIds = (await db.BenchmarkSamples
        .Where(s => s.OnAcPower == true).Select(s => s.RunId).Distinct().ToListAsync()).ToHashSet();

    var activeId = state.Current?.Id;

    var result = runs.Select(r =>
    {
        agg.TryGetValue(r.Id, out var a);
        double elapsed = a?.Elapsed ?? 0;
        long processed = a?.Processed ?? 0;
        long enqueued = a?.Enqueued ?? 0;
        bool charged = chargedRunIds.Contains(r.Id);
        int? battUsed = (!charged && a?.BattMax is int hi && a?.BattMin is int lo) ? Math.Max(0, hi - lo) : null;
        return new
        {
            id = r.Id,
            startedAt = r.StartedAt,
            stoppedAt = r.StoppedAt,
            status = r.Status.ToString(),
            isActive = r.Id == activeId,
            elapsedSeconds = Math.Round(elapsed, 1),
            processedTotal = processed,
            avgProcessedPerSec = Math.Round(processed / Math.Max(1, elapsed)),
            avgEnqueuedPerSec = Math.Round(enqueued / Math.Max(1, elapsed)),
            batteryUsedPercent = battUsed,
            batteryChargedDuringRun = charged,
        };
    });

    return Results.Ok(result);
});

// Detail of a single run — active OR historical — for the detail page. `bucket` (seconds)
// downsamples the table for display only; the DB always keeps 1s resolution.
app.MapGet("/api/benchmark/run/{id:guid}", async (Guid id, BenchmarkState state, BenchmarkDbContext db, int bucket = 1) =>
{
    var runRow = await db.BenchmarkRuns.FirstOrDefaultAsync(r => r.Id == id);
    if (runRow is null) return Results.NotFound();

    var active = state.Current;
    bool isActive = active?.Id == id;

    // Last 2h of 1s samples (bounds memory on very long runs), ascending.
    var raw = await db.BenchmarkSamples
        .Where(s => s.RunId == id)
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

    long enqueued = isActive ? Interlocked.Read(ref active!.Enqueued)
                             : (raw.Count > 0 ? raw[^1].OrdersEnqueuedTotal : 0);

    return Results.Ok(new
    {
        runId = runRow.Id,
        isActive,
        producing = isActive && active!.Producing,
        status = runRow.Status.ToString(),
        startedAt = runRow.StartedAt,
        stoppedAt = runRow.StoppedAt,
        endsAt = isActive ? active!.EndsAt : null,
        enqueued,
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
