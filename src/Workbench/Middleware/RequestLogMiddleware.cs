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

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> from the stream and decodes it as UTF-8.
    /// Returns the (possibly truncated) text and the number of bytes actually consumed.
    /// </summary>
    private static async Task<(string? Body, long Size)> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
               && (read = await body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }

        if (total == 0)
            return (null, 0);

        if (total > MaxBodyBytes)
        {
            var text = Encoding.UTF8.GetString(buffer, 0, (int)MaxBodyBytes) + "\n… (body truncated)";
            return (text, total);
        }

        return (Encoding.UTF8.GetString(buffer, 0, total), total);
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
            || context.Request.Path.StartsWithSegments(_workbenchPath))
        {
            await _next(context);
            return;
        }

        // Buffer the request body so downstream can still read it.
        string? requestBody = null;
        long requestSize = 0;
        var requestContentType = context.Request.ContentType ?? "";

        if (_captureBody && ShouldCaptureBody(context.Request, requestContentType))
        {
            context.Request.EnableBuffering();
            (requestBody, requestSize) = await ReadBodyAsync(context.Request.Body, context.RequestAborted);
            context.Request.Body.Position = 0;
        }
        else if (context.Request.ContentLength is > 0)
        {
            requestSize = context.Request.ContentLength.Value;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
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