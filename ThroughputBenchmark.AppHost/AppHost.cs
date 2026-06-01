// Aspire orchestration for the throughput benchmark.
//
// Scale knobs (set as env vars / user-secrets / launch settings on the AppHost):
//   Scale__ApiReplicas        (default 1)       – ASP.NET servers populating the queue
//   Scale__WorkerReplicas     (default 3)       – worker PROCESSES draining the queue
//   Scale__GeneratorReplicas  (default 2)       – generator PROCESSES spamming the API
//   Generator__Parallelism    (default cores)   – concurrent in-flight requests per generator
//   Worker__Consumers         (default cores)   – consumer threads per worker process
//   Worker__Prefetch          (default 50)      – RabbitMQ prefetch per consumer
//   Worker__ExtraCpuIterations(default 0)       – optional extra CPU burn per order (stress mode)
//   Db__MaxPoolSize           (default cores+5) – Npgsql pool cap per worker process

var builder = DistributedApplication.CreateBuilder(args);

var cfg = builder.Configuration;
int GetInt(string key, int def) => int.TryParse(cfg[key], out var v) ? v : def;

int apiReplicas = GetInt("Scale:ApiReplicas", 1);
// Generators scale horizontally like the workers: GeneratorReplicas processes, each firing
// Parallelism concurrent requests (defaults to one per core).
int generatorReplicas = GetInt("Scale:GeneratorReplicas", 2);
int generatorParallelism = GetInt("Generator:Parallelism", Environment.ProcessorCount);
int workerPrefetch = GetInt("Worker:Prefetch", 50);
int workerExtraCpu = GetInt("Worker:ExtraCpuIterations", 0);

// Workers scale HORIZONTALLY: WorkerReplicas separate processes act as competing consumers,
// each with its own RabbitMQ connection + GC (the real-world RabbitMQ deployment shape).
// Each process runs ONE CONSUMER PER CORE, so a single process already saturates the machine;
// adding processes (WorkerReplicas) then multiplies CPU throughput. Total consumers = replicas * cores.
int workerReplicas = GetInt("Scale:WorkerReplicas", 3);
int workerConsumers = GetInt("Worker:Consumers", Environment.ProcessorCount);

// --- Operational database ---
// Tuned so Postgres isn't the bottleneck up to ~10 worker processes. Each worker caps its
// Npgsql pool (Db:MaxPoolSize ~= cores+5); at the default one-consumer-per-core concurrency,
// 10 processes + API + pgAdmin stay under max_connections=300 (e.g. 18-core: 10x23 + 20 + 5).
// synchronous_commit=off skips the per-commit WAL fsync — a big insert-throughput win that's
// fine for a throwaway benchmark DB (a crash could lose the last few txns; no corruption).
var postgres = builder.AddPostgres("postgres")
    .WithArgs(
        "-c", "max_connections=300",
        "-c", "shared_buffers=2GB",
        "-c", "effective_cache_size=6GB",
        "-c", "work_mem=16MB",
        "-c", "maintenance_work_mem=512MB",
        "-c", "synchronous_commit=off",
        "-c", "wal_buffers=64MB",
        "-c", "max_wal_size=8GB",
        "-c", "min_wal_size=1GB",
        "-c", "checkpoint_timeout=15min",
        "-c", "checkpoint_completion_target=0.9",
        "-c", "random_page_cost=1.1")
    .WithPgAdmin();
var db = postgres.AddDatabase("benchmarkdb");

// Pool must cover a process's consumers; +5 headroom. Keeps replicas * pool under max_connections.
int workerMaxPool = GetInt("Db:MaxPoolSize", workerConsumers + 5);

// --- Queue (with management UI so you can watch queue depth live) ---
var rabbitmq = builder.AddRabbitMQ("messaging")
    .WithManagementPlugin();

// --- Web server(s): take requests, enqueue work, run the benchmark control panel ---
var api = builder.AddProject<Projects.ThroughputBenchmark_ApiService>("apiservice")
    .WithReference(db).WaitFor(db)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WithReplicas(apiReplicas)
    .WithExternalHttpEndpoints();

// --- Worker(s): consume the queue, fetch setup, calculate, store results ---
builder.AddProject<Projects.ThroughputBenchmark_Worker>("worker")
    .WithReference(db).WaitFor(db)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WithEnvironment("Worker__Prefetch", workerPrefetch.ToString())
    .WithEnvironment("Worker__ExtraCpuIterations", workerExtraCpu.ToString())
    .WithEnvironment("Worker__Consumers", workerConsumers.ToString())
    .WithEnvironment("Db__MaxPoolSize", workerMaxPool.ToString())
    .WithReplicas(workerReplicas);

// --- Load generator(s): poll the API and spam order requests while a run is active ---
builder.AddProject<Projects.ThroughputBenchmark_LoadGenerator>("loadgenerator")
    .WithReference(api).WaitFor(api)
    .WithEnvironment("Generator__Parallelism", generatorParallelism.ToString())
    .WithReplicas(generatorReplicas);

builder.Build().Run();
