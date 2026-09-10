using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text;
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

        if (!requestPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relative = requestPath[_basePath.Length..].TrimStart('/');

        if (relative.Length == 0 && !requestPath.EndsWith('/'))
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = _basePath + "/";
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
                await WriteJson(context, BuildOverview(context));
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

    private static async Task StreamEvents(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
        var interval = options.MetricsSampleInterval;

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        if (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteEvent(context, "overview", BuildOverview(context));
            if (options.EnableHealthReport)
            {
                await WriteEvent(context, "health", await BuildHealthReport(context));
            }
        }

        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(context.RequestAborted))
            {
                await WriteEvent(context, "overview", BuildOverview(context));
                if (options.EnableHealthReport)
                {
                    await WriteEvent(context, "health", await BuildHealthReport(context));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — stop the stream.
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static async Task WriteEvent(HttpContext context, string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n");
        await context.Response.Body.FlushAsync();
    }

    private static object BuildOverview(HttpContext context)
    {
        var collector = context.RequestServices.GetRequiredService<WorkbenchMetricsCollector>();
        return new
        {
            Process = collector.GetProcessInfo(),
            Runtime = collector.GetRuntimeMetrics(),
            Samples = collector.Samples,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            RefreshIntervalMs = (int)collectorSampleInterval(context).TotalMilliseconds,
            HealthStatus = healthStatusSummary(context),
        };
    }

    private static TimeSpan collectorSampleInterval(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
        return options.MetricsSampleInterval;
    }

    private static string healthStatusSummary(HttpContext context)
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
            var report = service.CheckHealthAsync().GetAwaiter().GetResult();
            return HealthStatusExtension.Format(report.Status);
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

        var report = await service.CheckHealthAsync();

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

    private static async Task ServeDashboard(HttpContext context, string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            relative = "index.html";
        }

        var resourceStream = EmbeddedAssets.GetResource(relative) ?? EmbeddedAssets.GetResource("index.html");
        if (resourceStream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = ContentTypes.Get(relative);
        context.Response.StatusCode = StatusCodes.Status200OK;
        await resourceStream.CopyToAsync(context.Response.Body);
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
    private static readonly IReadOnlyDictionary<string, Stream> ResourceCache = BuildResourceCache();

    public static Stream? GetResource(string relativePath)
    {
        if (ResourceCache.TryGetValue(Normalize(relativePath), out var stream))
        {
            stream.Position = 0;
            return stream;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, Stream> BuildResourceCache()
    {
        var rootNs = ThisAssembly.GetName().Name + ".wwwroot";
        var map = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ThisAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(rootNs, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = name[(rootNs.Length + 1)..];
            var stream = ThisAssembly.GetManifestResourceStream(name);
            if (stream != null)
            {
                map[Normalize(relative)] = stream;
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