using ThroughputBenchmark.LoadGenerator;

var builder = Host.CreateApplicationBuilder(args);

// Service discovery so "http://apiservice" resolves to the Aspire-managed endpoint.
// We deliberately skip the standard resilience handler here — a load generator should
// hammer the API directly, not back off via a circuit breaker.
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri("http://apiservice"));

builder.Services.AddHostedService<GeneratorService>();

var host = builder.Build();
host.Run();
