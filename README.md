# Throughput Benchmark

Benchmarks an application's throughput (and battery drain): a load generator sends requests to
an app server, which queues the work for background workers to process. Orchestrated with .NET Aspire.

## Prerequisites

- .NET 10 SDK
- A container runtime (Docker Desktop or Podman) — Aspire starts Postgres & RabbitMQ as containers
- Aspire workload/templates (already used to scaffold; not required to run)

## Run it

```bash
git clone https://github.com/KaliCZ/throughput-benchmark
cd throughput-benchmark
dotnet dev-certs https --trust          # one-time: trust the local HTTPS dev cert
dotnet run --project ThroughputBenchmark.AppHost
```

1. Open the **Aspire dashboard** URL printed in the console.
2. Open the **`apiservice`** endpoint from the dashboard — that's the benchmark control page.
3. Press **▶ Run 1 min** or **▶ Run 30 min**, then watch the live cards/table for the results.

> Stop the stack with **Ctrl+C** in the AppHost console so Aspire cleans up the containers.

## Benchmark results

Default config (3 workers, 1 generator, one consumer/in-flight per core). Numbers are the
run's averages: **processed/sec** = `averagePerSecond` from the page; **enqueued/sec** =
enqueued total ÷ elapsed. Add a row per machine.

### 1 minute — plugged in

| Machine / CPU | Avg processed/sec | Avg enqueued/sec |
|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | 3,888 | 7,847 |

### 1 minute — on battery

| Machine / CPU | Avg processed/sec | Avg enqueued/sec | Battery Used % |
|---|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | _TBD_ | _TBD_ | _TBD_ |

### 30 minutes — on battery, lowest screen brightness

| Machine / CPU | Avg processed/sec | Avg enqueued/sec | Battery Used % |
|---|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | _TBD_ | _TBD_ | _TBD_ |

> Read the values straight off the dashboard when the run auto-stops:
> - **Avg processed/sec** / **Avg enqueued/sec** — the throughput cards.
> - **Battery Used %** — the **Battery used** card: charge % at start − charge % at end.

## Results in Postgres

- `BenchmarkRuns` — one row per run (started/stopped timestamps, machine name).
- `BenchmarkSamples` — one row every second with cumulative + delta counts for both
  *enqueued* (accepted by the API) and *processed* (stored by a worker). At a 1s interval each
  delta *is* orders/sec for that second; compare across samples to see spikes/dips.
- `Orders` / `OrderItems` / `Payments` — the actual work output, tagged with `RunId`.
