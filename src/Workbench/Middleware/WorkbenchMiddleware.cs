using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Models;
using Workbench.Options;
using Workbench.Services;

namespace Workbench.Middleware;

/// <summary>
/// Serves the Workbench dashboard single-page app and the JSON API underneath it.
/// </summary>
public sealed class WorkbenchMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RequestDelegate _next;
    private readonly WorkbenchOptions _options;
    private readonly string _basePath;

    public WorkbenchMiddleware(RequestDelegate next, IOptions<WorkbenchOptions> options)
    {
        _next = next;
        _options = options.Value;
        _basePath = TrimBasePath(_options.Path);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        PathString remainingPath;

        if (_basePath == "/")
        {
            remainingPath = context.Request.Path;
        }
        else if (!context.Request.Path.StartsWithSegments(_basePath, out remainingPath))
        {
            await _next(context);
            return;
        }

        if (!IsAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var relative = remainingPath.Value?.TrimStart('/') ?? string.Empty;

        if (relative.Length == 0 && !requestPath.EndsWith('/'))
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = context.Request.PathBase + _basePath + "/" + context.Request.QueryString;
            return;
        }

        if (relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleApi(context, relative[4..]);
            return;
        }

        await ServeDashboard(context, relative);
    }

    private async Task HandleApi(HttpContext context, string endpoint)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        switch (endpoint.TrimEnd('/'))
        {
            case "overview":
                await WriteJson(context, await BuildOverview(context));
                break;
            case "endpoints":
                await WriteJson(context, DiscoverEndpoints(context));
                break;
            case "health" when _options.EnableHealthReport:
                await WriteJson(context, await BuildHealthReport(context));
                break;
            case "health":
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                break;
            case "stream":
                await StreamEvents(context);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private IEnumerable<EndpointInfo> DiscoverEndpoints(HttpContext context)
    {
        var dataSources = context.RequestServices.GetServices<EndpointDataSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EndpointInfo>();

        foreach (var dataSource in dataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                if (endpoint is not RouteEndpoint routeEndpoint)
                    continue;

                var rawPath = routeEndpoint.RoutePattern.RawText ?? string.Empty;
                if (!rawPath.StartsWith('/'))
                    rawPath = "/" + rawPath;

                if (IsWorkbenchPath(rawPath, _basePath))
                    continue; // don't list workbench itself

                var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                var methods = httpMethods is null || httpMethods.Count == 0
                    ? new[] { "ANY" }
                    : httpMethods.Where(m => !string.Equals(m, "OPTIONS", StringComparison.OrdinalIgnoreCase));

                foreach (var method in methods)
                {
                    var key = $"{method} {rawPath}";
                    if (!seen.Add(key))
                        continue;

                    result.Add(new EndpointInfo
                    {
                        Name = key,
                        Method = method,
                        Path = rawPath,
                        Parameters = routeEndpoint.RoutePattern.Parameters
                            .Select(parameter => parameter.Name)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                    });
                }
            }
        }

        return result.OrderBy(e => e.Path).ThenBy(e => e.Method);
    }

    private static bool IsWorkbenchPath(string path, string basePath) =>
        path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
        || (basePath == "/"
            ? path.StartsWith('/')
            : path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase));

    private bool IsAuthorized(HttpContext context)
    {
        if (_options.Authorize is not null)
            return _options.Authorize(context);

        var environment = context.RequestServices.GetService<IHostEnvironment>();
        return environment?.IsDevelopment() == true || context.User.Identity?.IsAuthenticated == true;
    }

    private static async Task StreamEvents(HttpContext context)
    {
        var services  = context.RequestServices;
        var options   = services.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
        var requestLog = services.GetService<RequestLogCollector>();
        var interval  = options.MetricsSampleInterval;

        context.Response.Headers.ContentType   = "text/event-stream";
        context.Response.Headers.CacheControl   = "no-cache";
        context.Response.Headers.Connection     = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = requestLog?.Subscribe();
        using var timer = new PeriodicTimer(interval);
        var timerTask = timer.WaitForNextTickAsync(context.RequestAborted).AsTask();
        var logTask = subscription?.WaitToReadAsync(context.RequestAborted).AsTask();

        try
        {
            if (!context.RequestAborted.IsCancellationRequested)
            {
                await WriteEvent(
                    context,
                    "overview",
                    await BuildOverview(context, includeHealthStatus: !options.EnableHealthReport));
                if (options.EnableHealthReport)
                    await WriteEvent(context, "health", await BuildHealthReport(context));
            }

            while (!context.RequestAborted.IsCancellationRequested)
            {
                if (logTask is null)
                    await timerTask;
                else
                    await Task.WhenAny(timerTask, logTask);

                if (logTask?.IsCompleted == true)
                {
                    if (!await logTask)
                        break;

                    while (subscription!.TryRead(out var entry))
                    {
                        if (entry is not null)
                            await WriteEvent(context, "request", entry);
                    }

                    logTask = subscription.WaitToReadAsync(context.RequestAborted).AsTask();
                }

                if (timerTask.IsCompleted)
                {
                    if (!await timerTask)
                        break;

                    await WriteEvent(
                        context,
                        "overview",
                        await BuildOverview(
                            context,
                            includeRecentRequests: false,
                            includeHealthStatus: !options.EnableHealthReport));
                    if (options.EnableHealthReport)
                        await WriteEvent(context, "health", await BuildHealthReport(context));

                    timerTask = timer.WaitForNextTickAsync(context.RequestAborted).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
    }

    private static async Task WriteEvent(HttpContext context, string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n");
        await context.Response.Body.FlushAsync();
    }

    private static async Task<object> BuildOverview(
        HttpContext context,
        bool includeRecentRequests = true,
        bool includeHealthStatus = true)
    {
        var collector = context.RequestServices.GetRequiredService<WorkbenchMetricsCollector>();
        var requestLog = context.RequestServices.GetService<RequestLogCollector>();
        IReadOnlyList<RequestLogEntry>? recentRequests = includeRecentRequests
            ? requestLog?.GetEntries() ?? Array.Empty<RequestLogEntry>()
            : null;
        var healthStatus = includeHealthStatus ? await HealthStatusSummary(context) : null;

        return new
        {
            Process = collector.GetProcessInfo(),
            Runtime = collector.GetRuntimeMetrics(),
            Samples = collector.Samples,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            RefreshIntervalMs = (int)CollectorSampleInterval(context).TotalMilliseconds,
            HealthStatus = healthStatus,
            RecentRequests = recentRequests,
        };
    }

    private static TimeSpan CollectorSampleInterval(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
        return options.MetricsSampleInterval;
    }

    private static async Task<string> HealthStatusSummary(HttpContext context)
    {
        if (!context.RequestServices.GetRequiredService<IOptions<WorkbenchOptions>>().Value.EnableHealthReport)
        {
            return "Disabled";
        }

        var service = context.RequestServices.GetService<HealthCheckService>();
        if (service is null)
        {
            return "NoHealthChecks";
        }

        try
        {
            var report = await service.CheckHealthAsync(context.RequestAborted);
            return HealthStatusExtension.Format(report.Status);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "Unavailable";
        }
    }

    private static async Task<HealthReportModel> BuildHealthReport(HttpContext context)
    {
        var service = context.RequestServices.GetService<HealthCheckService>();
        if (service is null)
        {
            return new HealthReportModel
            {
                Status = "NoHealthChecks",
                GeneratedAt = DateTimeOffset.UtcNow,
                Entries = Array.Empty<HealthEntry>(),
            };
        }

        var report = await service.CheckHealthAsync(context.RequestAborted);

        var entries = report.Entries
            .Select(kv => new HealthEntry
            {
                Name = kv.Key,
                Status = HealthStatusExtension.Format(kv.Value.Status),
                Description = kv.Value.Description,
                Duration = kv.Value.Duration,
                Data = kv.Value.Data.Count > 0 ? kv.Value.Data : null,
            })
            .ToArray();

        return new HealthReportModel
        {
            Status = HealthStatusExtension.Format(report.Status),
            GeneratedAt = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }

    private async Task ServeDashboard(HttpContext context, string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            relative = "index.html";
        }

        var resourceStream = EmbeddedAssets.GetResource(relative);
        var effectivePath = relative;
        if (resourceStream is null)
        {
            resourceStream = EmbeddedAssets.GetResource("index.html");
            effectivePath = "index.html";
        }

        if (resourceStream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using (resourceStream)
        {
            context.Response.ContentType = ContentTypes.Get(effectivePath);
            context.Response.StatusCode = StatusCodes.Status200OK;
            if (effectivePath == "index.html")
            {
                using var reader = new StreamReader(resourceStream);
                var html = await reader.ReadToEndAsync();
                var baseUrl = HtmlEncoder.Default.Encode(context.Request.PathBase + _basePath + "/");
                await context.Response.WriteAsync(html.Replace("<head>", $"<head><base href=\"{baseUrl}\">"));
                return;
            }

            await resourceStream.CopyToAsync(context.Response.Body);
        }
    }

    private static async Task WriteJson(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
    }

    private static string TrimBasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/workbench";
        }

        return "/" + path.Trim().Trim('/');
    }
}

/// <summary>
/// Small formatting helpers for health check status values.
/// </summary>
public static class HealthStatusExtension
{
    private static readonly Dictionary<HealthStatus, string> Map = new()
    {
        [HealthStatus.Healthy] = "Healthy",
        [HealthStatus.Degraded] = "Degraded",
        [HealthStatus.Unhealthy] = "Unhealthy",
    };

    public static string Format(HealthStatus status) =>
        Map.TryGetValue(status, out var value) ? value : status.ToString();
}

/// <summary>
/// Resolves embedded front-end resources.
/// </summary>
public static class EmbeddedAssets
{
    private static readonly Assembly ThisAssembly = typeof(EmbeddedAssets).Assembly;
    private static readonly IReadOnlyDictionary<string, byte[]> ResourceCache = BuildResourceCache();

    public static Stream? GetResource(string relativePath)
    {
        return ResourceCache.TryGetValue(Normalize(relativePath), out var content)
            ? new MemoryStream(content, writable: false)
            : null;
    }

    private static IReadOnlyDictionary<string, byte[]> BuildResourceCache()
    {
        var rootNs = ThisAssembly.GetName().Name + ".wwwroot";
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ThisAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(rootNs, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = name[(rootNs.Length + 1)..];
            using var stream = ThisAssembly.GetManifestResourceStream(name);
            if (stream is not null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                map[Normalize(relative)] = buffer.ToArray();
            }
        }
        return map;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();
}

/// <summary>
/// Resolves HTTP content types for dashboard assets.
/// </summary>
public static class ContentTypes
{
    public static string Get(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
    }
}