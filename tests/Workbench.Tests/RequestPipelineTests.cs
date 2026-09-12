using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Extensions;
using Workbench.Models;
using Workbench.Services;

namespace Workbench.Tests;

public sealed class RequestPipelineTests
{
    [Theory]
    [InlineData(5, HttpStatusCode.OK)]
    [InlineData(1025, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Downstream_can_configure_and_enforce_request_body_limits(int bodyLength, HttpStatusCode expected)
    {
        await using var app = CreateApp();
        var completed = TrackCompletion(app);
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (BadHttpRequestException error)
            {
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsync("Request rejected");
            }
        });
        app.UseWorkbench();
        app.MapPost("/limited", async (HttpContext context) =>
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            Assert.NotNull(limit);
            Assert.False(limit.IsReadOnly);
            limit.MaxRequestBodySize = 1024;
            using var reader = new StreamReader(context.Request.Body);
            return Results.Text(await reader.ReadToEndAsync());
        });
        await app.StartAsync();
        using var client = Client(app);
        var body = new string('x', bodyLength);
        using var response = await client.PostAsync("/limited", new StringContent(body));

        Assert.Equal(expected, response.StatusCode);
        var entry = await LoggedEntry(app, completed);
        Assert.Equal((int)expected, entry.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.Equal(body, entry.RequestBody);
            Assert.Equal(bodyLength, entry.RequestSize);
        }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(422)]
    public async Task Errors_use_the_final_status_from_outer_exception_handlers(int statusCode)
    {
        await using var app = CreateApp();
        var completed = TrackCompletion(app);
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsync("Handled error");
            }
        });
        app.UseWorkbench();
        app.MapPost("/failure", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            await reader.ReadToEndAsync();
            throw new InvalidOperationException("Expected failure");
        });
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.PostAsync("/failure", new StringContent("hello"));

        Assert.Equal(statusCode, (int)response.StatusCode);
        var entry = await LoggedEntry(app, completed);
        Assert.Equal(statusCode, entry.StatusCode);
        Assert.Equal("/failure", entry.Path);
        Assert.Equal("hello", entry.RequestBody);
        Assert.Equal(5, entry.RequestSize);
    }

    [Fact]
    public async Task Reexecuted_error_pipeline_logs_the_original_request_once()
    {
        await using var app = CreateApp();
        var completed = TrackCompletion(app);
        app.UseExceptionHandler("/error");
        app.UseWorkbench();
        app.MapGet("/failure", (HttpContext _) => Task.FromException(new InvalidOperationException("Expected failure")));
        app.MapGet("/error", () => Results.StatusCode(503));
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync("/failure");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var entry = await LoggedEntry(app, completed);
        Assert.Equal(503, entry.StatusCode);
        Assert.Equal("/failure", entry.Path);
    }

    [Fact]
    public async Task Unhandled_errors_use_the_status_generated_by_kestrel()
    {
        await using var app = CreateApp();
        var completed = TrackCompletion(app);
        app.UseWorkbench();
        app.MapGet("/failure", (HttpContext _) => Task.FromException(new InvalidOperationException("Expected failure")));
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync("/failure");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(500, (await LoggedEntry(app, completed)).StatusCode);
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddWorkbench();
        return builder.Build();
    }

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Urls.Single()),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static TaskCompletionSource TrackCompletion(WebApplication app)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            context.Response.OnCompleted(() =>
            {
                completed.TrySetResult();
                return Task.CompletedTask;
            });
            return next(context);
        });
        return completed;
    }

    private static async Task<RequestLogEntry> LoggedEntry(WebApplication app, TaskCompletionSource completed)
    {
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return Assert.Single(app.Services.GetRequiredService<RequestLogCollector>().GetEntries());
    }
}
