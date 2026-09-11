namespace Workbench.Models;

/// <summary>
/// A discovered HTTP endpoint that can be invoked from the API explorer panel.
/// </summary>
public sealed class EndpointInfo
{
    /// <summary>
    /// Stable id of the endpoint within the explorer (per page load).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// HTTP method (GET, POST, PUT, PATCH, DELETE, ...).
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Absolute route path, potentially containing <c>{parameter}</c> placeholders.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Route parameter names extracted from the path, in order.
    /// </summary>
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();
}