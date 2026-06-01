namespace ThroughputBenchmark.Shared.Messaging;

/// <summary>Names shared between the API (publisher) and the Worker (consumer).</summary>
public static class QueueNames
{
    public const string Orders = "orders";
}

/// <summary>A single requested order item.</summary>
public record OrderLine(int ProductId, int Quantity);

/// <summary>
/// The unit of work placed on the queue by the API and consumed by the worker.
/// Kept intentionally small — the worker fetches the real product/user data from
/// Postgres to simulate a realistic "load setup, calculate, store result" task.
/// </summary>
public record OrderMessage(
    Guid RunId,
    int UserId,
    IReadOnlyList<OrderLine> Lines,
    DateTimeOffset EnqueuedAt);
