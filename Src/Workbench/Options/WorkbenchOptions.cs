namespace Workbench.Options;

/// <summary>
/// Configuration options for the Workbench dashboard.
/// </summary>
public sealed class WorkbenchOptions
{
    /// <summary>
    /// Root path under which the dashboard is served. Defaults to <c>/workbench</c>.
    /// </summary>
    public string Path { get; set; } = "/workbench";

    /// <summary>
    /// How often the collector samples runtime metrics. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MetricsSampleInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far back in time the metrics history window extends. Defaults to 10 minutes.
    /// </summary>
    public TimeSpan MetricsHistory { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Controls whether the Health Report endpoint is enabled. Defaults to <c>true</c>.
    /// </summary>
    public bool EnableHealthReport { get; set; } = true;
}