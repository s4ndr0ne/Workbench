namespace Workbench.Models;

/// <summary>
/// A single health report entry for an endpoint or service.
/// </summary>
public sealed class HealthEntry
{
    public required string Name { get; init; }

    public required string Status { get; init; }

    public string? Description { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyDictionary<string, object>? Data { get; init; }
}

/// <summary>
/// The full health report for the application.
/// </summary>
public sealed class HealthReportModel
{
    public required string Status { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<HealthEntry> Entries { get; init; }
}