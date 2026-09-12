using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Models;
using Workbench.Options;
using Workbench.Services;

namespace Workbench.Middleware;

/// <summary>
/// Captures every inbound HTTP request — method, path, body, status code,
/// duration — and stores it in the <see cref="RequestLogCollector"/>.
/// Installed by <c>UseWorkbench</c>; requests under the Workbench base path
/// are excluded to avoid self-noise.
/// </summary>
public sealed class RequestLogMiddleware
{
    private const long MaxBodyBytes = 64 * 1024;

    private readonly RequestDelegate _next;
    private readonly RequestLogCollector _collector;
    private readonly ILogger<RequestLogMiddleware> _logger;
    private readonly string _workbenchPath;
    private readonly bool _captureBody;
    private static readonly string[] SkippedMethods = ["OPTIONS"];
    private static readonly object PendingErrorLogKey = new();

    public RequestLogMiddleware(
        RequestDelegate next,
        RequestLogCollector collector,
        ILogger<RequestLogMiddleware> logger,
        IOptions<WorkbenchOptions> options)
    {
        _next = next;
        _collector = collector;
        _logger = logger;
        _workbenchPath = NormalizeBasePath(options.Value.Path);
        _captureBody = options.Value.CaptureRequestBody;
    }

    private static bool ShouldCaptureBody(HttpRequest request, string contentType)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return false;

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return false;

        // ContentLength is null for chunked/streamed bodies: still worth a bounded read.
        return request.ContentLength switch
        {
            null => true,
            > 0 and <= MaxBodyBytes => true,
            _ => false,
        };
    }

    private static string NormalizeBasePath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? "/workbench"
            : "/" + path.Trim().Trim('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "";

        if (Array.Exists(SkippedMethods, m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
            || context.Request.Path.StartsWithSegments(_workbenchPath)
            || context.Items.ContainsKey(PendingErrorLogKey))
        {
            await _next(context);
            return;
        }

        // Capture only bytes read by the application, without starting body consumption here.
        string? requestBody = null;
        long requestSize = 0;
        var requestContentType = context.Request.ContentType ?? "";

        RequestBodyCaptureStream? streamingCapture = null;
        var originalBody = context.Request.Body;

        if (_captureBody && ShouldCaptureBody(context.Request, requestContentType))
        {
            streamingCapture = new RequestBodyCaptureStream(originalBody, MaxBodyBytes);
            context.Request.Body = streamingCapture;
        }
        else if (context.Request.ContentLength is > 0)
        {
            requestSize = context.Request.ContentLength.Value;
        }

        var sw = Stopwatch.StartNew();
        var failed = false;

        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (streamingCapture is not null && ReferenceEquals(context.Request.Body, streamingCapture))
                context.Request.Body = originalBody;

            if (streamingCapture is not null)
            {
                requestBody = streamingCapture.Body;
                requestSize = streamingCapture.BytesRead;
            }

            if (failed)
            {
                // Outer exception handlers decide the final status and may re-execute this pipeline.
                context.Items[PendingErrorLogKey] = true;
                context.Response.OnCompleted(() =>
                {
                    RecordEntry();
                    return Task.CompletedTask;
                });
            }
            else
            {
                RecordEntry();
            }
        }

        void RecordEntry()
        {
            sw.Stop();

            var entry = new RequestLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Method = method,
                Path = path,
                StatusCode = context.Response.StatusCode,
                DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                ContentType = requestContentType,
                RequestSize = requestSize,
                RequestBody = requestBody,
                RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "-",
                TraceId = context.TraceIdentifier,
            };

            _collector.Add(entry);

            _logger.LogDebug(
                "{Method} {Path} -> {Status} in {Duration}ms body={BodyLen}",
                method, path, context.Response.StatusCode,
                Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                requestSize);
        }
    }
}

internal sealed class RequestBodyCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _buffer;
    private readonly System.Collections.BitArray? _capturedPositions;
    private int _captured;

    public RequestBodyCaptureStream(Stream inner, long maximumBytes)
    {
        _inner = inner;
        _buffer = new byte[checked((int)maximumBytes + 1)];
        if (inner.CanSeek)
            _capturedPositions = new System.Collections.BitArray(_buffer.Length);
    }

    public long BytesRead { get; private set; }

    public string? Body
    {
        get
        {
            if (_captured == 0)
                return null;

            var length = Math.Min(_captured, _buffer.Length - 1);
            var body = Encoding.UTF8.GetString(_buffer, 0, length);
            return _captured == _buffer.Length ? body + "\n… (body truncated)" : body;
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    private long ReadPosition => _inner.CanSeek ? _inner.Position : BytesRead;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var position = ReadPosition;
        var read = _inner.Read(buffer, offset, count);
        Capture(buffer.AsSpan(offset, read), position);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var position = ReadPosition;
        var read = _inner.Read(buffer);
        Capture(buffer[..read], position);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var position = ReadPosition;
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Capture(buffer.AsSpan(offset, read), position);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var position = ReadPosition;
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Capture(buffer.Span[..read], position);
        return read;
    }

    public override int ReadByte()
    {
        var position = ReadPosition;
        var value = _inner.ReadByte();
        if (value >= 0)
            Capture(stackalloc byte[] { (byte)value }, position);
        return value;
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        // The request pipeline owns the wrapped stream.
    }

    private void Capture(ReadOnlySpan<byte> data, long position)
    {
        if (data.IsEmpty)
            return;

        // Rewinds must not count the same body bytes twice.
        BytesRead = Math.Max(BytesRead, position + data.Length);
        if (position >= _buffer.Length)
            return;

        var start = (int)position;
        var length = Math.Min(data.Length, _buffer.Length - start);
        data[..length].CopyTo(_buffer.AsSpan(start));

        if (_capturedPositions is null)
        {
            _captured = start + length;
            return;
        }

        for (var i = start; i < start + length; i++)
            _capturedPositions[i] = true;

        // Only expose the contiguous prefix; forward seeks may leave unread gaps.
        while (_captured < _buffer.Length && _capturedPositions[_captured])
            _captured++;
    }
}