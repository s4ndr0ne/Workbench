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
