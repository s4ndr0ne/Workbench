using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// A few health checks so the dashboard has something to show.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("The API is responding."))
    .AddCheck("memory", () =>
    {
        var mb = GC.GetTotalMemory(false) / 1024d / 1024d;
        return mb < 512
            ? HealthCheckResult.Healthy($"{mb:0.0} MB allocated", new Dictionary<string, object> { ["allocatedMb"] = Math.Round(mb, 1) })
            : HealthCheckResult.Degraded($"{mb:0.0} MB allocated");
    })
    .AddCheck("downstream-api", () => Random.Shared.Next(10) == 0
        ? HealthCheckResult.Degraded("Slow response from partner API")
        : HealthCheckResult.Healthy("Partner API reachable"));

builder.Services.AddWorkbench(options =>
{
    options.Path = "/workbench";
    options.MetricsSampleInterval = TimeSpan.FromSeconds(2);
    options.MetricsHistory = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

// Mount Workbench early so the request log sees every request.
app.UseWorkbench();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

// Minimal-API endpoints show up in the API explorer too.
app.MapGet("/ping", () => Results.Ok(new { pong = true, at = DateTimeOffset.UtcNow }));
app.MapPost("/echo", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Text(body, request.ContentType ?? "text/plain");
});
app.MapDelete("/items/{id:guid}", (Guid id) => Results.NoContent());

app.Run();
