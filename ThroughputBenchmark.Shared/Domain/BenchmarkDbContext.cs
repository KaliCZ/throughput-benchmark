using Microsoft.EntityFrameworkCore;

namespace ThroughputBenchmark.Shared.Domain;

public class BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<BenchmarkRun> BenchmarkRuns => Set<BenchmarkRun>();
    public DbSet<BenchmarkSample> BenchmarkSamples => Set<BenchmarkSample>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);

        b.Entity<Order>(e =>
        {
            e.Property(o => o.TotalAmount).HasPrecision(18, 2);
            // The sampler counts orders processed since the last tick via a ProcessedAt
            // watermark; this composite index makes that an O(delta) range scan.
            e.HasIndex(o => new { o.RunId, o.Status, o.ProcessedAt });
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId);
            e.HasOne(o => o.Payment).WithOne().HasForeignKey<Payment>(p => p.OrderId);
        });

        b.Entity<OrderItem>(e =>
        {
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
            e.Property(i => i.LineTotal).HasPrecision(18, 2);
        });

        b.Entity<Payment>().Property(p => p.Amount).HasPrecision(18, 2);

        b.Entity<BenchmarkSample>().HasIndex(s => new { s.RunId, s.SampledAt });
    }
}
