using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Extensions;
using Workbench.Middleware;
using Workbench.Models;
using Workbench.Services;

namespace Workbench.Tests;

public sealed class CollectorTests
{
    [Fact]
    public void Subscribers_receive_independent_bounded_event_streams()
    {
        var collector = new RequestLogCollector(50);
        collector.Add(Entry(0));

        using var first = collector.Subscribe();
        using var second = collector.Subscribe();
        Assert.False(first.TryRead(out _));
        Assert.False(second.TryRead(out _));

        for (var i = 0; i < 60; i++)
            collector.Add(Entry(i));

        var firstEntries = Drain(first);
        var secondEntries = Drain(second);

        Assert.Equal(50, firstEntries.Count);
        Assert.Equal(firstEntries.Select(e => e.TraceId), secondEntries.Select(e => e.TraceId));
        Assert.Equal("10", firstEntries[0].TraceId);
        Assert.Equal("59", firstEntries[^1].TraceId);
    }

    [Theory]
    [InlineData(0, 600, 500)]
    [InlineData(50, 600, 500)]
    [InlineData(200, 100, 500)]
    [InlineData(200, 600, 49)]
    [InlineData(100, 604_800_000, 500)]
    public void Invalid_options_are_rejected(int intervalMs, int historyMs, int requestLogCapacity)
    {
        var services = new ServiceCollection();
        services.AddWorkbench(options =>
        {
            options.MetricsSampleInterval = TimeSpan.FromMilliseconds(intervalMs);
            options.MetricsHistory = TimeSpan.FromMilliseconds(historyMs);
            options.RequestLogCapacity = requestLogCapacity;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<WorkbenchMetricsCollector>());
    }

    [Fact]
    public void Embedded_resources_return_independent_streams()
    {
        using var first = EmbeddedAssets.GetResource("index.html");
        using var second = EmbeddedAssets.GetResource("index.html");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        var firstByte = first.ReadByte();
        Assert.Equal(0, second.Position);
        Assert.Equal(firstByte, second.ReadByte());
    }

    [Fact]
    public void Embedded_dashboard_bounds_paused_request_buffer()
    {
        using var stream = EmbeddedAssets.GetResource("index.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        Assert.Contains("pausedBuffer = mergeEntries(pausedBuffer,", html);
        Assert.Contains(".slice(0, MAX_LOG)", html);
        Assert.DoesNotContain("pausedBuffer.reverse()", html);
    }

    private static List<RequestLogEntry> Drain(RequestLogCollector.RequestLogSubscription subscription)
    {
        var entries = new List<RequestLogEntry>();
        while (subscription.TryRead(out var entry))
            entries.Add(entry!);
        return entries;
    }

    private static RequestLogEntry Entry(int id) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        Method = "GET",
        Path = "/test",
        StatusCode = 200,
        DurationMs = 1,
        ContentType = "",
        RequestSize = 0,
        RemoteIp = "-",
        TraceId = id.ToString(),
    };
}
