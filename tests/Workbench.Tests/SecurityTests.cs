using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workbench.Extensions;

namespace Workbench.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Dashboard_rejects_anonymous_requests_in_production()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddWorkbench();

        var app = builder.Build();
        app.UseWorkbench();
        await app.StartAsync();

        try
        {
            var response = await app.GetTestClient().GetAsync("/workbench/");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}