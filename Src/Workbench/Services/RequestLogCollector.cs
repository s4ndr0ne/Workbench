using System.Threading.Channels;
using Workbench.Models;

namespace Workbench.Services;

/// <summary>
/// Stores the most recent HTTP request/response log entries in a ring buffer
/// and exposes a channel for SSE consumers to receive new entries in real time.
/// </summary>
public sealed class RequestLogCollector
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<RequestLogEntry> _entries;
    private readonly Channel<RequestLogEntry> _channel;

    public RequestLogCollector(int capacity = 500)
    {
        _capacity = Math.Max(50, capacity);
        _entries = new Queue<RequestLogEntry>(_capacity);
        _channel = Channel.CreateUnbounded<RequestLogEntry>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });
    }

    public void Add(RequestLogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= _capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);
        }

        _channel.Writer.TryWrite(entry);
    }

    public IReadOnlyList<RequestLogEntry> GetEntries()
    {
        lock (_lock)
            return _entries.ToArray();
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) =>
        _channel.Reader.WaitToReadAsync(ct);

    public bool TryRead(out RequestLogEntry? entry) =>
        _channel.Reader.TryRead(out entry);
}