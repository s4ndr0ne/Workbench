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

    /// <summary>
    /// Maximum number of request-log entries kept in memory. Defaults to 500.
    /// </summary>
    public int RequestLogCapacity { get; set; } = 500;

    /// <summary>
    /// When <c>true</c>, body bytes read by the application are captured and included in the log,
    /// retaining at most 64 KB. Known-length bodies larger than 64 KB and multipart bodies are excluded.
    /// Does not pre-read the body or enable buffering. Defaults to <c>true</c>.
    /// </summary>
    public bool CaptureRequestBody { get; set; } = true;
}