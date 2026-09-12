using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Workbench.Extensions;
using Workbench.Options;

namespace Workbench.Tests;

/// <summary>
/// Boots a minimal ASP.NET Core app with Workbench mounted, backed by the in-memory TestServer.
/// </summary>
public sealed class WorkbenchHostFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public string BasePath { get; }

    public WorkbenchHostFixture() { BasePath = "/workbench"; }

    /// <summary>Creates a fixture mounted at a custom base path (used outside of xunit's class-fixture injection).</summary>
    public static WorkbenchHostFixture At(string basePath) => new(basePath);

    private WorkbenchHostFixture(string basePath) => BasePath = basePath;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("ok"));

        builder.Services.AddWorkbench(o =>
        {
            o.Path = BasePath;
            o.MetricsSampleInterval = TimeSpan.FromMilliseconds(200);
        });

        _app = builder.Build();
        _app.UseWorkbench();
        _app.MapGet("/ping", () => Results.Ok(new { pong = true }));
        _app.MapGet("/workbench-other", () => "not the dashboard");
        _app.MapPost("/echo", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            return Results.Text(await reader.ReadToEndAsync(), "application/json");
        });
        _app.MapGet("/items/{id:int}", (int id) => Results.Ok(new { id }));
        _app.MapGet("/files/{**path}", (string path) => Results.Ok(new { path }));
        _app.MapGet("/names/{name:minlength(3)}", (string name) => Results.Ok(new { name }));

        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
