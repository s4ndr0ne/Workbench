using System.Threading.Channels;
using Workbench.Models;

namespace Workbench.Services;

/// <summary>
/// Stores the most recent HTTP request/response log entries in a ring buffer
/// and exposes bounded subscriptions for SSE consumers to receive new entries in real time.
/// </summary>
public sealed class RequestLogCollector
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<RequestLogEntry> _entries;
    private readonly Dictionary<long, Channel<RequestLogEntry>> _subscribers = new();
    private long _nextSubscriberId;

    public RequestLogCollector(int capacity = 500)
    {
        _capacity = Math.Max(50, capacity);
        _entries = new Queue<RequestLogEntry>(_capacity);
    }

    public void Add(RequestLogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= _capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);

            foreach (var subscriber in _subscribers.Values)
                subscriber.Writer.TryWrite(entry);
        }
    }

    public IReadOnlyList<RequestLogEntry> GetEntries()
    {
        lock (_lock)
            return _entries.ToArray();
    }

    public RequestLogSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(_capacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_lock)
        {
            var id = ++_nextSubscriberId;
            _subscribers.Add(id, channel);
            return new RequestLogSubscription(this, id, channel.Reader);
        }
    }

    private void Unsubscribe(long id)
    {
        Channel<RequestLogEntry>? channel;
        lock (_lock)
        {
            if (!_subscribers.Remove(id, out channel))
                return;
        }

        channel.Writer.TryComplete();
    }

    public sealed class RequestLogSubscription : IDisposable
    {
        private readonly RequestLogCollector _owner;
        private readonly long _id;
        private readonly ChannelReader<RequestLogEntry> _reader;
        private int _disposed;

        internal RequestLogSubscription(
            RequestLogCollector owner,
            long id,
            ChannelReader<RequestLogEntry> reader)
        {
            _owner = owner;
            _id = id;
            _reader = reader;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
            _reader.WaitToReadAsync(cancellationToken);

        public bool TryRead(out RequestLogEntry? entry) => _reader.TryRead(out entry);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Unsubscribe(_id);
        }
    }
}