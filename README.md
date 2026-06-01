# Throughput Benchmark

A small, real-world-ish workload for benchmarking a PC's local-development throughput
(and battery drain). It mirrors a typical backend: a web API takes order requests and
drops them on a queue; background workers pull from the queue, read setup data from
Postgres, build + serialize an invoice, and write the order back. A load generator spams
the API while a benchmark run is active. Throughput is sampled into Postgres every
second so you can chart per-second rate and spot spikes/slowdowns.

### What a worker does per order

1. `SELECT` the user (PK lookup) and the order's products (`WHERE id IN …`).
2. Compute data-dependent business math (per-region tax, bulk discount, totals).
3. Build an invoice object and **serialize it to JSON** — the realistic per-item CPU work
   (proportional to the data, ~0.5 KB, not an artificial spin loop). Stored on the order.
4. `INSERT` the order + line items + payment in one batched `SaveChanges`.

Measured ~6 ms/order on an 18-core machine (p95 ~11 ms) — I/O-shaped, dominated by the DB
round-trips. Keys are **time-ordered UUIDv7** so index inserts stay sequential and don't
degrade as the table grows. Set `Worker__ExtraCpuIterations > 0` to add a CPU burn per order
and turn it back into a CPU-bound stress test.

Everything is orchestrated by **.NET Aspire**.

## Architecture

```
LoadGenerator (console)  --HTTP-->  ApiService (ASP.NET)  --RabbitMQ-->  Worker(s)
   polls /status, spams                 enqueues order msgs                 fetch user+products from Postgres,
   /api/orders while a run               1s sampler -> Postgres             run calculation, store Order+Items+Payment
   is active                                                                back into Postgres
                                   \------------------ Postgres ------------------/
```

| Project | Role |
|---|---|
| `ThroughputBenchmark.AppHost` | Aspire orchestrator (Postgres + RabbitMQ + pgAdmin + all services) |
| `ThroughputBenchmark.ServiceDefaults` | Shared OpenTelemetry / health / service discovery |
| `ThroughputBenchmark.Shared` | EF Core `BenchmarkDbContext`, entities, queue message contract |
| `ThroughputBenchmark.ApiService` | Web server: order endpoint → queue, **Start/Stop control page**, 1s sampler |
| `ThroughputBenchmark.Worker` | Queue consumer: fetch setup → calculate → store results |
| `ThroughputBenchmark.LoadGenerator` | Console app that floods the API with order requests |

## Prerequisites

- .NET 10 SDK
- A container runtime (Docker Desktop or Podman) — Aspire starts Postgres & RabbitMQ as containers
- Aspire workload/templates (already used to scaffold; not required to run)

## Run it

```powershell
dotnet run --project ThroughputBenchmark.AppHost
```

1. Open the **Aspire dashboard** URL printed in the console.
2. Open the **`apiservice`** endpoint from the dashboard — that's the benchmark control page.
3. Press **▶ Start**. The load generators detect the active run and start hammering the API.
4. Watch the live cards/table: enqueued, processed, queue backlog, avg orders/sec, per-second deltas.
   The samples table has a **"Show every"** dropdown (Auto / 1s / 5s / 10s / 30s / 60s) that buckets
   the rows for display — the DB always keeps 1s resolution, and the metric cards stay 1s-accurate.
   **Auto** (the default) coarsens the table as the run grows so it never gets unwieldy: 1s for the
   first 30s, then 5s, 10s at 2m, 30s at 5m, and 60s past 15m.

### Control buttons

| Button | What it does |
|---|---|
| **▶ Start** | Begins a run: API accepts orders, generators send load, sampler records every second. |
| **⏸ Stop load** | Stops producing (API rejects new orders, generators idle) but **keeps workers draining** the existing queue. The run stays alive so you can watch the backlog burn down. |
| **■ Stop & purge** | Stops producing **and purges the queue**, so workers immediately go idle (CPU drops). Ends the run. |
| **🗑 Wipe DB** | Clears all order + benchmark data (keeps seeded products/users) so you can run a clean benchmark. Disabled mid-run. |

These map to endpoints: `POST /api/benchmark/{start,stop-producing,stop,wipe}`, plus `GET /api/benchmark/{status,current}`.

The RabbitMQ management UI (linked from the dashboard) shows live queue depth — handy for
seeing the consumer fall behind the producer.

> Stop the stack with **Ctrl+C** in the AppHost console so Aspire tears the containers down
> cleanly. (A hard kill can leave the Postgres/RabbitMQ containers running.)

## Running on another machine

The whole point is to compare hardware, and the app **auto-sizes to the machine** — `Worker:Consumers`
defaults to that machine's CPU core count, worker replicas to 3, and the Npgsql pool to `cores + 5`.
Nothing is hard-coded to this box, so a clone runs fairly on any machine.

```bash
git clone https://github.com/KaliCZ/throughput-benchmark
cd throughput-benchmark
dotnet dev-certs https --trust          # one-time: trust the local HTTPS dev cert
dotnet run --project ThroughputBenchmark.AppHost
```

Then open the `apiservice` endpoint from the dashboard and press **Start**.

What that machine needs:
- **.NET 10 SDK** and a **container runtime** (Docker Desktop / Podman). The Aspire templates are
  *not* required just to run — only the SDK + containers.
- Enough RAM for the Postgres settings (`shared_buffers=2GB`); fine on any modern dev box.

For a comparable score, run the same way on each machine (same buttons, similar run length) and
compare the **avg orders/sec** plus the per-second curve. If a machine's backlog stays flat, bump
`Generator__Parallelism` there so the workers stay fed (see *Producer vs consumer balance*). The
only fixed knobs are the Postgres server settings, which are sized to cover up to ~10 workers on
any reasonable machine.

## Benchmark results

Default config (3 workers, 2 generators, one consumer/in-flight per core). Numbers are the
run's averages: **processed/sec** = `averagePerSecond` from the page; **enqueued/sec** =
enqueued total ÷ elapsed. Add a row per machine.

### 1 minute — plugged in

| Machine / CPU | Avg processed/sec | Avg enqueued/sec |
|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | 3,881 | 8,077 |

### 1 minute — on battery

| Machine / CPU | Avg processed/sec | Avg enqueued/sec |
|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | _TBD_ | _TBD_ |

### 30 minutes — on battery, lowest screen brightness

| Machine / CPU | Avg processed/sec | Avg enqueued/sec | Battery used |
|---|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | _TBD_ | _TBD_ | _TBD_ |

> The plugged-in row above was measured on AC. The battery rows are left blank to fill in under
> those conditions (unplug / lowest brightness, no other heavy apps). "Battery used" = charge %
> at start minus charge % at end of the 30-minute run.

## Results in Postgres

- `BenchmarkRuns` — one row per run (started/stopped timestamps, machine name).
- `BenchmarkSamples` — one row every second with cumulative + delta counts for both
  *enqueued* (accepted by the API) and *processed* (stored by a worker). At a 1s interval each
  delta *is* orders/sec for that second; compare across samples to see spikes/dips.
- `Orders` / `OrderItems` / `Payments` — the actual work output, tagged with `RunId`.

## Scaling knobs (the part to tune)

The producer side (API + queue) is fast; the **worker** (CPU calc + DB writes) is the
bottleneck by design — that's the realistic signal. Tune via environment variables on the
**AppHost** before launching:

| Env var | Default | Effect |
|---|---|---|
| `Scale__ApiReplicas` | 1 | ASP.NET servers populating the queue *(keep at 1 — see note)* |
| `Scale__WorkerReplicas` | 3 | **Worker processes** draining the queue (competing consumers, each its own connection + GC). The primary scaling lever. |
| `Scale__GeneratorReplicas` | 2 | Generator **processes** spamming the API (horizontal, like the workers) |
| `Generator__Parallelism` | CPU count | Concurrent in-flight requests per generator process |
| `Worker__Consumers` | CPU count | Consumer threads **per worker process** — one per core, so each process already saturates the machine. Total worker concurrency = `WorkerReplicas × Consumers`. |
| `Db__MaxPoolSize` | cores + 5 | Npgsql connection-pool cap per worker process (API uses 20). Keeps `replicas × pool` under Postgres `max_connections`. |
| `Worker__Prefetch` | 50 | RabbitMQ prefetch per consumer channel |
| `Worker__CalcIterations` | 50000 | Simulated CPU work per order (raise to make work heavier) |

Example — 3 workers, heavier calc, more load:

```powershell
$env:Scale__WorkerReplicas = 3
$env:Worker__CalcIterations = 200000
$env:Generator__Parallelism = 64
dotnet run --project ThroughputBenchmark.AppHost
```

### Pushing CPU to 100%

The worker is intentionally the bottleneck. Each consumer alternates CPU (the calc) with a
DB-write wait. Each **process** runs one consumer per core, which over-subscribes the cores
just enough to overlap those I/O waits and keep the CPU busy; adding **processes** then
multiplies throughput. So:

1. **`Scale__WorkerReplicas`** is the main throughput lever — more processes ≈ more orders/sec
   (measured ~1.7k/s at 1 process → ~3.8k/s at 3 on an 18-core machine), until Postgres or the
   CPU caps out. This is the realistic RabbitMQ competing-consumers shape.
2. **`Worker__Consumers`** (default = cores) tunes per-process concurrency if you want fewer/more
   threads per process.
3. **`Worker__CalcIterations`** makes each order more CPU-heavy (lowers throughput but pegs cores
   — useful for a pure thermal/battery stress run).

Connection budget: total Postgres connections ≈ `WorkerReplicas × Db__MaxPoolSize` (+~25 for API
and pgAdmin). The default `max_connections=300` covers up to ~10 worker processes at the default
one-consumer-per-core setting. Going beyond that? Raise `max_connections` in the AppHost too.

Watch the **Queue backlog** card: if it keeps growing, producers outpace consumers (expected);
if it shrinks, you've added enough worker capacity. All workers share one Postgres container, so
past a point Postgres throughput (not connections — those are tuned for, see below) becomes the
limit; the next step would be tuning Postgres further or sharding the DB.

### Postgres tuning

The AppHost starts Postgres with throughput-oriented settings (in `AppHost.cs`):
`max_connections=300`, `shared_buffers=2GB`, `effective_cache_size=6GB`, `wal_buffers=64MB`,
`max_wal_size=8GB`, relaxed checkpoints, and **`synchronous_commit=off`** — the biggest win, since
it stops fsync'ing the WAL on every commit. Each worker process caps its Npgsql pool
(`Db__MaxPoolSize`, default cores+5) so up to ~10 processes stay within `max_connections`.

### Producer vs consumer balance

For the processed-throughput number to mean "worker capacity", the producers must stay *ahead*
of the workers — i.e. the **Queue backlog should keep growing** during a run. If the backlog
goes flat, the generator has become the bottleneck and you're measuring producer rate, not
worker rate. Scale the producer:

- **`Scale__GeneratorReplicas`** (default 2) — more generator processes (horizontal, like the
  workers). The main producer lever.
- **`Generator__Parallelism`** (default = cores) — more concurrent in-flight requests per
  generator process. A generator is roughly latency-bound (≈ parallelism ÷ round-trip-time).

Caveat measured on an 18-core machine: everything (generators, API, RabbitMQ, workers, Postgres)
shares the same CPU. Once the box is saturated (~4–4.4k orders/sec here), pushing the producer
*harder* grows the backlog but doesn't raise processed/sec — it slightly lowers it, because the
busier generator steals CPU from the workers. So size the producer just high enough to keep the
backlog growing, not maximal.

### Why throughput steps down / fluctuates

If you see throughput hold high for ~30–40s then drop and plateau, the usual cause is **CPU
power/thermal throttling**, not the queue or the DB — especially on a thin laptop. Evidence
from this project on an 18-core Snapdragon: under heavy per-order CPU work, the clock left its
boost budget after ~40s and settled lower, and *both* the enqueue and process rates fell
together (a shared-CPU-budget signature). With the lighter invoice workload the clock only
throttled ~14% and throughput stayed flat at ~4.2k/s for 2+ minutes. So a step-down is often
the benchmark correctly measuring sustained vs burst performance.

Things that were ruled out as the cause (worth knowing):

- **Missing indexes?** No — the worker's reads are primary-key lookups, and the sampler's count
  is covered by an index on `(RunId, Status)`. The real index pitfall was *random GUID* keys
  fragmenting the B-trees as the table grew; fixed by switching to time-ordered **UUIDv7**.
- **RabbitMQ slowing down past ~250k messages?** No — in a run the backlog grew past 470k while
  processing rate stayed flat. Modern RabbitMQ keeps the backlog on disk and streams it to
  consumers sequentially; consumers here are limited by CPU/DB, not queue delivery. (A huge
  backlog can trip RabbitMQ's memory alarm and block *publishers*, but that doesn't slow
  consumers.)
- **Small in-run wobble (±5–10%)** is normal: Postgres checkpoints, autovacuum, and GC.

### Note on multiple API replicas

The 1-second sampler and the in-memory "enqueued" counter live in the ApiService. With more
than one API replica you'd get duplicate samples and a split enqueued count. Keep
`Scale__ApiReplicas = 1` for now, **or** move `SamplerService` + the enqueued counter into a
dedicated singleton service before scaling the API out. The *processed* count is read from
Postgres, so it stays accurate regardless.

## Known MVP simplifications

- Queue is non-durable / messages transient (we want raw speed, not delivery guarantees).
- `synchronous_commit=off` trades durability for speed: a Postgres crash could lose the last few
  committed transactions (no corruption). Fine for a throwaway benchmark DB; don't copy for prod.
- Schema is created with `EnsureCreated()` and seeded on API startup (no EF migrations).
- The generator floods the queue far faster than workers drain it, so queue depth grows
  during a run. That's intentional for measuring consumer throughput; for very long runs you
  may want to pace the generator or bound the queue.
