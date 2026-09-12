using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Workbench.Middleware;
using Workbench.Options;

namespace Workbench.Tests;

public sealed class DashboardTests : IClassFixture<WorkbenchHostFixture>
{
    private readonly WorkbenchHostFixture _host;

    public DashboardTests(WorkbenchHostFixture host) => _host = host;

    [Fact]
    public async Task Root_without_trailing_slash_redirects_to_dashboard()
    {
        var response = await _host.Client.GetAsync("/workbench");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("/workbench/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Redirect_preserves_path_base()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/app";
        context.Request.Path = "/workbench";
        var middleware = new WorkbenchMiddleware(
            _ => Task.CompletedTask,
            Microsoft.Extensions.Options.Options.Create(new WorkbenchOptions { Authorize = _ => true }));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
        Assert.Equal("/app/workbench/", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Dashboard_serves_embedded_index_html()
    {
        var response = await _host.Client.GetAsync("/workbench/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<title>Workbench</title>", html);
        Assert.Contains("<base href=\"/workbench/\">", html);
    }

    [Fact]
    public async Task Unknown_asset_falls_back_to_index_html()
    {
        var response = await _host.Client.GetAsync("/workbench/does-not-exist");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Workbench</title>", html);
    }

    [Fact]
    public async Task Similar_path_is_not_intercepted_and_is_logged()
    {
        var response = await _host.Client.GetAsync("/workbench-other");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("not the dashboard", await response.Content.ReadAsStringAsync());

        using var doc = await GetJson("/workbench/api/overview");
        var entries = doc.RootElement.GetProperty("recentRequests").EnumerateArray();
        Assert.Contains(entries, e => e.GetProperty("path").GetString() == "/workbench-other");
    }

    [Fact]
    public async Task Overview_returns_process_runtime_and_samples()
    {
        using var doc = await GetJson("/workbench/api/overview");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("process", out var process));
        Assert.True(root.TryGetProperty("runtime", out _));
        Assert.True(root.TryGetProperty("samples", out var samples));
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        Assert.True(samples.GetArrayLength() >= 1);
        Assert.Equal(Environment.ProcessId.ToString("D5"), process.GetProperty("processId").GetString());
        Assert.Equal("Healthy", root.GetProperty("healthStatus").GetString());
    }

    [Fact]
    public async Task Health_reports_registered_checks()
    {
        using var doc = await GetJson("/workbench/api/health");
        var root = doc.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal("self", entry.GetProperty("name").GetString());
        Assert.Equal("ok", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Endpoints_lists_app_routes_but_not_workbench_itself()
    {
        using var doc = await GetJson("/workbench/api/endpoints");
        var endpoints = doc.RootElement.EnumerateArray().ToList();

        Assert.Contains(endpoints, e => e.GetProperty("method").GetString() == "GET" && e.GetProperty("path").GetString() == "/ping");
        Assert.Contains(endpoints, e => e.GetProperty("method").GetString() == "POST" && e.GetProperty("path").GetString() == "/echo");
        Assert.Contains(endpoints, e => e.GetProperty("path").GetString() == "/workbench-other");
        Assert.DoesNotContain(endpoints, e =>
        {
            var path = e.GetProperty("path").GetString()!;
            return path.Equals("/workbench", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/workbench/", StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal("id", SingleParameter(endpoints, "/items/{id:int}"));
        Assert.Equal("path", SingleParameter(endpoints, "/files/{**path}"));
        Assert.Equal("name", SingleParameter(endpoints, "/names/{name:minlength(3)}"));
    }

    [Fact]
    public async Task Api_rejects_non_get_methods()
    {
        var response = await _host.Client.PostAsync("/workbench/api/overview", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_endpoint_returns_404()
    {
        var response = await _host.Client.GetAsync("/workbench/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Request_log_captures_calls_with_body_and_skips_workbench_traffic()
    {
        var marker = Guid.NewGuid().ToString("N");
        var payload = new { marker };

        var echo = await _host.Client.PostAsJsonAsync("/echo", payload);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Contains(marker, await echo.Content.ReadAsStringAsync()); // body was re-readable downstream

        await _host.Client.GetAsync("/workbench/api/health");

        using var doc = await GetJson("/workbench/api/overview");
        var entries = doc.RootElement.GetProperty("recentRequests").EnumerateArray().ToList();

        var logged = Assert.Single(entries, e => e.TryGetProperty("requestBody", out var body)
                                                  && body.ValueKind == JsonValueKind.String
                                                  && body.GetString()!.Contains(marker));
        Assert.Equal("POST", logged.GetProperty("method").GetString());
        Assert.Equal("/echo", logged.GetProperty("path").GetString());
        Assert.Equal(200, logged.GetProperty("statusCode").GetInt32());
        Assert.DoesNotContain(entries, e => e.GetProperty("path").GetString()!.StartsWith("/workbench/"));
    }

    [Fact]
    public async Task Chunked_body_capture_keeps_the_full_64_kb_limit()
    {
        var payload = new string('x', 70 * 1024);
        var echo = await _host.Client.PostAsync("/echo", new StreamingContent(payload));

        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal(payload, await echo.Content.ReadAsStringAsync());

        using var doc = await GetJson("/workbench/api/overview");
        var logged = doc.RootElement.GetProperty("recentRequests").EnumerateArray()
            .Last(e => e.GetProperty("path").GetString() == "/echo");
        var body = logged.GetProperty("requestBody").GetString()!;

        Assert.Equal(70 * 1024, logged.GetProperty("requestSize").GetInt64());
        Assert.StartsWith(new string('x', 64 * 1024), body);
        Assert.EndsWith("\n… (body truncated)", body);
    }

    [Fact]
    public async Task Stream_sends_request_history_only_in_initial_overview()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/workbench/api/stream");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _host.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var overviewCount = 0;
        string? eventName = null;
        while (overviewCount < 2)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
            }
            else if (eventName == "overview" && line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var payload = JsonDocument.Parse(line[6..]);
                overviewCount++;
                Assert.Equal(
                    overviewCount == 1,
                    payload.RootElement.TryGetProperty("recentRequests", out _));
            }
        }
    }

    private static string? SingleParameter(IEnumerable<JsonElement> endpoints, string path)
    {
        var endpoint = endpoints.Single(e => e.GetProperty("path").GetString() == path);
        return Assert.Single(endpoint.GetProperty("parameters").EnumerateArray()).GetString();
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        var response = await _host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    }

    private sealed class StreamingContent(string content) : HttpContent
    {
        private readonly byte[] _content = System.Text.Encoding.UTF8.GetBytes(content);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
