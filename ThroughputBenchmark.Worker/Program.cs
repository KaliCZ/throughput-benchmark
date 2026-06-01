using ThroughputBenchmark.Shared.Domain;
using ThroughputBenchmark.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Aspire-wired infrastructure (connection strings come from the AppHost).
// Bound the Npgsql pool per process so N worker processes don't exhaust Postgres connections.
int maxPool = builder.Configuration.GetValue("Db:MaxPoolSize", Environment.ProcessorCount + 5);
builder.AddNpgsqlDbContext<BenchmarkDbContext>("benchmarkdb", settings =>
{
    if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        settings.ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            MaxPoolSize = maxPool,
            MinPoolSize = 1,
        }.ConnectionString;
});
builder.AddRabbitMQClient("messaging");

builder.Services.AddScoped<OrderProcessor>();
builder.Services.AddHostedService<QueueConsumerService>();

var host = builder.Build();
host.Run();
