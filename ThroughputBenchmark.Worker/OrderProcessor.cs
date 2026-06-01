using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThroughputBenchmark.Shared.Domain;
using ThroughputBenchmark.Shared.Messaging;

namespace ThroughputBenchmark.Worker;

/// <summary>
/// A realistic unit of work: load setup data (user + products) from Postgres, build an invoice
/// from the order and serialize it to JSON — the per-item CPU work is now proportional to the
/// data (constant instructions), not an artificial spin loop — then persist the order, its line
/// items and the payment.
/// </summary>
public sealed class OrderProcessor(BenchmarkDbContext db, IConfiguration config)
{
    // Optional extra CPU burn per order (0 = off). Turn this up to make it a CPU-bound stress
    // test again without touching code.
    private readonly int _extraCpuIterations = config.GetValue("Worker:ExtraCpuIterations", 0);

    private static readonly JsonSerializerOptions InvoiceOptions = new(JsonSerializerDefaults.Web);
    private static double _cpuSink;

    public async Task ProcessAsync(OrderMessage msg, CancellationToken ct)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        // --- Fetch setup from the database ---
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == msg.UserId, ct);
        var productIds = msg.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        bool ok = user is not null && products.Count > 0;

        // UUIDv7 is time-ordered, so primary-key / index inserts stay sequential (append-mostly)
        // instead of fragmenting the B-tree the way random GUIDs do as the table grows.
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            RunId = msg.RunId,
            UserId = msg.UserId,
            CreatedAt = msg.EnqueuedAt,
            Status = ok ? OrderStatus.Completed : OrderStatus.Failed,
        };

        decimal subtotal = 0;
        var invoiceLines = new List<InvoiceLine>(msg.Lines.Count);
        foreach (var line in msg.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product)) continue;
            var lineTotal = product.Price * line.Quantity;
            subtotal += lineTotal;
            order.Items.Add(new OrderItem
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitPrice = product.Price,
                LineTotal = lineTotal,
            });
            invoiceLines.Add(new InvoiceLine(product.Id, product.Sku, product.Name,
                line.Quantity, product.Price, lineTotal));
        }

        // --- Business math (data-dependent) + serialize the invoice to JSON ---
        int totalQty = order.Items.Sum(i => i.Quantity);
        decimal discountRate = (totalQty > 8 || subtotal > 400m) ? 0.05m : 0m;
        decimal discount = Math.Round(subtotal * discountRate, 2);
        decimal taxRate = 0.05m + (msg.UserId % 5) * 0.01m;          // 5%–9% by "region"
        decimal taxable = subtotal - discount;
        decimal tax = Math.Round(taxable * taxRate, 2);
        decimal grandTotal = taxable + tax;

        var invoice = new Invoice(
            InvoiceNumber: $"INV-{order.CreatedAt:yyyyMMdd}-{order.Id.ToString("N")[..8].ToUpperInvariant()}",
            IssuedAt: DateTimeOffset.UtcNow,
            UserId: msg.UserId,
            CustomerName: user?.Name ?? "Unknown",
            CustomerEmail: user?.Email ?? "",
            Currency: "USD",
            Lines: invoiceLines,
            Subtotal: subtotal,
            DiscountRate: discountRate,
            Discount: discount,
            TaxRate: taxRate,
            Tax: tax,
            GrandTotal: grandTotal);

        order.InvoiceJson = JsonSerializer.Serialize(invoice, InvoiceOptions);
        order.TotalAmount = grandTotal;
        order.Payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = order.Id,
            Amount = grandTotal,
            Status = ok ? PaymentStatus.Paid : PaymentStatus.Failed,
        };

        if (_extraCpuIterations > 0)
            _cpuSink = BurnCpu(_extraCpuIterations);

        order.ProcessedAt = DateTimeOffset.UtcNow;
        order.ProcessingMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Optional CPU burn (off by default) to turn this back into a CPU-bound stress test.</summary>
    private static double BurnCpu(int iterations)
    {
        double acc = 0;
        for (int i = 1; i <= iterations; i++)
            acc += Math.Sqrt(i) * Math.Sin(i) + Math.Log(i);
        return acc;
    }

    private record Invoice(
        string InvoiceNumber, DateTimeOffset IssuedAt, int UserId, string CustomerName,
        string CustomerEmail, string Currency, IReadOnlyList<InvoiceLine> Lines,
        decimal Subtotal, decimal DiscountRate, decimal Discount, decimal TaxRate,
        decimal Tax, decimal GrandTotal);

    private record InvoiceLine(int ProductId, string Sku, string Description,
        int Quantity, decimal UnitPrice, decimal LineTotal);
}
