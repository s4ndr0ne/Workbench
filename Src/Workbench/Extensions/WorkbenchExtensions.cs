using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Middleware;
using Workbench.Options;
using Workbench.Services;

namespace Workbench.Extensions;

/// <summary>
/// Provides the <c>AddWorkbench</c> and <c>UseWorkbench</c> extension methods.
/// </summary>
public static class WorkbenchBuilderExtensions
{
    /// <summary>
    /// Adds Workbench services (metrics collector, request log) with default options.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services)
    {
        services.Configure<WorkbenchOptions>(_ => { });
        services.TryAddSingleton<WorkbenchMetricsCollector>();
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkbenchOptions>>().Value;
            return new RequestLogCollector(opts.RequestLogCapacity);
        });
        return services;
    }

    /// <summary>
    /// Adds Workbench services and optionally configures options.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services, Action<WorkbenchOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<WorkbenchMetricsCollector>();
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkbenchOptions>>().Value;
            return new RequestLogCollector(opts.RequestLogCapacity);
        });
        return services;
    }

    /// <summary>
    /// Adds Workbench services with explicit options instance.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services, WorkbenchOptions options)
    {
        return services.AddWorkbench(o =>
        {
            o.Path = options.Path;
            o.MetricsSampleInterval = options.MetricsSampleInterval;
            o.MetricsHistory = options.MetricsHistory;
            o.EnableHealthReport = options.EnableHealthReport;
            o.RequestLogCapacity = options.RequestLogCapacity;
            o.CaptureRequestBody = options.CaptureRequestBody;
        });
    }
}

/// <summary>
/// Provides the <c>UseWorkbench</c> endpoint mapping extension.
/// </summary>
public static class WorkbenchEndpointExtensions
{
    /// <summary>
    /// Maps the Workbench dashboard at the configured path (default: <c>/workbench</c>).
    /// Also installs a request-log middleware that must sit before routing.
    /// Call this early, before <c>MapControllers</c> / <c>MapGrpcService</c>.
    /// </summary>
    public static IApplicationBuilder UseWorkbench(this IApplicationBuilder app)
    {
        return app
            .UseMiddleware<RequestLogMiddleware>()
            .UseMiddleware<WorkbenchMiddleware>();
    }
}