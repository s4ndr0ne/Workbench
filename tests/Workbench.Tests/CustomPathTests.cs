using System.Net;

namespace Workbench.Tests;

public sealed class CustomPathTests : IAsyncLifetime
{
    private readonly WorkbenchHostFixture _host = WorkbenchHostFixture.At("/ops/monitor/");

    public Task InitializeAsync() => _host.InitializeAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Dashboard_is_served_at_custom_path()
    {
        var response = await _host.Client.GetAsync("/ops/monitor/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Default_path_is_not_mounted()
    {
        var response = await _host.Client.GetAsync("/workbench/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Api_is_available_under_custom_path()
    {
        var response = await _host.Client.GetAsync("/ops/monitor/api/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
