namespace Workbench.Models;

/// <summary>
/// Runtime information about the hosting process.
/// </summary>
public sealed class ProcessInfo
{
    public required string ApplicationName { get; init; }

    public required string Environment { get; init; }

    public required string MachineName { get; init; }

    public required string ProcessId { get; init; }

    public required string StartedAtUtc { get; init; }

    public required string Uptime { get; init; }

    public required string WorkingSetMb { get; init; }

    public required string PrivateMemoryMb { get; init; }

    public required string VirtualMemoryMb { get; init; }

    public required int ThreadCount { get; init; }

    public required string RuntimeVersion { get; init; }

    public required string OperatingSystem { get; init; }

    public required string WorkingDirectory { get; init; }
}