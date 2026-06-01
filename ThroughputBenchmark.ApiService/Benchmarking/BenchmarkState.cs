namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>
/// In-memory view of the currently active benchmark run, shared across requests.
/// The current run is held behind a single reference so reads on the hot path
/// (every order request) are lock-free; we swap the reference atomically on start/stop.
///
/// A run has two independent switches:
///   * <see cref="RunInfo.Producing"/> — whether the API accepts new orders / generators send load.
///   * the run existing at all (<see cref="Current"/> != null) — whether the sampler is recording.
/// "Stop load" flips Producing off but keeps the run alive so you can watch the queue drain.
/// "Stop &amp; purge" ends the run entirely.
/// </summary>
public sealed class BenchmarkState
{
    public sealed class RunInfo(Guid id, DateTimeOffset startedAt)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long Enqueued;
        public volatile bool Producing = true;
    }

    private RunInfo? _current;

    public RunInfo? Current => Volatile.Read(ref _current);

    public void Start(Guid id, DateTimeOffset startedAt)
        => Volatile.Write(ref _current, new RunInfo(id, startedAt));

    /// <summary>Stop accepting/generating new orders, but keep the run (and sampler) alive.</summary>
    public void StopProducing()
    {
        var run = Current;
        if (run is not null) run.Producing = false;
    }

    /// <summary>End the run entirely.</summary>
    public void Stop() => Volatile.Write(ref _current, null);
}
