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
3. Press **▶ Run 1 min** or **▶ Run 20 min**, then watch the live cards/table for the results.

> Stop the stack with **Ctrl+C** in the AppHost console so Aspire cleans up the containers.

## Benchmark results

Default config (3 workers, 1 generator, one consumer/in-flight per **logical processor** —
i.e. `Environment.ProcessorCount`, which counts SMT threads, not physical cores). On chips
without SMT (the Snapdragon and the Core Ultra) threads = cores; the SMT-enabled Ryzen runs
at **32-way** parallelism (16 cores × 2 threads), so its per-process consumer/generator count
is double what the core figure suggests. Numbers are the run's averages: **processed/sec** =
`averagePerSecond` from the page; **enqueued/sec** = enqueued total ÷ elapsed. Add a row per machine.

### 1 minute — plugged in

| Machine / CPU | Avg processed/sec | Avg enqueued/sec |
|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | 4,751 | 7,591 |
| **Asus Zenbook 14 (UX3405CA)**<br>Intel Core Ultra 7 255H, 16 cores | 1,451 | 4,123 |
| **Asus ROG Strix G16 (G614PR)**<br>AMD Ryzen 9 8940HX, 16 cores / 32 threads | 4,087 | 4,648 |
| **Apple MacBook Pro 14" (Nov 2023)**<br>Apple M3 Pro, 12 cores | 1,538 | 3,537 |

### 1 minute — on battery

| Machine / CPU | Avg processed/sec | Avg enqueued/sec | Battery Used % |
|---|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | 4,366 | 7,812 | 1% |
| **Asus Zenbook 14 (UX3405CA)**<br>Intel Core Ultra 7 255H, 16 cores | 1,619 | 4,152 | 1% |
| **Asus ROG Strix G16 (G614PR)**<br>AMD Ryzen 9 8940HX, 16 cores / 32 threads | 3,758 | 4,191 | 2% |
| **Apple MacBook Pro 14" (Nov 2023)**<br>Apple M3 Pro, 12 cores | 1,854 | 4,958 | 0% |

### 20 minutes — on battery

The **energy** columns are derived, not read off the dashboard: **Wh used** = Battery Used % ×
full-charge capacity; **Wh / 1M orders** = Wh used ÷ (avg processed/sec × 1,200 s) — lower is
better (more work per watt-hour). They're approximate: Windows reports charge as an integer %,
so this is only meaningful on these 20-minute runs where the drain is large (it's why the
1-minute battery tables above, at 1–2% drain, omit it). Full-charge capacities: ROG Strix
**87.5 Wh** measured (90 Wh design); Zenbook A16 **70 Wh** and Zenbook 14 **75 Wh** (nominal spec);
MacBook Pro 14" **72.4 Wh** (Apple spec).

| Machine / CPU | Brightness | Avg processed/sec | Avg enqueued/sec | Battery | Used % | Wh used | Wh / 1M orders |
|---|---|---|---|---|---|---|---|
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | low | 4,171 | 6,528 | 70 Wh | 27% | 18.9 | **3.8** |
| **Asus A16 ARM**<br>Snapdragon X2 Elite Extreme (X2E94100), 18 cores | high | 4,306 | 6,977 | 70 Wh | 28% | 19.6 | **3.8** |
| **Asus Zenbook 14 (UX3405CA)**<br>Intel Core Ultra 7 255H, 16 cores | low | 1,100 | 2,856 | 75 Wh | 12% | 9.0 | **6.8** |
| **Asus ROG Strix G16 (G614PR)**<br>AMD Ryzen 9 8940HX, 16 cores / 32 threads | low | 3,337 | 3,378 | 87.5 Wh | 27% | 23.6 | **5.9** |
| **Apple MacBook Pro 14" (Nov 2023)**<br>Apple M3 Pro, 12 cores | — | 1,854 | 5,297 | 72.4 Wh | 14% | 10.1 | **4.6** |

> **Efficiency:** the Snapdragon's ~3.8 Wh/1M beats the M3 Pro's 4.6, the Ryzen's 5.9 (~1.55×)
> and the Core Ultra's 6.8 — the M3 Pro lands second despite its much lower raw throughput.
> The Snapdragon's edge over the Ryzen splits roughly evenly between two things: the Snapdragon
> processes ~25% more orders per % of charge (chip/platform), **and** its 70 Wh battery is ~20%
> smaller than the Ryzen's 87.5 Wh, so an identical 27% drain is less actual energy.

> Read the throughput values straight off the dashboard when the run auto-stops:
> - **Avg processed/sec** / **Avg enqueued/sec** — the throughput cards.
> - **Battery Used %** — the **Battery used** card: charge % at start − charge % at end.

## Results in Postgres

- `BenchmarkRuns` — one row per run (started/stopped timestamps, machine name).
- `BenchmarkSamples` — one row every second with cumulative + delta counts for both
  *enqueued* (accepted by the API) and *processed* (stored by a worker). At a 1s interval each
  delta *is* orders/sec for that second; compare across samples to see spikes/dips.
- `Orders` / `OrderItems` / `Payments` — the actual work output, tagged with `RunId`.
