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
    /// Adds Workbench services (metrics collector) with default options.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services)
    {
        services.Configure<WorkbenchOptions>(_ => { });
        services.TryAddSingleton<WorkbenchMetricsCollector>();
        return services;
    }

    /// <summary>
    /// Adds Workbench services and optionally configures options.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services, Action<WorkbenchOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<WorkbenchMetricsCollector>();
        return services;
    }

    /// <summary>
    /// Adds Workbench services with default options.
    /// </summary>
    public static IServiceCollection AddWorkbench(this IServiceCollection services, WorkbenchOptions options)
    {
        return services.AddWorkbench(o =>
        {
            o.Path = options.Path;
            o.MetricsSampleInterval = options.MetricsSampleInterval;
            o.MetricsHistory = options.MetricsHistory;
            o.EnableHealthReport = options.EnableHealthReport;
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
    /// Must be called after <c>AddWorkbench</c>.
    /// </summary>
    public static IApplicationBuilder UseWorkbench(this IApplicationBuilder app)
    {
        return app.UseMiddleware<WorkbenchMiddleware>();
    }
}