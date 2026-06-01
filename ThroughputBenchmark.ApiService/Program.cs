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
builder.Services.AddHostedService<SamplerService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

// Create schema + seed setup data, and prepare the publisher channel pool, on startup.
await DbInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<RabbitMqPublisher>().InitializeAsync();

// ---- Benchmark control ----

app.MapPost("/api/benchmark/start", async (BenchmarkState state, BenchmarkDbContext db) =>
{
    var run = new BenchmarkRun
    {
        Id = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UtcNow,
        Status = RunStatus.Running,
        MachineName = Environment.MachineName,
    };
    db.BenchmarkRuns.Add(run);
    await db.SaveChangesAsync();

    state.Start(run.Id, run.StartedAt);
    return Results.Ok(new { runId = run.Id, startedAt = run.StartedAt });
});

// Stop accepting/generating new orders, but keep workers draining the existing queue.
app.MapPost("/api/benchmark/stop-producing", (BenchmarkState state) =>
{
    state.StopProducing();
    return Results.Ok(new { producing = false });
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
        })
        .ToListAsync();
    raw.Reverse();

    // Cumulative average (per second) and a rolling last-10s rate — both from raw 1s data,
    // so they don't change with the chosen table granularity.
    double avgPerSec = raw.Count > 0
        ? raw[^1].OrdersProcessedTotal / Math.Max(1, raw[^1].ElapsedSeconds)
        : 0;
    var recent = raw.TakeLast(10).ToList();
    double recentPerSec = recent.Count > 0
        ? recent.Sum(x => x.OrdersProcessedDelta) / (double)(recent.Count * SamplerService.IntervalSeconds)
        : 0;

    // Bucket the table rows: sum deltas over each window, totals = end of window.
    int g = Math.Max(1, bucket);
    var samples = raw
        .GroupBy(s => (int)Math.Ceiling(s.ElapsedSeconds / g))
        .Select(grp => new
        {
            ElapsedSeconds = grp.Max(x => x.ElapsedSeconds),
            OrdersProcessedTotal = grp.Last().OrdersProcessedTotal,
            OrdersProcessedDelta = grp.Sum(x => x.OrdersProcessedDelta),
            OrdersEnqueuedTotal = grp.Last().OrdersEnqueuedTotal,
            OrdersEnqueuedDelta = grp.Sum(x => x.OrdersEnqueuedDelta),
        })
        .ToList();

    return Results.Ok(new
    {
        active = true,
        producing = run.Producing,
        runId = run.Id,
        startedAt = run.StartedAt,
        enqueued = Interlocked.Read(ref run.Enqueued),
        averagePerSecond = avgPerSec,
        recentPerSecond = recentPerSec,
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
