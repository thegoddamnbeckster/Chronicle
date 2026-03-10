namespace Chronicle.Services.Plugins;

/// <summary>
/// Result of a plugin health check, enriched with an optional failure reason and severity.
/// </summary>
/// <param name="Healthy">Whether the plugin passed its health check.</param>
/// <param name="FailureReason">
/// Human-readable explanation of the failure. Null when healthy.
/// </param>
/// <param name="IsCritical">
/// <c>true</c> — unexpected error (network failure, unhandled exception) → red badge.
/// <c>false</c> — configuration issue (missing/invalid settings) → yellow badge.
/// </param>
public record PluginHealthResult(
    bool Healthy,
    string? FailureReason = null,
    bool IsCritical = true);
