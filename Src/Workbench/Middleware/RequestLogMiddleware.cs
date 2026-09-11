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
        _workbenchPath = options.Value.Path.TrimEnd('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "";

        if (Array.Exists(SkippedMethods, m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
            || path.StartsWith(_workbenchPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Buffer the request body so downstream can still read it.
        string? requestBody = null;
        long requestSize = 0;
        var requestContentType = context.Request.ContentType ?? "";

        if (context.Request.ContentLength is > 0
            && context.Request.ContentLength <= MaxBodyBytes
            && !requestContentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);

            requestBody = await reader.ReadToEndAsync(context.RequestAborted);
            requestSize = Encoding.UTF8.GetByteCount(requestBody);
            if (requestSize > MaxBodyBytes)
            {
                requestBody = requestBody[..(int)(MaxBodyBytes / 2)] + "\n… (body truncated)";
            }
            context.Request.Body.Position = 0;
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