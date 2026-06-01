namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>
/// In-memory view of the currently active benchmark run, shared across requests.
/// The current run is held behind a single reference so reads on the hot path
/// (every order request) are lock-free; we swap the reference atomically on start/stop.
///
/// A run has two switches:
///   * <see cref="RunInfo.Producing"/> — whether the API accepts new orders / generators send load.
///   * the run existing at all (<see cref="Current"/> != null) — whether the sampler is recording.
/// Stop (and the fixed-duration auto-stop) flips Producing off, purges the queue, and ends the run
/// in one step. Producing is flipped first so the API stops enqueuing before the purge — no race.
/// </summary>
public sealed class BenchmarkState
{
    public sealed class RunInfo(Guid id, DateTimeOffset startedAt, DateTimeOffset? endsAt)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>For a fixed-duration run, when the sampler should auto-stop it; null = open-ended.</summary>
        public DateTimeOffset? EndsAt { get; } = endsAt;

        public long Enqueued;
        public volatile bool Producing = true;
    }

    private RunInfo? _current;

    public RunInfo? Current => Volatile.Read(ref _current);

    public void Start(Guid id, DateTimeOffset startedAt, DateTimeOffset? endsAt = null)
        => Volatile.Write(ref _current, new RunInfo(id, startedAt, endsAt));

    /// <summary>Stop accepting/generating new orders, but keep the run (and sampler) alive.</summary>
    public void StopProducing()
    {
        var run = Current;
        if (run is not null) run.Producing = false;
    }

    /// <summary>End the run entirely.</summary>
    public void Stop() => Volatile.Write(ref _current, null);
}
