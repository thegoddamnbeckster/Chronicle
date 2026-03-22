namespace Chronicle.API.DTOs;

public record EnrichmentStatsDto(
    string PluginId,
    string PluginName,
    int Pending,
    int Completed,
    int Failed,
    int Exhausted,
    int NotFound,
    int Skipped);

public record ResetEnrichmentDto(string Scope, int? MediaItemId);
// Scope values: "single", "exhausted", "all"
