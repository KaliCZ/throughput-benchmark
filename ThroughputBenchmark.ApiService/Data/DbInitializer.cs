using Microsoft.EntityFrameworkCore;
using ThroughputBenchmark.Shared.Domain;

namespace ThroughputBenchmark.ApiService.Data;

/// <summary>Creates the schema and seeds the "setup" data the worker reads per order.</summary>
public static class DbInitializer
{
    public const int ProductCount = 1000;
    public const int UserCount = 200;

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BenchmarkDbContext>();

        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.Products.AnyAsync(ct))
        {
            var products = Enumerable.Range(1, ProductCount).Select(i => new Product
            {
                Id = i,
                Name = $"Product {i}",
                Sku = $"SKU-{i:D5}",
                Price = 5m + (i % 100),
                StockQuantity = 1000,
            });
            db.Products.AddRange(products);
        }

        if (!await db.Users.AnyAsync(ct))
        {
            var users = Enumerable.Range(1, UserCount).Select(i => new User
            {
                Id = i,
                Name = $"User {i}",
                Email = $"user{i}@example.com",
            });
            db.Users.AddRange(users);
        }

        await db.SaveChangesAsync(ct);
    }
}
