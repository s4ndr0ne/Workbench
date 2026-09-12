using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Extensions;

namespace Workbench.Tests;

public sealed class RootPathTests
{
    [Fact]
    public async Task Endpoint_discovery_excludes_routes_intercepted_by_root_dashboard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWorkbench(options =>
        {
            options.Path = "/";
            options.Authorize = _ => true;
        });

        var app = builder.Build();
        app.UseWorkbench();
        app.MapGet("/ping", () => Microsoft.AspNetCore.Http.Results.Ok());
        await app.StartAsync();

        try
        {
            var response = await app.GetTestClient().GetAsync("/api/endpoints");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

            Assert.Empty(document.RootElement.EnumerateArray());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}