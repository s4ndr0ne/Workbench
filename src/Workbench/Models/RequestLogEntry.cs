namespace Workbench.Models;

/// <summary>
/// A captured HTTP request/response pair.
/// </summary>
public sealed class RequestLogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required int StatusCode { get; init; }

    public required double DurationMs { get; init; }

    public required string ContentType { get; init; }

    public required long RequestSize { get; init; }

    public string? RequestBody { get; init; }

    public required string RemoteIp { get; init; }

    public required string TraceId { get; init; }
}