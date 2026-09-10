using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Workbench.Models;
using Workbench.Options;

namespace Workbench.Services;

/// <summary>
/// Captures runtime process, memory and CPU metrics in a rolling window.
/// Registered as a singleton and polled by the dashboard front-end.
/// </summary>
public sealed class WorkbenchMetricsCollector : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _lock = new();
    private readonly Queue<MetricsSample> _samples;
    private readonly Timer _timer;
    private int _cpuFraction;

    public WorkbenchMetricsCollector(IOptions<WorkbenchOptions> options)
    {
        var value = options.Value;
        _interval = value.MetricsSampleInterval;
        var capacity = Math.Max(10, (int)(value.MetricsHistory.TotalSeconds / value.MetricsSampleInterval.TotalSeconds));
        _samples = new Queue<MetricsSample>(capacity);
        _timer = new Timer(OnTick, null, _interval, _interval);
        OnTick(0);
    }

    public IReadOnlyList<MetricsSample> Samples
    {
        get
        {
            lock (_lock)
            {
                return _samples.ToArray();
            }
        }
    }

    public ProcessInfo GetProcessInfo()
    {
        var process = Process.GetCurrentProcess();
        var elapsed = DateTimeOffset.UtcNow - _startedAt;

        return new ProcessInfo
        {
            ApplicationName = Environment.GetEnvironmentVariable("ASPNETCORE_APPLICATIONNAME") ?? AppDomain.CurrentDomain.FriendlyName,
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId.ToString("D5"),
            StartedAtUtc = _startedAt.ToString("O"),
            Uptime = FormatUptime(elapsed),
            WorkingSetMb = ToMb(process.WorkingSet64),
            PrivateMemoryMb = ToMb(process.PrivateMemorySize64),
            VirtualMemoryMb = ToMb(process.VirtualMemorySize64),
            ThreadCount = process.Threads.Count,
            RuntimeVersion = Environment.Version.ToString(),
            OperatingSystem = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            WorkingDirectory = Environment.CurrentDirectory,
        };
    }

    public RuntimeMetricsInfo GetRuntimeMetrics()
    {
        var gc = GC.GetGCMemoryInfo();
        var uptimeSeconds = Math.Max(1, (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds);
        int cpuPercent;
        lock (_lock)
        {
            cpuPercent = _cpuFraction;
        }

        return new RuntimeMetricsInfo
        {
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalBytesAllocatedMb = ToMbNumber(gc.TotalCommittedBytes),
            HeapSizeMb = ToMbNumber(gc.HeapSizeBytes),
            TimeInGcPercent = Math.Round(SumPauseDurations(gc) * 100d / uptimeSeconds, 2),
            CpuUsagePercent = Math.Round(cpuPercent / 100d, 1),
        };
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    private void OnTick(object? state)
    {
        var sample = new MetricsSample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuPercent = ReadCpuPercent(),
            WorkingSetMb = ToMbNumber(Process.GetCurrentProcess().WorkingSet64),
            ManagedHeapMb = ToMbNumber(GC.GetGCMemoryInfo().HeapSizeBytes),
        };

        lock (_lock)
        {
            _cpuFraction = (int)Math.Round(sample.CpuPercent * 100);

            if (_samples.Count >= SampleCapacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
        }
    }

    private int SampleCapacity => Math.Max(10, (int)(_interval.TotalSeconds * 2 * 60));

    private static double ReadCpuPercent()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var startTime = process.TotalProcessorTime;
            var start = DateTime.UtcNow;
            Thread.Sleep(50);
            process.Refresh();
            var endTime = process.TotalProcessorTime;
            var elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
            var cpuMs = (endTime - startTime).TotalMilliseconds;
            return Math.Round(cpuMs / elapsedMs * 100d, 2);
        }
        catch
        {
            return 0;
        }
    }

    private static string ToMb(long bytes) => ToMbNumber(bytes).ToString("0.0", CultureInfo.InvariantCulture);

    private static double SumPauseDurations(GCMemoryInfo gc)
    {
        var total = 0.0;
        foreach (var pause in gc.PauseDurations)
        {
            total += pause.TotalSeconds;
        }
        return total;
    }

    private static double ToMbNumber(long bytes) => Math.Round(bytes / 1024d / 1024d, 1);

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours:00}h {uptime.Minutes:00}m";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes:00}m {uptime.Seconds:00}s";
        }

        return $"{(int)uptime.TotalMinutes}m {uptime.Seconds:00}s";
    }
}

/// <summary>
/// A single point-in-time metrics reading.
/// </summary>
public sealed class MetricsSample
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required double CpuPercent { get; init; }

    public required double WorkingSetMb { get; init; }

    public required double ManagedHeapMb { get; init; }
}