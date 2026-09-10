namespace Workbench.Models;

/// <summary>
/// Snapshot of the runtime GC and memory metrics.
/// </summary>
public sealed class RuntimeMetricsInfo
{
    public required long Gen0Collections { get; init; }

    public required long Gen1Collections { get; init; }

    public required long Gen2Collections { get; init; }

    public required double TotalBytesAllocatedMb { get; init; }

    public required double HeapSizeMb { get; init; }

    public required double TimeInGcPercent { get; init; }

    public required double CpuUsagePercent { get; init; }
}