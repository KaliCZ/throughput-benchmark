namespace ThroughputBenchmark.Shared.Domain;

// ---- Operational / setup data (seeded once) ----

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

// ---- Per-order data (written by the worker) ----

public enum OrderStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}

public class Order
{
    public Guid Id { get; set; }

    /// <summary>The benchmark run this order belongs to (used for sampling/aggregation).</summary>
    public Guid RunId { get; set; }

    public int UserId { get; set; }
    public OrderStatus Status { get; set; }
    public decimal TotalAmount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>How long the worker spent processing this order, in milliseconds.</summary>
    public double ProcessingMs { get; set; }

    /// <summary>The generated invoice, serialized to JSON (the realistic per-item work).</summary>
    public string? InvoiceJson { get; set; }

    public List<OrderItem> Items { get; set; } = new();
    public Payment? Payment { get; set; }
}

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public enum PaymentStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
}

public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string Method { get; set; } = "card";
}

// ---- Benchmark bookkeeping ----

public enum RunStatus
{
    Running = 0,
    Stopped = 1,
}

public class BenchmarkRun
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public RunStatus Status { get; set; }
    public string MachineName { get; set; } = "";
    public string? Notes { get; set; }
}

/// <summary>
/// A point-in-time snapshot taken every few seconds during a run so we can chart
/// throughput (orders/sec) and spot spikes or slowdowns afterwards.
/// </summary>
public class BenchmarkSample
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset SampledAt { get; set; }

    /// <summary>Seconds since the run started.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Cumulative orders fully processed (stored by a worker) since the run started.</summary>
    public long OrdersProcessedTotal { get; set; }

    /// <summary>Orders processed since the previous sample.</summary>
    public long OrdersProcessedDelta { get; set; }

    /// <summary>Cumulative order requests the API has accepted and enqueued.</summary>
    public long OrdersEnqueuedTotal { get; set; }

    /// <summary>Enqueued since the previous sample.</summary>
    public long OrdersEnqueuedDelta { get; set; }

    // Host system metrics at sample time (null when the platform doesn't expose them).
    public double? CpuPercent { get; set; }
    public int? BatteryPercent { get; set; }
}
